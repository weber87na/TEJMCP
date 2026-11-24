using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace TejMcp;

public sealed class TejQueryOptions
{
    [Description("指定單日或多日，格式 YYYY-MM-DD。月、季、半年用資料所屬月份，年資料用年份。")]
    public string[]? Dates { get; init; }
    [Description("最近 1–100 期。預設每個代碼各取最近期。")]
    public int? Latest { get; init; }
    [Description("搭配 latest。true 先取整批代碼的最新日期，再於同組日期顯示各公司資料。")]
    public bool Aligned { get; init; }
    [Description("true 由最近日期開始，false 由最早日期開始。")]
    public bool Descending { get; init; } = true;
    [Description("彙整頻率：week、month、quarter、year。省略時保留原始資料。")]
    public string? Period { get; init; }
    [Description("搭配 period：last 取期內最後一列，first 取第一列，mean 或 sum 彙總數值欄位。")]
    public string Aggregation { get; init; } = "last";
}

public sealed record TejSource(
    [property: Description("TEJ 資料表代碼。")] string TableId,
    [property: Description("選取的欄位代碼及順序。")] string[] Fields,
    [property: Description("彙整時使用 first、last、mean 或 sum。")] string Aggregation = "last");

public sealed record TejReportDefinition(
    [property: Description("依輸出順序列出 1–8 個資料來源。")] TejSource[] Sources,
    [property: Description("公司或資料序列代碼。與 watchlistName 擇一。")] string[]? Codes = null,
    [property: Description("本機觀察清單名稱。")] string? WatchlistName = null,
    [property: Description("起始日期 YYYY-MM-DD。跨表報表需填寫，欄位群組可省略。")] string? StartDate = null,
    [property: Description("結束日期 YYYY-MM-DD，省略採台北今天。")] string? EndDate = null,
    [property: Description("兩碼市場代碼，預設 TW。")] string Country = "TW",
    [property: Description("對齊週期：exact、week、month、quarter、year，預設 exact。")] string Period = "exact",
    [property: Description("true 時從最新期間往前排列。")] bool Descending = false,
    [property: Description("以台灣產業名稱或代碼，每次重跑時重新查找公司。與 codes、watchlistName 擇一。")] string? Industry = null,
    [property: Description("與 industry 搭配，依上市別代碼或關鍵字縮小樣本。")] string? Market = null);

public sealed record TejMatrix(TejField[] Columns, TejField[] Fields, object?[][] Rows, string[] MissingCodes);
internal sealed record TejTextFilter(string[] Fields, string Term, bool IncludeCompanyShortName = false);
internal sealed record TejSelection(DateTime[]? Dates, int? Latest, bool Aligned, bool Descending,
    bool AllowAllCodes = false, TejTextFilter[]? TextFilters = null, string? DateField = null)
{
    internal TejTextFilter[] Filters => TextFilters ?? [];
}

internal sealed class TejReports(TejClient client)
{
    internal TejResult Query(string table, string[] codes, string? start, string? end, string[]? fields,
        string country, TejQueryOptions? options, int limit)
    {
        TejClient.CheckLimit(limit, 1000);
        options ??= new TejQueryOptions();
        if (options.Dates?.Length > 100)
            throw new TejException("每次最多指定 100 個日期。");
        if (options.Latest is < 1 or > 100 || (options.Latest != null && options.Dates?.Length > 0) ||
            (options.Aligned && options.Latest == null))
            throw new TejException("最近期須為 1 到 100，與指定日期擇一。齊頭設定適用於最近期。");
        if (options.Period == null)
            return client.QuerySelection(table, codes, start, end, fields, country, limit,
                new TejSelection(options.Dates?.Select(TejClient.ParseDate).ToArray(), options.Latest, options.Aligned, options.Descending));

        if (start == null || options.Latest != null || options.Dates?.Length > 0)
            throw new TejException("週期彙整需指定起日，與最近期及指定日期擇一。");
        ValidatePeriod(options.Period, allowExact: false);
        ValidateAggregation(options.Aggregation);
        var input = client.QuerySelection(table, codes, start, end ?? Today(), fields, country, 10_000,
            new TejSelection(null, null, false, false));
        var series = Fold(input, options.Period, options.Aggregation);
        var rows = Order(series, options.Descending)
            .Select(item => new object?[] { item.Key.Code, item.Key.Period }.Concat(item.Value).ToArray());
        return LimitRows(input with { Columns = PeriodColumns(input.Columns, options.Period) }, rows, limit);
    }

    internal TejResult Companies(string? keyword, string? industry, string? market, int limit)
    {
        TejClient.CheckLimit(limit, 200);
        var filters = new List<TejTextFilter>();
        if (!string.IsNullOrWhiteSpace(keyword))
            filters.Add(new TejTextFilter(["COID", "NAME_F_CHI", "NAME_F_ENG"], keyword!, IncludeCompanyShortName: true));
        if (!string.IsNullOrWhiteSpace(industry))
            filters.Add(new TejTextFilter(["IND", "TEJIND"], industry!));
        if (!string.IsNullOrWhiteSpace(market))
            filters.Add(new TejTextFilter(["MKT"], market!));
        return client.QuerySelection("CRMSTD_S", null, null, null,
            ["NAME_F_CHI", "NAME_F_ENG", "IND", "TEJIND", "MKT", "LIST_DAY1", "LIST_DAY2"], "TW", limit,
            new TejSelection(null, null, false, false, true, filters.ToArray()));
    }

    internal TejResult Events(string dataset, string[]? codes, string start, string end,
        string? keyword, bool includeBody, string? dateBasis, int limit)
    {
        TejClient.CheckLimit(limit, 1000);
        if (dateBasis != null && dataset != "special")
            throw new TejException("公告日或事件日的選擇適用於 special 事件來源。");
        (string table, string[] fields, string[] searchable, string? dateField) = dataset switch
        {
            "news" => ("WNEWS", includeBody ? new[] { "NEWSTXT_1", "NEWSTXT_2" } : new[] { "NEWSTXT_1" },
                includeBody ? new[] { "NEWSTXT_1", "NEWSTXT_2" } : new[] { "NEWSTXT_1" }, (string?)null),
            "market_memo" => ("WMEMO", new[] { "CONTENT" }, new[] { "CONTENT" }, (string?)null),
            "special" => ("WMKTEVT", new[] { "COIDNM", "EVT_DATE", "ANN_DATE", "EMT_DATE", "RMK" }, new[] { "COIDNM", "RMK" },
                dateBasis switch
                {
                    null or "announcement" => "ANN_DATE",
                    "occurrence" => "EVT_DATE",
                    _ => throw new TejException("特定事件須以 announcement 公告日或 occurrence 事件日查詢。"),
                }),
            _ => throw new TejException("事件來源須為 news、market_memo 或 special。"),
        };
        var filters = string.IsNullOrWhiteSpace(keyword) ? [] : new[] { new TejTextFilter(searchable, keyword!) };
        return client.QuerySelection(table, codes, start, end, fields, "TW", limit,
            new TejSelection(null, null, false, true, true, filters, dateField));
    }

    internal const int MaximumPanelCodes = 200;

    internal TejResult Panel(TejReportDefinition report, string[] codes, int limit)
    {
        TejClient.CheckLimit(limit, 10_000);
        ValidateDefinition(report);
        if (report.StartDate == null)
            throw new TejException("跨資料表查詢需指定起始日期。");
        codes = TejClient.CompanyCodes(codes, MaximumPanelCodes);
        var batches = codes.Select((code, index) => (code, index))
            .GroupBy(item => item.index / 30).Select(group => group.Select(item => item.code).ToArray()).ToArray();
        var end = report.EndDate ?? Today();
        var values = new Dictionary<(string Code, string Period), object?[]>();
        var columns = new List<TejField>();
        var totalFields = report.Sources.Sum(source => source.Fields.Length);
        foreach (var source in report.Sources)
        {
            var selection = new TejSelection(null, null, false, false);
            var input = client.QuerySelection(source.TableId, batches[0], report.StartDate, end, source.Fields,
                report.Country, 10_000, selection);
            var fields = input.Columns.Skip(2).ToArray();
            var sourceRows = 0;
            for (var batch = 0; batch < batches.Length; batch++)
            {
                if (batch > 0)
                    input = client.QuerySelection(source.TableId, batches[batch], report.StartDate, end, source.Fields,
                        report.Country, Math.Max(1, 10_000 - sourceRows), selection);
                sourceRows += input.Rows.Length;
                if (sourceRows > 10_000)
                    throw new TejException("來源資料超過查詢上限。請縮短日期或減少公司後重試。");
                foreach (var item in Fold(input, report.Period, source.Aggregation))
                {
                    if (!values.TryGetValue(item.Key, out var row))
                        values[item.Key] = row = new object?[totalFields];
                    item.Value.CopyTo(row, columns.Count);
                }
            }
            columns.AddRange(fields.Select(field => field with { Code = input.TableId + "." + field.Code }));
        }

        TejField[] keys = [new("COID", "公司代碼", "CHAR", null, true), new("PERIOD", "期間", "DATE", null, true)];
        var result = new TejResult("PANEL", "TEJ 跨表資料", report.Country.Trim().ToUpperInvariant(),
            keys.Concat(columns).ToArray(), [], false);
        var rows = Order(values, report.Descending)
            .Select(item => new object?[] { item.Key.Code, item.Key.Period }.Concat(item.Value).ToArray());
        return LimitRows(result, rows, limit);
    }

    internal TejMatrix Matrix(string table, string[] codes, string[] fields, string[] dates, string country,
        string? rankField, bool descending)
    {
        codes = TejClient.CompanyCodes(codes);
        if (dates == null || dates.Length is < 1 or > 100)
            throw new TejException("請指定 1 到 100 個日期。");
        if (codes.Length > 1 && dates.Distinct().Count() > 1)
            throw new TejException("請選擇單一公司跨期，或同一日期跨公司。");

        var data = Query(table, codes, null, null, fields, country, new TejQueryOptions { Dates = dates, Descending = descending }, 1000);
        EnsureSeries(data);
        var variables = data.Columns.Skip(2).ToArray();
        var observations = data.Rows;
        if (rankField != null)
        {
            if (dates.Distinct().Count() != 1)
                throw new TejException("排名需指定單一日期。");
            var index = Array.FindIndex(data.Columns, column => !column.IsKey &&
                column.Code.Equals(rankField.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !IsNumeric(data.Columns[index]))
                throw new TejException("排名欄位須是已選取的數值欄位。");
            observations = observations.OrderBy(row => Number(row[index]) == null)
                .ThenBy(row => Number(row[index]) is { } value && descending ? -value : Number(row[index]))
                .ThenBy(row => Text(row[0]), StringComparer.Ordinal).ToArray();
        }

        var key = dates.Distinct().Count() == 1 ? 0 : 1;
        var columns = new[] { new TejField("FIELD", "欄位", "CHAR", null, true) }
            .Concat(observations.Select(row => new TejField(Text(row[key]), Text(row[key]), "VALUE", null, false))).ToArray();
        var rows = variables.Select((field, index) =>
            new object?[] { field.Code }.Concat(observations.Select(row => row[index + 2])).ToArray()).ToArray();
        return new TejMatrix(columns, variables, rows, codes.Except(data.Rows.Select(row => Text(row[0]))).ToArray());
    }

    internal static void ValidateDefinition(TejReportDefinition report)
    {
        if (report == null || report.Sources == null || report.Sources.Length is < 1 or > 8 ||
            report.Sources.Any(source => source == null || string.IsNullOrWhiteSpace(source.TableId) ||
                source.Fields == null || source.Fields.Length is < 1 or > 30 ||
                source.Fields.Any(field => string.IsNullOrWhiteSpace(field) || field.Trim().ToUpperInvariant() is "COID" or "MDATE") ||
                source.Fields.Select(field => field.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Fields.Length))
            throw new TejException("請指定 1 到 8 張表及每表 1 到 30 個資料欄位。COID、MDATE 會自動加入。");
        if (report.Sources.Select(source => source.TableId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != report.Sources.Length)
            throw new TejException("同一張表請合併為一個資料來源。");
        var samples = new object?[] { report.Codes, report.WatchlistName, report.Industry };
        if (report.Country == null || samples.Count(sample => sample != null) > 1)
            throw new TejException("請提供市場，並在證券代碼、清單名稱與產業中擇一。");
        if ((report.Industry != null && (string.IsNullOrWhiteSpace(report.Industry) ||
            !report.Country.Equals("TW", StringComparison.OrdinalIgnoreCase))) || (report.Market != null && report.Industry == null))
            throw new TejException("動態產業樣本適用於台灣市場，上市別需搭配產業。");
        if (report.Codes != null)
            TejClient.CompanyCodes(report.Codes, MaximumPanelCodes);
        if (report.StartDate != null)
            TejClient.ParseDate(report.StartDate);
        if (report.EndDate != null)
            TejClient.ParseDate(report.EndDate);
        ValidatePeriod(report.Period, allowExact: true);
        foreach (var source in report.Sources)
        {
            ValidateAggregation(source.Aggregation);
            if (report.Period == "exact" && source.Aggregation != "last")
                throw new TejException("請指定彙整週期以使用 first、mean 或 sum。");
        }
    }

    private static Dictionary<(string Code, string Period), object?[]> Fold(TejResult data, string period, string aggregation)
    {
        EnsureSeries(data);
        var fields = data.Columns.Skip(2).ToArray();
        if (aggregation is "mean" or "sum" && fields.Any(field => !IsNumeric(field)))
            throw new TejException("平均或加總只適用於數值欄位。");
        var groups = data.Rows.GroupBy(row => (Code: Text(row[0]), Period: PeriodLabel(row[1], period)));
        return groups.ToDictionary(group => group.Key, group => aggregation switch
        {
            "first" => group.First().Skip(2).ToArray(),
            "last" => group.Last().Skip(2).ToArray(),
            _ => fields.Select((field, index) => Aggregate(group.Select(row => Number(row[index + 2])), aggregation)).ToArray(),
        });
    }

    private static void EnsureSeries(TejResult data)
    {
        if (data.Truncated)
            throw new TejException("來源資料超過查詢上限。請縮短日期或減少公司後重試。");
        if (!data.Columns.Where(column => column.IsKey).Select(column => column.Code).SequenceEqual(new[] { "COID", "MDATE" }))
            throw new TejException("此作業需要以公司代碼及日期為主鍵的資料表。");
    }

    private static string PeriodLabel(object? value, string period)
    {
        var source = Text(value);
        var date = ParsePeriod(source);
        if (period == "exact")
            return source.Length switch { 4 => date.ToString("yyyy", CultureInfo.InvariantCulture),
                6 or 7 => date.ToString("yyyy-MM", CultureInfo.InvariantCulture), _ => TejWorkflows.IsoDate(date) };
        if (period == "week")
            return TejWorkflows.IsoDate(date.AddDays(-((int)date.DayOfWeek + 6) % 7));
        if (period == "year")
            return date.ToString("yyyy", CultureInfo.InvariantCulture);
        return date.ToString("yyyy-", CultureInfo.InvariantCulture) + (period == "quarter"
            ? "Q" + ((date.Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture)
            : date.ToString("MM", CultureInfo.InvariantCulture));
    }

    private static DateTime ParsePeriod(string value)
    {
        if (TejClient.DataDate(value) is { } date)
            return date;
        return DateTime.TryParseExact(value, ["yyyyMM", "yyyy-MM", "yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : throw new TejException("TEJ 回傳無法辨識的日期。");
    }

    private static TejField[] PeriodColumns(TejField[] columns, string period) =>
        new[] { columns[0], new TejField("PERIOD", period + " 期間", "CHAR", null, true) }.Concat(columns.Skip(2)).ToArray();

    private static IEnumerable<KeyValuePair<(string Code, string Period), object?[]>> Order(
        Dictionary<(string Code, string Period), object?[]> rows, bool descending) =>
        (descending ? rows.OrderByDescending(row => row.Key.Period, StringComparer.Ordinal)
            : rows.OrderBy(row => row.Key.Period, StringComparer.Ordinal)).ThenBy(row => row.Key.Code, StringComparer.Ordinal);

    private static TejResult LimitRows(TejResult result, IEnumerable<object?[]> rows, int limit)
    {
        var limited = rows.Take(limit + 1).ToArray();
        return result with { Rows = limited.Take(limit).ToArray(), Truncated = limited.Length > limit };
    }

    private static object? Aggregate(IEnumerable<decimal?> numbers, string aggregation)
    {
        var available = numbers.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : aggregation == "mean" ? available.Average() : available.Sum();
    }

    private static void ValidatePeriod(string period, bool allowExact)
    {
        if (period is not ("week" or "month" or "quarter" or "year") && !(allowExact && period == "exact"))
            throw new TejException("請提供有效的對齊週期。");
    }

    private static void ValidateAggregation(string aggregation)
    {
        if (aggregation is not ("first" or "last" or "mean" or "sum"))
            throw new TejException("彙整方法須為 first、last、mean 或 sum。");
    }

    internal static bool IsNumeric(TejField field) => field.Type.ToUpperInvariant() is
        "DOUBLE" or "FLOAT" or "LONG" or "INT" or "INTEGER" or "DECIMAL" or "NUMERIC" or "REAL" or "NUMBER" or "SMALLINT" or "BIGINT";
    internal static decimal? Number(object? value) => decimal.TryParse(Text(value), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) ? number : null;
    internal static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
    private static string Today() => TejWorkflows.IsoDate(TejClient.TaipeiToday());
}
