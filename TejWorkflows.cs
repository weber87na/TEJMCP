using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TejMcp;

public sealed record PricePerformance(string Code, decimal FirstClose, decimal LastClose,
    decimal PriceChangePct, decimal MaxDrawdownPct, decimal? RelativeToBenchmarkPp);

public sealed record PriceComparison(string TableId, string FirstTradingDate, string LastTradingDate,
    int CommonTradingDays, string? BenchmarkCode, PricePerformance[] Performance, string[] MissingCodes);

public sealed record InstitutionalFlowTotal(string Code, int TradingDays, decimal? ForeignNetThousandShares,
    decimal? FundNetThousandShares, decimal? DealerNetThousandShares, decimal? TotalNetThousandShares);

public sealed record InstitutionalFlows(TejResult Daily, InstitutionalFlowTotal[] Totals, string[] MissingCodes);

public sealed record ValuationSnapshot(string RequestedDate, string? TradingDate, TejResult Valuation, string[] MissingCodes);

public sealed record PostMarketStock(string Code, decimal Close, decimal? PriceChange, decimal? DailyReturnPct,
    decimal? VolumeThousandShares, int PriorVolumeSessions, decimal? Prior20AverageVolumeThousandShares,
    decimal? VolumeToPrior20Average, decimal? ForeignNetThousandShares, decimal? FundNetThousandShares,
    decimal? DealerNetThousandShares, decimal? TotalNetThousandShares);

public sealed record PostMarketSummary(string RequestedDate, string? TradingDate, string? WatchlistName,
    string PriceTableId, string FlowTableId, PostMarketStock[] Stocks, string[] MissingPriceCodes,
    string[] MissingInstitutionalCodes);

public sealed record StockScreenAssessment(string Code, string? RevenueMonth, string? RevenueAnnouncementDate,
    decimal? RevenueYoYPct, decimal? ForeignNetThousandShares, int ForeignSessionsAvailable, string[] MissingForeignDates,
    bool? MeetsRevenue, bool? MeetsForeign, int? Rank = null);

public sealed record StockScreenResult(string AsOfDate, string? WatchlistName, string RevenueTableId, string FlowTableId,
    decimal RevenueYoYAbove, decimal ForeignNetAbove, int ForeignTradingDays, string SortBy, string[] ForeignTradingDates,
    StockScreenAssessment[] Matches, StockScreenAssessment[] Excluded);

internal sealed class TejWorkflows(TejClient client)
{
    private const string AdjustedDailyPrices = "WAPRCD1";
    private const string DailyPrices = "WPRCD";
    private const string MonthlyRevenueTable = "WSALE";
    private const string InstitutionalFlowTable = "WTINST1";
    private const int VolumeWindow = 20;
    private static readonly TimeSpan LatestRegularClose = new(13, 33, 0);
    private static readonly string[] PriceFields = ["OPEN", "HIGH", "LOW", "CLOSE", "VOLUME", "AMOUNT"];
    private static readonly string[] PostMarketPriceFields = ["CLOSE", "CLSCHG", "ROI", "VOLUME"];
    private static readonly string[] RevenueFields = ["ANND_S", "D0001", "D0002", "D0003", "D0004", "D0005", "D0007"];
    private static readonly string[] FinancialFields = ["0003", "CURR", "3100", "3990", "R105", "R106", "R108", "R103"];
    private static readonly string[] FlowFields = ["QFII_Ex1", "Fund_Ex", "DLR_Ex", "TTL_EX"];
    private static readonly string[] TesgFields = ["ANN_DATE", "TESG_LVL", "TESGC", "TESG_CHG", "N_ENV", "N_SOCIAL", "N_CG"];
    private static readonly string[] ValuationFields = ["CLOSE", "MV", "PER-TEJ", "PBR-TEJ", "DIV_YID"];
    private static readonly string[] RevenueMonthFormats = ["yyyyMM", "yyyy-MM", "yyyy/MM"];

    private sealed record DailyBar(string Code, DateTime Date, decimal? Close, decimal? PriceChange,
        decimal? DailyReturnPct, decimal? Volume);

    internal TejResult Series(string report, string[] codes, string startDate, string endDate, int limit)
    {
        TejClient.CheckLimit(limit, 1000);
        return ReadReport(report, codes, startDate, endDate, limit);
    }

    internal TejResult ReadReport(string report, string[] codes, string startDate, string endDate, int limit)
    {
        var (table, fields) = report switch
        {
            "daily_prices" => (DailyPrices, PriceFields),
            "adjusted_prices" => (AdjustedDailyPrices, PriceFields),
            "monthly_revenue" => (MonthlyRevenueTable, RevenueFields),
            "quarterly_financials" => ("WIM1AQ", FinancialFields),
            "institutional_flows" => (InstitutionalFlowTable, FlowFields),
            "tesg_ratings" => ("WTESG", TesgFields),
            "valuation" => (DailyPrices, ValuationFields),
            _ => throw new TejException("找不到指定的台股報表類型。"),
        };

        return client.Query(table, codes, startDate, endDate, fields, "TW", limit);
    }

    internal PriceComparison ComparePrices(string[] codes, string startDate, string endDate, string? benchmarkCode)
    {
        var companies = TejClient.CompanyCodes(codes);
        var benchmark = benchmarkCode is null ? null : TejClient.CompanyCode(benchmarkCode);
        var requested = benchmark is null ? companies : companies.Concat(new[] { benchmark }).Distinct().ToArray();
        var data = client.Query(AdjustedDailyPrices, requested, startDate, endDate, ["CLOSE"], "TW", 10_000);
        RequireComplete(data);

        var prices = data.Rows.Select(row => (Code: RowCode(row), Date: TradingDate(row), Close: Number(row[2])))
            .Where(row => row.Close > 0)
            .GroupBy(row => row.Code).ToDictionary(group => group.Key,
                group => group.ToDictionary(row => row.Date, row => row.Close!.Value));
        if (!companies.Any(prices.ContainsKey))
            throw new TejException("指定標的在這段期間沒有可比較的調整收盤價。");

        var commonDates = new HashSet<DateTime>(prices.Values.First().Keys);
        foreach (var series in prices.Values.Skip(1))
            commonDates.IntersectWith(series.Keys);
        var dates = commonDates.OrderBy(date => date).ToArray();
        if (dates.Length < 2)
            throw new TejException("有資料的標的至少要有兩個共通交易日，才能比較區間表現。");

        var first = dates[0];
        var last = dates[dates.Length - 1];
        decimal? benchmarkChange = benchmark != null && prices.TryGetValue(benchmark, out var benchmarkPrices)
            ? Change(benchmarkPrices[first], benchmarkPrices[last]) : null;

        var performance = prices.Select(pair =>
        {
            var series = pair.Value;
            var change = Change(series[first], series[last]);
            return new PricePerformance(pair.Key, series[first], series[last], RoundPercent(change),
                RoundPercent(MaxDrawdown(dates.Select(date => series[date]))),
                benchmarkChange is { } reference ? RoundPercent(change - reference) : null);
        }).OrderByDescending(row => row.PriceChangePct).ThenBy(row => row.Code, StringComparer.Ordinal).ToArray();

        return new PriceComparison(data.TableId, IsoDate(first), IsoDate(last), dates.Length, benchmark,
            performance, requested.Where(code => !prices.ContainsKey(code)).ToArray());
    }

    internal InstitutionalFlows Flows(string[] codes, string startDate, string endDate, int limit)
    {
        var data = Series("institutional_flows", codes, startDate, endDate, limit);
        RequireComplete(data);
        var totals = data.Rows.GroupBy(RowCode).Select(group =>
        {
            var rows = group.ToArray();
            return new InstitutionalFlowTotal(group.Key, rows.Length, Total(rows, 2), Total(rows, 3),
                Total(rows, 4), Total(rows, 5));
        }).ToArray();
        return new InstitutionalFlows(data, totals, Missing(codes, data.Rows));
    }

    internal ValuationSnapshot Valuation(string[] codes, string asOfDate)
    {
        var requested = TejClient.ParseDate(asOfDate);
        var first = requested.AddDays(-Math.Min(14, (requested - DateTime.MinValue).Days));
        var data = ReadReport("valuation", codes, IsoDate(first), IsoDate(requested), 1000);
        var datedRows = data.Rows.Select(row => (Row: row, Date: TradingDate(row)))
            .Where(row => row.Date <= requested).ToArray();
        if (datedRows.Length == 0)
            return new ValuationSnapshot(IsoDate(requested), null, data with { Rows = [] }, TejClient.CompanyCodes(codes));

        var latest = datedRows.Max(row => row.Date);
        var snapshot = datedRows.Where(row => row.Date == latest).Select(row => row.Row).ToArray();
        return new ValuationSnapshot(IsoDate(requested), IsoDate(latest), data with { Rows = snapshot }, Missing(codes, snapshot));
    }

    internal PostMarketSummary PostMarket(string[] codes, string? asOfDate, string? watchlistName)
    {
        var companies = TejClient.CompanyCodes(codes);
        var now = TejClient.TaipeiNow();
        var requested = asOfDate is null ? now.Date : TejClient.ParseDate(asOfDate);
        var latestCompleted = LatestCompletedDate(now);
        var end = requested <= latestCompleted ? requested : latestCompleted;
        var start = Lookback(end, 90);
        var data = client.Query(DailyPrices, companies, IsoDate(start), IsoDate(end), PostMarketPriceFields, "TW", 10_000);
        RequireComplete(data);

        var bars = data.Rows.Select(row => new DailyBar(RowCode(row), TradingDate(row), Number(row[2]), Number(row[3]),
                Number(row[4]), Number(row[5])))
            .Where(bar => bar.Close > 0 && bar.Date <= end)
            .GroupBy(bar => bar.Code)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(bar => bar.Date).ToArray());
        if (bars.Count == 0)
            return new PostMarketSummary(IsoDate(requested), null, watchlistName, DailyPrices, InstitutionalFlowTable,
                [], companies, []);

        var latestDate = bars.Values.Max(series => series[0].Date);
        var tradingDate = IsoDate(latestDate);
        var activeCodes = companies.Where(code => bars.TryGetValue(code, out var series) && series[0].Date == latestDate).ToArray();
        var flow = ReadReport("institutional_flows", activeCodes, tradingDate, tradingDate, 1000);
        var flowByCode = flow.Rows.ToDictionary(RowCode);
        var stocks = activeCodes.Select(code =>
        {
            flowByCode.TryGetValue(code, out var row);
            return SummarizeStock(bars[code], row);
        }).ToArray();
        var missingFlow = stocks.Where(stock => stock.ForeignNetThousandShares is null && stock.FundNetThousandShares is null &&
            stock.DealerNetThousandShares is null && stock.TotalNetThousandShares is null).Select(stock => stock.Code).ToArray();

        return new PostMarketSummary(IsoDate(requested), tradingDate, watchlistName, DailyPrices, InstitutionalFlowTable,
            stocks, companies.Except(activeCodes).ToArray(), missingFlow);
    }

    internal StockScreenResult ScreenStocks(string[] codes, string? watchlistName, string? asOfDate, decimal revenueYoYAbove,
        decimal foreignNetAbove, int foreignTradingDays, string sortBy)
    {
        var companies = TejClient.CompanyCodes(codes);
        TejClient.CheckLimit(foreignTradingDays, 60);
        if (sortBy is not ("revenue_yoy" or "foreign_net"))
            throw new TejException("排序欄位須為 revenue_yoy 或 foreign_net。");

        var now = TejClient.TaipeiNow();
        var requested = asOfDate is null ? now.Date : TejClient.ParseDate(asOfDate);
        var asOf = requested <= now.Date ? requested : now.Date;
        var completed = LatestCompletedDate(now);
        var foreignEnd = asOf <= completed ? asOf : completed;
        var foreignStart = Lookback(foreignEnd, Math.Max(30, foreignTradingDays * 3));
        var calendar = client.TradingDays("TW", foreignStart, foreignEnd).ToHashSet();

        var revenue = client.Query(MonthlyRevenueTable, companies, IsoDate(Lookback(asOf, 3660)), IsoDate(asOf),
            ["ANND_S", "D0003"], "TW", 10_000);
        RequireComplete(revenue);
        var latestRevenue = revenue.Rows.Select(row => (Row: row, AnnouncedOn: TejClient.DataDate(row[2])))
            .Where(item => item.AnnouncedOn.HasValue && item.AnnouncedOn.Value <= asOf)
            .Select(item => (Code: RowCode(item.Row), Month: RevenueMonth(item.Row[1]),
                AnnouncedOn: item.AnnouncedOn!.Value, YoY: Number(item.Row[3])))
            .GroupBy(item => item.Code).ToDictionary(group => group.Key, group => group
                .OrderByDescending(item => item.Month).ThenByDescending(item => item.AnnouncedOn).First());

        var foreign = client.Query(InstitutionalFlowTable, companies, IsoDate(foreignStart), IsoDate(foreignEnd),
            [FlowFields[0]], "TW", 10_000);
        RequireComplete(foreign);
        var trades = foreign.Rows.Select(row => (Code: RowCode(row), Date: TradingDate(row), Net: Number(row[2])))
            .Where(row => row.Net.HasValue && calendar.Contains(row.Date)).ToArray();
        var publishedThrough = trades.Select(row => (DateTime?)row.Date).Max();
        var days = calendar.Where(day => day <= publishedThrough).OrderByDescending(day => day)
            .Take(foreignTradingDays).OrderBy(day => day).ToArray();
        var foreignByCode = trades.GroupBy(row => row.Code).ToDictionary(group => group.Key,
            group => group.ToDictionary(row => row.Date, row => row.Net!.Value));

        var assessed = companies.Select(code =>
        {
            var hasRevenue = latestRevenue.TryGetValue(code, out var monthly);
            foreignByCode.TryGetValue(code, out var observations);
            var missingDates = days.Where(day => observations?.ContainsKey(day) != true).ToArray();
            decimal? net = days.Length == foreignTradingDays && missingDates.Length == 0
                ? days.Sum(day => observations![day]) : null;
            return new StockScreenAssessment(code,
                hasRevenue ? monthly.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null,
                hasRevenue ? IsoDate(monthly.AnnouncedOn) : null, monthly.YoY, net, days.Length - missingDates.Length,
                missingDates.Select(IsoDate).ToArray(), Above(monthly.YoY, revenueYoYAbove), Above(net, foreignNetAbove));
        }).ToArray();

        var matched = assessed.Where(row => row.MeetsRevenue == true && row.MeetsForeign == true)
            .OrderByDescending(row => sortBy == "revenue_yoy" ? row.RevenueYoYPct : row.ForeignNetThousandShares)
            .ThenBy(row => row.Code, StringComparer.Ordinal)
            .Select((row, index) => row with { Rank = index + 1 }).ToArray();
        var excluded = assessed.Where(row => row.MeetsRevenue != true || row.MeetsForeign != true).ToArray();

        return new StockScreenResult(IsoDate(asOf), watchlistName, MonthlyRevenueTable, InstitutionalFlowTable, revenueYoYAbove,
            foreignNetAbove, foreignTradingDays, sortBy, days.Select(IsoDate).ToArray(), matched, excluded);
    }

    private static PostMarketStock SummarizeStock(DailyBar[] series, object?[]? flow)
    {
        var current = series[0];
        var previousVolumes = series.Skip(1).Take(VolumeWindow).Select(bar => bar.Volume)
            .Where(volume => volume >= 0).Select(volume => volume!.Value).ToArray();
        decimal? average = previousVolumes.Length == VolumeWindow ? previousVolumes.Average() : null;
        decimal? ratio = current.Volume is >= 0 && average is > 0
            ? Math.Round(current.Volume.Value / average.Value, 4, MidpointRounding.AwayFromZero) : null;

        return new PostMarketStock(current.Code, current.Close!.Value, current.PriceChange, current.DailyReturnPct,
            current.Volume, previousVolumes.Length, average, ratio, Number(flow?[2]), Number(flow?[3]), Number(flow?[4]),
            Number(flow?[5]));
    }

    private static decimal Change(decimal first, decimal last) => (last / first - 1m) * 100m;

    private static decimal MaxDrawdown(IEnumerable<decimal> prices)
    {
        var peak = 0m;
        var maximum = 0m;
        foreach (var price in prices)
        {
            peak = Math.Max(peak, price);
            maximum = Math.Max(maximum, (peak - price) / peak * 100m);
        }
        return maximum;
    }

    private static decimal RoundPercent(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static decimal? Total(object?[][] rows, int column)
    {
        var values = rows.Select(row => Number(row[column])).ToArray();
        return values.All(value => value.HasValue) ? values.Sum() : null;
    }

    private static decimal? Number(object? value) => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string RowCode(object?[] row) => TejClient.CompanyCode(Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? "");

    private static DateTime TradingDate(object?[] row) => TejClient.DataDate(row[1])
        ?? throw new TejException("TEJ 回傳的交易日格式不正確。");

    private static DateTime RevenueMonth(object? value) => DateTime.TryParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(),
        RevenueMonthFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
        ? month : throw new TejException("TEJ 回傳的營收月份格式不正確。");

    private static bool? Above(decimal? value, decimal threshold) => value is { } number ? number > threshold : null;

    // A TWSE closing auction delayed by the exchange may finish at 13:33.
    private static DateTime LatestCompletedDate(DateTimeOffset taipeiNow) =>
        taipeiNow.TimeOfDay < LatestRegularClose ? taipeiNow.Date.AddDays(-1) : taipeiNow.Date;

    private static DateTime Lookback(DateTime end, int days) => end.AddDays(-Math.Min(days, (end - DateTime.MinValue).Days));

    private static string IsoDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Missing(string[] codes, object?[][] rows)
    {
        var available = rows.Select(RowCode).ToHashSet(StringComparer.Ordinal);
        return TejClient.CompanyCodes(codes).Where(code => !available.Contains(code)).ToArray();
    }

    private static void RequireComplete(TejResult data)
    {
        if (data.Truncated)
            throw new TejException("查詢已超過筆數上限。請縮短期間或減少證券代碼，再計算區間結果。");
    }
}
