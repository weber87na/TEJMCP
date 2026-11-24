using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace TejMcp;

public sealed record TejEvent(
    [property: Description("台股證券代碼。")] string Code,
    [property: Description("指定的事件日 YYYY-MM-DD。")] string Date);

public sealed record TejEventOptions(
    [property: Description("mean_adjusted、market_adjusted 或 market_ols。")] string Model = "market_ols",
    [property: Description("比較指數或證券代碼。市場模式預設使用加權指數 Y9999。")] string? BenchmarkCode = null,
    [property: Description("simple、log 或 turnover。")] string ReturnType = "simple",
    [property: Description("估計期起點，依所選頻率偏移。")] int EstimationStart = -120,
    [property: Description("估計期終點，須在事件期之前。")] int EstimationEnd = -11,
    [property: Description("事件期起點，依所選頻率偏移。")] int EventStart = -5,
    [property: Description("事件期終點，依所選頻率偏移。")] int EventEnd = 5,
    [property: Description("估計期最少有效資料筆數。")] int MinimumEstimationObservations = 60,
    [property: Description("任一事件日個股缺值時，drop 排除樣本，zero 將該日個股值設為 0。基準缺值會排除。")] string Missing = "drop",
    [property: Description("非交易日以 next 移至下一個交易日，或以 reject 排除。")] string NonTradingDay = "next",
    [property: Description("day、week 或 month。週月採 TEJ 原生表的完整期間。")] string Frequency = "day");

public sealed record TejEventDay(int Offset, string Date, double ObservedPct, double? BenchmarkPct,
    double ExpectedPct, double AbnormalPp, double CumulativeAbnormalPp, bool Imputed);
public sealed record TejEventEstimate(int Observations, double Alpha, double? Beta, double? ResidualStdDev);
public sealed record TejEventResult(string Code, string RequestedDate, string? TradingDate, string? ExcludedReason,
    TejEventEstimate? Estimate, double? CarPp, TejEventDay[] Days);
public sealed record TejEventAverage(int Offset, int N, double AarPp, double CaarPp, double? CrossSectionalT);
public sealed record TejEventStudyResult(string SourceTable, string SourceField, string? BenchmarkCode,
    TejEventOptions Options, TejEventResult[] Events, TejEventAverage[] Averages);

internal sealed class TejEventStudy(TejClient client)
{
    internal TejEventStudyResult Run(TejEvent[] events, TejEventOptions? options)
    {
        options ??= new TejEventOptions();
        if (events == null || events.Length is < 1 or > 30 || events.Any(item => item == null))
            throw new TejException("請指定 1 到 30 組事件，每組含證券代碼與日期。");
        var samples = events.Select(item => (Code: TejClient.CompanyCode(item.Code), Date: TejClient.ParseDate(item.Date)))
            .Distinct().ToArray();
        var source = options.Frequency switch
        {
            "day" => "WAPRCD1",
            "week" => "WAPRCW1",
            "month" => "WAPRCM1",
            _ => throw new TejException("頻率須為 day、week 或 month。"),
        };
        var field = options.ReturnType switch
        {
            "simple" => "ROI",
            "log" => "ROIB",
            "turnover" => "TURNOVER",
            _ => throw new TejException("報酬率類型須為 simple、log 或 turnover。"),
        };
        var benchmark = options.Model switch
        {
            "mean_adjusted" when options.BenchmarkCode == null => null,
            "market_ols" or "market_adjusted" => TejClient.CompanyCode(options.BenchmarkCode ?? "Y9999"),
            _ => throw new TejException("請選擇有效的預期模式。mean_adjusted 省略基準。"),
        };
        if (options.ReturnType == "turnover" && benchmark != null && options.BenchmarkCode == null)
            throw new TejException("週轉率的市場模式需指定具週轉率資料的基準。");
        Validate(options);
        var now = TejClient.TaipeiNow();
        if (samples.Any(item => item.Date > now.Date || item.Date.Year < 1900))
            throw new TejException("事件日須在 1900 年至台北今天之間。");
        var results = samples.Select(item => Analyze(item.Code, item.Date, source, field, benchmark, options, now)).ToArray();
        var averages = results.SelectMany(result => result.Days).GroupBy(day => day.Offset).OrderBy(group => group.Key)
            .Select(group =>
            {
                var abnormal = group.Select(day => day.AbnormalPp).ToArray();
                var mean = abnormal.Average();
                var deviation = StandardDeviation(abnormal, mean, 1);
                double? t = deviation > 0 ? mean / (deviation.Value / Math.Sqrt(abnormal.Length)) : null;
                return new TejEventAverage(group.Key, abnormal.Length, mean, group.Average(day => day.CumulativeAbnormalPp), t);
            }).ToArray();
        return new TejEventStudyResult(source, field, benchmark, options, results, averages);
    }

    internal static TejEvent[] ReadSamples(string text)
    {
        if (text.Length > 3000)
            throw new TejException("樣本文字過長，請提供 1 到 30 列事件。");
        return text.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
            {
                var fields = line.Split(',');
                if (fields.Length != 2 || !DateTime.TryParseExact(fields[1].Trim().Trim('"'), ["yyyyMMdd", "yyyy-MM-dd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new TejException("樣本每一列須包含證券代碼及有效日期，以逗號分隔。");
                return new TejEvent(fields[0].Trim().Trim('"'), TejWorkflows.IsoDate(date));
            }).ToArray();
    }

    private TejEventResult Analyze(string code, DateTime requested, string source, string field, string? benchmark,
        TejEventOptions options, DateTimeOffset now)
    {
        var requestedText = TejWorkflows.IsoDate(requested);
        var todayCutoff = TejWorkflows.LatestCompletedDate(now);
        var offsetStart = options.Model == "market_adjusted" ? options.EventStart : options.EstimationStart;
        var (start, end) = options.Frequency switch
        {
            "week" => (requested.AddDays((offsetStart - 5) * 7), requested.AddDays((options.EventEnd + 5) * 7)),
            "month" => (requested.AddMonths(offsetStart - 2), requested.AddMonths(options.EventEnd + 2)),
            _ => (requested.AddDays(offsetStart * 3 - 30), requested.AddDays(options.EventEnd * 3 + 30)),
        };
        if (end > todayCutoff)
            end = todayCutoff;
        var tradingDays = client.TradingDays("TW", start, end);
        var eventDay = Array.FindIndex(tradingDays, date => date >= requested);
        if (eventDay < 0 || (options.NonTradingDay == "reject" && tradingDays[eventDay] != requested))
            return new TejEventResult(code, requestedText, null, "沒有符合的事件交易日。", null, null, []);

        var dateOfEvent = tradingDays[eventDay];
        var tradingDate = TejWorkflows.IsoDate(dateOfEvent);
        TejEventResult Exclude(string reason, TejEventEstimate? estimate = null) =>
            new(code, requestedText, tradingDate, reason, estimate, null, []);
        if (benchmark == code)
            return Exclude("個股與基準須使用不同的代碼。");
        var daily = options.Frequency == "day";
        var calendar = daily ? tradingDays : tradingDays.GroupBy(date => PeriodStart(date, options.Frequency))
            .Where(group => (options.Frequency == "week" ? group.Key.AddDays(6) : group.Key.AddMonths(1).AddDays(-1)) <= todayCutoff)
            .Select(group => group.Last()).ToArray();
        var anchor = Array.FindIndex(calendar, date => date >= dateOfEvent);
        if (anchor < 0 || anchor + options.EventEnd >= calendar.Length || anchor + options.EventStart < 0)
            return Exclude("事件期的完整期間尚未齊備。");
        var first = Math.Max(0, anchor + offsetStart);
        var last = anchor + options.EventEnd;
        var data = client.QuerySelection(source, benchmark == null ? [code] : [code, benchmark],
            TejWorkflows.IsoDate(calendar[first]), TejWorkflows.IsoDate(calendar[last]),
            daily ? [field] : [field, "END_DATE"], "TW", 10_000, new TejSelection(null, null, false, false));
        if (!data.Rows.Any(row => TejReports.Text(row[0]) == code && TejReports.Number(row[2]) != null))
            return Exclude("查無指定證券的有效資料。");
        var observations = data.Rows.Select(row => (Code: TejReports.Text(row[0]),
                Date: TejClient.DataDate(row[daily ? 1 : 3]), Value: TejReports.Number(row[2])))
            .Where(row => row.Date.HasValue).ToDictionary(row => (row.Code, row.Date!.Value), row => row.Value);
        double? Read(string id, DateTime date) => observations.TryGetValue((id, date), out var value) && value.HasValue
            ? (double)value.Value : null;
        var estimate = Estimate();
        if (estimate == null)
            return Exclude("估計期資料不足或基準在估計期沒有變異。");

        var days = new List<TejEventDay>();
        double car = 0;
        for (var offset = options.EventStart; offset <= options.EventEnd; offset++)
        {
            var date = calendar[anchor + offset];
            var observed = Read(code, date);
            var market = benchmark == null ? null : Read(benchmark, date);
            if ((benchmark != null && market == null) || (observed == null && options.Missing == "drop"))
                return Exclude($"事件期缺少 {TejWorkflows.IsoDate(date)} 的個股或基準資料。", estimate);
            var expected = estimate.Alpha + (estimate.Beta ?? 0) * (market ?? 0);
            var abnormal = (observed ?? 0) - expected;
            car += abnormal;
            days.Add(new TejEventDay(offset, TejWorkflows.IsoDate(date), observed ?? 0, market, expected, abnormal, car, observed == null));
        }
        return new TejEventResult(code, requestedText, tradingDate, null, estimate, car, days.ToArray());

        TejEventEstimate? Estimate()
        {
            if (options.Model == "market_adjusted")
                return new TejEventEstimate(0, 0, 1, null);
            var past = calendar.Skip(first).Take(Math.Max(0, anchor + options.EstimationEnd - first + 1))
                .Select(date => (Y: Read(code, date), X: benchmark == null ? 0d : Read(benchmark, date)))
                .Where(point => point.Y.HasValue && point.X.HasValue)
                .Select(point => (Y: point.Y!.Value, X: point.X!.Value)).ToArray();
            if (past.Length < options.MinimumEstimationObservations)
                return null;
            var yMean = past.Average(point => point.Y);
            if (benchmark == null)
                return new TejEventEstimate(past.Length, yMean, null,
                    StandardDeviation(past.Select(point => point.Y).ToArray(), yMean, 1));
            var xMean = past.Average(point => point.X);
            var xVariance = past.Sum(point => Square(point.X - xMean));
            if (xVariance == 0)
                return null;
            var beta = past.Sum(point => (point.X - xMean) * (point.Y - yMean)) / xVariance;
            var alpha = yMean - beta * xMean;
            var residuals = past.Select(point => point.Y - alpha - beta * point.X).ToArray();
            return new TejEventEstimate(past.Length, alpha, beta, StandardDeviation(residuals, 0, 2));
        }
    }

    private static void Validate(TejEventOptions options)
    {
        if (options.EventStart is < -60 or > 0 || options.EventEnd is < 0 or > 60)
            throw new TejException("事件期須涵蓋第 0 期，前後各最多 60 期。");
        var longestStart = options.Frequency == "month" ? -120 : -500;
        if (options.Model != "market_adjusted" &&
            (options.EstimationStart < longestStart || options.EstimationEnd >= options.EventStart ||
             options.EstimationStart > options.EstimationEnd ||
             options.MinimumEstimationObservations < (options.Model == "market_ols" ? 3 : 2) ||
             options.MinimumEstimationObservations > options.EstimationEnd - options.EstimationStart + 1))
            throw new TejException($"估計期須介於事件前 {-longestStart} 期至事件期之前，並容納所需的最低樣本數。");
        if (options.Missing is not ("drop" or "zero") || options.NonTradingDay is not ("next" or "reject"))
            throw new TejException("請使用有效的缺值與非交易日設定。");
    }

    private static DateTime PeriodStart(DateTime date, string frequency) => frequency == "week"
        ? date.AddDays(-((int)date.DayOfWeek + 6) % 7) : new DateTime(date.Year, date.Month, 1);

    internal static TejResult ExportRows(TejEventStudyResult study, string view)
    {
        TejField Field(string code) => new(code, code, "VALUE", null, false);
        var (codes, rows) = view switch
        {
            "periods" => (new[] { "COID", "EVENT_DATE", "TRADING_DATE", "OFFSET", "PERIOD_END", "OBSERVED_PCT",
                    "BENCHMARK_PCT", "EXPECTED_PCT", "ABNORMAL_PP", "CAR_PP", "IMPUTED" },
                study.Events.SelectMany(item => item.Days.Select(day => new object?[]
                {
                    item.Code, item.RequestedDate, item.TradingDate, day.Offset, day.Date, day.ObservedPct,
                    day.BenchmarkPct, day.ExpectedPct, day.AbnormalPp, day.CumulativeAbnormalPp, day.Imputed,
                }))),
            "estimates" => (new[] { "COID", "EVENT_DATE", "TRADING_DATE", "EXCLUDED_REASON", "N", "ALPHA", "BETA", "RESIDUAL_STD_DEV", "CAR_PP" },
                study.Events.Select(item => new object?[]
                {
                    item.Code, item.RequestedDate, item.TradingDate, item.ExcludedReason, item.Estimate?.Observations,
                    item.Estimate?.Alpha, item.Estimate?.Beta, item.Estimate?.ResidualStdDev, item.CarPp,
                })),
            "averages" => (new[] { "OFFSET", "N", "AAR_PP", "CAAR_PP", "CROSS_SECTIONAL_T" },
                study.Averages.Select(item => new object?[] { item.Offset, item.N, item.AarPp, item.CaarPp, item.CrossSectionalT })),
            _ => throw new TejException("事件研究匯出表須選擇 periods、estimates 或 averages。"),
        };
        return new TejResult(study.SourceTable, "TEJ 事件研究", "TW", codes.Select(Field).ToArray(), rows.ToArray(), false);
    }

    private static double Square(double value) => value * value;
    private static double? StandardDeviation(double[] values, double mean, int fittedParameters) =>
        values.Length <= fittedParameters ? null : Math.Sqrt(values.Sum(value => Square(value - mean)) / (values.Length - fittedParameters));
}
