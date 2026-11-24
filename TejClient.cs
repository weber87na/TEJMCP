using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TEJProClient;

namespace TejMcp;

public sealed record TejTable(string TableId, string Title, string Frequency);
public sealed record TejField(string Code, string Title, string Type, string? Unit, bool IsKey);
public sealed record TejResult(string TableId, string Title, string Country, TejField[] Columns, object?[][] Rows, bool Truncated);
public sealed record TejFieldMatch(string TableId, string TableTitle, string Frequency, TejField Field);

internal sealed class TejClient(TejConfiguration configuration)
{
    private const string Topics = "[AP001].[dbo].[sys_alltopic_資料項目定義]";
    private const string Grants = "[AP001].[dbo].[sys_odtopic]";
    private const string FieldGrants = "[AP001].[dbo].[sys_odcollist]";
    private const string Fields = "[AP001].[dbo].[TableColumn_Info]";
    private const string MarketCodes = "[AP001].[dbo].[sys_odmktlist]";
    private const string CustomCodes = "[AP001].[dbo].[sys_odcustlist]";
    private static readonly string[] DataDateFormats = ["yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy/M/d"];

    private readonly object authLock = new();
    private Session? session;

    private sealed record Session(string Member, string Entity, string TopicType, string QueryHost,
        string QueryPath, bool UseAes, DateTimeOffset RefreshAfter);

    private sealed record TableDefinition(string Id, string Title, string Database, string SplitSource, string Frequency,
        string[] GrantedIdTypes);

    internal object Status()
    {
        var command = new CommandClass();
        var connection = configuration.ReadConnection();
        return new
        {
            installRoot = configuration.InstallRoot,
            clientAssemblyVersion = command.GetType().Assembly.GetName().Version?.ToString(),
            processArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
            authHost = connection.AuthUrl.Host,
            credentialsConfigured = TejCredentials.IsConfigured,
            exportDirectory = configuration.ExportDirectory,
            watchlistPath = configuration.WatchlistPath,
        };
    }

    internal void CheckAuthentication() => GetSession(refresh: true);

    internal TejTable[] ListTables(string keyword, string country, int limit)
    {
        country = Country(country);
        CheckLimit(limit, 200);
        var current = GetSession();
        var like = SearchPattern(keyword);
        var sql = $"""
            SELECT TOP ({limit}) d.[TOPIC_ID], MIN(d.[DESCRIPTION]) AS [TITLE], MIN(d.[資料週期]) AS [FREQUENCY]
            FROM {Topics} AS d
            WHERE {TopicScope(current, country)}
              AND (d.[DESCRIPTION] LIKE {like} ESCAPE '~' OR d.[TOPIC_ID] LIKE {like} ESCAPE '~')
              AND EXISTS (SELECT 1 FROM {Grants} AS g WHERE {GrantScope(current, "d.[TOPIC_ID]")})
            GROUP BY d.[TOPIC_ID]
            ORDER BY d.[TOPIC_ID]
            """;

        using var result = Read(current, sql);
        return result.Rows.Cast<DataRow>().Select(row => new TejTable(
            Text(row, "TOPIC_ID"), Text(row, "TITLE"), Text(row, "FREQUENCY"))).ToArray();
    }

    internal TejFieldMatch[] SearchFields(string keyword, string country, string? tableId, int limit)
    {
        country = Country(country);
        CheckLimit(limit, 200);
        var matchesTables = keyword?.TrimStart().StartsWith("#", StringComparison.Ordinal) == true;
        var term = matchesTables ? keyword!.TrimStart().Substring(1) : keyword;
        var like = SearchPattern(term, required: tableId == null);
        var tableFilter = tableId == null ? "" : $"AND d.[TOPIC_ID] = {SqlText(Identifier(tableId, "資料表代碼"))}";
        var searchFilter = matchesTables
            ? $"(d.[DESCRIPTION] LIKE {like} ESCAPE '~' OR d.[TOPIC_ID] LIKE {like} ESCAPE '~')"
            : $"(f.[CNAME] LIKE {like} ESCAPE '~' OR f.[CODE] LIKE {like} ESCAPE '~')";
        var current = GetSession();
        var sql = $"""
            SELECT TOP ({limit}) d.[TOPIC_ID], MIN(d.[DESCRIPTION]) AS [TABLE_TITLE],
                MIN(d.[資料週期]) AS [FREQUENCY], f.[CODE], MIN(COALESCE(f.[CNAME], f.[CODE])) AS [TITLE],
                MIN(f.[TYPE]) AS [TYPE], MIN(f.[UNIT]) AS [UNIT], MAX(CONVERT(int, f.[PK])) AS [PK]
            FROM {Topics} AS d
            JOIN {Fields} AS f ON f.[TOPIC_ID] = d.[TOPIC_ID]
            WHERE {TopicScope(current, country)} {tableFilter}
                AND {searchFilter} AND {VisibleField(current)}
            GROUP BY d.[TOPIC_ID], f.[CODE]
            ORDER BY d.[TOPIC_ID], MIN(f.[S_NO]), f.[CODE]
            """;
        using var result = Read(current, sql);
        return result.Rows.Cast<DataRow>().Select(row => new TejFieldMatch(Text(row, "TOPIC_ID"),
            Text(row, "TABLE_TITLE"), Text(row, "FREQUENCY"), ReadField(row))).ToArray();
    }

    internal TejField[] Describe(string tableId, string country)
    {
        tableId = Identifier(tableId, "資料表代碼");
        country = Country(country);
        var current = GetSession();
        var table = ResolveTable(current, tableId, country);
        return ReadFields(current, table.Id);
    }

    internal DateTime[] TradingDays(string country, DateTime start, DateTime end)
    {
        country = Country(country);
        var sql = $"""
            SELECT DISTINCT [MDATE]
            FROM [AP001].[dbo].[sys_tradeday]
            WHERE [COUNTRY] = {SqlText(country)} AND [TRADEDAY_CNO] <> 0
                AND [MDATE] BETWEEN {SqlText(DateText(start))} AND {SqlText(DateText(end))}
            ORDER BY [MDATE]
            """;
        using var result = Read(GetSession(), sql);
        return result.Rows.Cast<DataRow>().Select(row => DataDate(row["MDATE"])
            ?? throw new TejException("TEJ 交易日曆的日期格式不正確。")).ToArray();
    }

    internal TejResult Query(string tableId, string[] codes, string startDate, string endDate,
        string[]? fieldCodes, string country, int limit)
        => ReadQuery(tableId, codes, startDate, endDate, fieldCodes, country, limit, null);

    internal TejResult QuerySelection(string tableId, string[]? codes, string? startDate, string? endDate,
        string[]? fieldCodes, string country, int limit, TejSelection selection)
        => ReadQuery(tableId, codes, startDate, endDate, fieldCodes, country, limit, selection);

    private TejResult ReadQuery(string tableId, string[]? codes, string? startDate, string? endDate,
        string[]? fieldCodes, string country, int limit, TejSelection? selection)
    {
        tableId = Identifier(tableId, "資料表代碼");
        country = Country(country);
        CheckLimit(limit, 10_000);
        var companies = codes == null && selection?.AllowAllCodes == true ? [] : CompanyCodes(codes!);
        var start = startDate == null ? (DateTime?)null : ParseDate(startDate);
        var end = endDate == null ? (DateTime?)null : ParseDate(endDate);
        if (end < start || (selection == null && (start == null || end == null || (end - start)?.TotalDays > 3660)))
            throw new TejException("結束日必須在起始日之後，且查詢區間最多 3,660 天。");
        if (fieldCodes?.Length > 30)
            throw new TejException("每次最多選擇 30 個資料欄位。");
        var requested = fieldCodes?.Select(FieldCode)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();

        var current = GetSession();
        var table = ResolveTable(current, tableId, country);
        if (table.SplitSource.Length != 0)
            throw new TejException("此資料表由 TEJ 拆表組成，目前查詢工具支援單一實體資料表。");

        var fields = ReadFields(current, table.Id);
        var keys = fields.Where(field => field.IsKey).Select(field => field.Code).ToArray();
        var hasDate = keys.Contains("MDATE", StringComparer.OrdinalIgnoreCase);
        if (!keys.Contains("COID", StringComparer.OrdinalIgnoreCase) ||
            (selection == null && (!hasDate || keys.Length != 2)))
            throw new TejException("此資料表的主鍵格式與公司代碼、日期查詢工具不相容。");
        if (!hasDate && (start != null || end != null || selection?.Dates?.Length > 0 || selection?.Latest != null))
            throw new TejException("此資料表的主鍵不含日期。請省略日期與最近期設定。");
        var keyOrder = new[] { "COID" }.Concat(hasDate ? new[] { "MDATE" } : []).Concat(keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var lookup = fields.ToDictionary(field => field.Code, StringComparer.OrdinalIgnoreCase);
        var variableFields = requested.Length > 0 ? requested : fields.Where(field => !field.IsKey)
            .Select(field => field.Code).Take(12).ToArray();
        var filterFields = selection?.Filters.SelectMany(filter => filter.Fields).Select(FieldCode) ?? Enumerable.Empty<string>();
        if (selection?.DateField is { } dateField)
            filterFields = filterFields.Append(FieldCode(dateField));
        var selected = keyOrder.Concat(variableFields)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = selected.Concat(filterFields).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => !lookup.ContainsKey(code)).ToArray();
        if (missing.Length > 0)
            throw new TejException("找不到已授權欄位 " + string.Join(", ", missing) + "。請先查看資料表欄位。");

        var columns = selected.Select(code => lookup[code]).ToArray();
        var grantColumns = selected.Concat(filterFields).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => lookup[code].Code).ToArray();
        var sql = QuerySql(current, table, country, companies, start, end, columns, grantColumns, keyOrder,
            hasDate, selection, limit);
        using var result = Read(current, sql, country + "|||" + table.Id);
        if (result.Rows.Count > 0 && columns.Any(column => !result.Columns.Contains(column.Code)))
            throw new TejException("TEJ 回傳的欄位與請求不符。");
        var rows = result.Rows.Cast<DataRow>().Take(limit)
            .Select(row => columns.Select(column => ToJsonValue(row[column.Code])).ToArray()).ToArray();
        return new TejResult(table.Id, table.Title, country, columns, rows, result.Rows.Count > limit);
    }

    private Session GetSession(bool refresh = false)
    {
        lock (authLock)
        {
            if (!refresh && session is { } cached && cached.RefreshAfter > DateTimeOffset.UtcNow)
                return cached;

            var credentials = TejCredentials.Read();
            var connection = configuration.ReadConnection();
            var command = new CommandClass { mUseAES = connection.UseAes };
            string[] response;
            try
            {
                response = command.GFunStr_Login(connection.AuthUrl.AbsoluteUri, credentials.User, credentials.Password);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            {
                throw new TejException("TEJ 登入未完成。請確認網路、帳號密碼及 Smart Wizard 的登入方式。");
            }

            if (response.Length < 5 || response[0] != "1")
                throw new TejException("TEJ 未核准本次登入。請確認帳號及 Smart Wizard 存取權限。");

            var entity = response[2].Trim();
            var topicType = Identifier(response[3], "TEJ 登入回傳的帳號類型");
            if (entity.Length is < 1 or > 128 || entity.Any(char.IsControl))
                throw new TejException("TEJ 未回傳可用的授權帳號識別。");

            var query = TejConfiguration.HttpsUrl(response[4].Trim());
            if (!string.Equals(query.Host, connection.AuthUrl.Host, StringComparison.OrdinalIgnoreCase) &&
                !query.Host.EndsWith(".tej.com.tw", StringComparison.OrdinalIgnoreCase) &&
                !query.Host.EndsWith(".tejwin.com", StringComparison.OrdinalIgnoreCase))
                throw new TejException("TEJ 回傳了未辨識的查詢主機。");

            var member = credentials.User;
            if (topicType == "TEJSCH" && response.Length > 23 && !string.IsNullOrWhiteSpace(response[23]))
            {
                if (!int.TryParse(response[23], NumberStyles.None, CultureInfo.InvariantCulture, out var schoolNumber))
                    throw new TejException("TEJ 回傳的校園帳號序號格式不正確。");
                member = entity + schoolNumber.ToString("000", CultureInfo.InvariantCulture);
            }

            session = new Session(member, entity, topicType, query.AbsoluteUri.TrimEnd('/'),
                connection.QueryPath, connection.UseAes, DateTimeOffset.UtcNow.AddMinutes(30));
            return session;
        }
    }

    private static DataTable Read(Session session, string sql, string usage = "")
    {
        var command = new CommandClass
        {
            Str_Memeber = session.Member,
            GStr_API主機 = session.QueryHost,
            GConStr_APIURL = session.QueryPath,
            mUseAES = session.UseAes,
        };

        try
        {
            return command.GFunStr_GetAPIData(CommandClass.API執行動作.查詢資料, "", sql, usage);
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            throw new TejException("TEJ 查詢服務未回傳可用的結果。請確認目前連線及資料授權。");
        }
    }

    private static TableDefinition ResolveTable(Session session, string tableId, string country)
    {
        var sql = $"""
            SELECT DISTINCT d.[TOPIC_ID], d.[DESCRIPTION], d.[DBID], d.[拆表來源], d.[資料週期],
                CASE WHEN EXISTS (SELECT 1 FROM {Grants} AS g WHERE {GrantScope(session, "d.[TOPIC_ID]")}
                    AND g.[MKTGROUP] = RTRIM(d.[COUNTRY]) + '-' + RTRIM(d.[ID_TYPE]))
                    THEN d.[ID_TYPE] ELSE NULL END AS [GRANTED_ID_TYPE]
            FROM {Topics} AS d
            WHERE {TopicScope(session, country)} AND d.[TOPIC_ID] = {SqlText(tableId)}
              AND EXISTS (SELECT 1 FROM {Grants} AS g WHERE {GrantScope(session, "d.[TOPIC_ID]")})
            """;
        using var result = Read(session, sql);
        if (result.Rows.Count == 0)
            throw new TejException("找不到此市場的已授權資料表。請先搜尋資料表代碼。");
        var rows = result.Rows.Cast<DataRow>();
        var sources = rows.Select(row => (Database: Text(row, "DBID"), SplitSource: Text(row, "拆表來源"),
            Frequency: Text(row, "資料週期"))).Distinct().ToArray();
        if (sources.Length != 1)
            throw new TejException("同一資料表代碼對應到多個實體資料表，無法確定查詢來源。");
        var idTypes = rows.Select(row => Text(row, "GRANTED_ID_TYPE")).Where(id => id.Length != 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new TableDefinition(Text(result.Rows[0], "TOPIC_ID"), Text(result.Rows[0], "DESCRIPTION"),
            sources[0].Database, sources[0].SplitSource, sources[0].Frequency, idTypes);
    }

    private static TejField[] ReadFields(Session session, string tableId)
    {
        var sql = $"""
            SELECT DISTINCT f.[CODE], COALESCE(f.[CNAME], f.[CODE]) AS [TITLE], f.[TYPE], f.[UNIT], f.[PK], f.[S_NO]
            FROM {Fields} AS f
            WHERE f.[TOPIC_ID] = {SqlText(tableId)} AND {VisibleField(session)}
            ORDER BY f.[S_NO], f.[CODE]
            """;
        using var result = Read(session, sql);
        return result.Rows.Cast<DataRow>().Select(ReadField)
            .GroupBy(field => field.Code, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
    }

    private static TejField ReadField(DataRow row) => new(Text(row, "CODE"), Text(row, "TITLE"),
        Text(row, "TYPE"), Text(row, "UNIT"), Text(row, "PK") is "1" or "True");

    private static string VisibleField(Session session) => $"""
        (f.[C_AP] = 'Y' OR f.[PK] = 1)
        AND EXISTS (SELECT 1 FROM {Grants} AS g WHERE {GrantScope(session, "f.[TOPIC_ID]")}
            AND (g.[COLGROUP] = 'ALL' OR (g.[COLGROUP] IS NULL AND EXISTS (
                SELECT 1 FROM {FieldGrants} AS c WHERE c.[USERID] = g.[USERID]
                    AND c.[TOPICID] = g.[TOPICID] AND c.[CODE] = f.[CODE]))))
        """;

    private static string QuerySql(Session session, TableDefinition table, string country, string[] companies,
        DateTime? start, DateTime? end, TejField[] columns, string[] grantColumns, string[] keys,
        bool hasDate, TejSelection? selection, int limit)
    {
        var select = string.Join(", ", columns.Select(column => "d.[" + FieldCode(column.Code) + "]"));
        var fieldCodes = string.Join(", ", grantColumns.Select(SqlText));
        var source = SqlIdentifier(table.Database) + ".[dbo]." + SqlIdentifier(table.Id);
        var predicates = new List<string>();
        if (companies.Length > 0)
            predicates.Add($"d.[COID] IN ({string.Join(", ", companies.Select(SqlText))})");
        if (hasDate)
            AddDateFilters(predicates, selection?.DateField == null ? table.Frequency : "D", start, end,
                selection?.Dates, selection?.DateField ?? "MDATE");
        foreach (var filter in selection?.Filters ?? [])
        {
            var pattern = SearchPattern(filter.Term, required: true);
            var matches = filter.Fields.Select(field => $"d.[{FieldCode(field)}] LIKE {pattern} ESCAPE '~'").ToList();
            if (filter.IncludeCompanyShortName)
                matches.Add($"EXISTS (SELECT 1 FROM [COID001].[dbo].[TW_GSTOCK] AS s WHERE s.[COID] = d.[COID] AND s.[TC] LIKE {pattern} ESCAPE '~')");
            predicates.Add("(" + string.Join(" OR ", matches) + ")");
        }
        var filters = predicates.Count == 0 ? "" : string.Join(" AND ", predicates) + " AND ";
        var from = $"""
            FROM {source} AS d
            WHERE {filters}EXISTS (SELECT 1 FROM {Grants} AS g WHERE {GrantScope(session, SqlText(table.Id))}
                  AND ({MarketScope(table, country)})
                  AND (g.[CODEGROUP] IS NULL OR EXISTS (SELECT 1 FROM {CustomCodes} AS u
                      WHERE u.[CUSTID] = g.[USERID] AND u.[CODEGROUP] = g.[CODEGROUP] AND u.[CODE] = d.[COID]))
                  AND (g.[COLGROUP] = 'ALL' OR (g.[COLGROUP] IS NULL AND {grantColumns.Length} = (
                      SELECT COUNT(DISTINCT c.[CODE]) FROM {FieldGrants} AS c
                      WHERE c.[USERID] = g.[USERID] AND c.[TOPICID] = g.[TOPICID] AND c.[CODE] IN ({fieldCodes})))))
            """;
        var dateOrder = selection?.Descending == true ? "DESC" : "ASC";
        var codeOrder = string.Join(", ", keys.Where(key => key != "MDATE").Select(key => "d.[" + FieldCode(key) + "] ASC"));
        var order = hasDate ? "d.[MDATE] " + dateOrder + ", " + codeOrder : codeOrder;
        if (selection?.DateField is { } preferredDate)
            order = $"d.[{FieldCode(preferredDate)}] {dateOrder}, " + order;
        if (selection?.Latest is not { } latest)
            return $"SELECT TOP ({limit + 1}) {select} {from} ORDER BY {order}";

        var partition = selection.Aligned ? "" : "PARTITION BY d.[COID] ";
        return $"""
            SELECT TOP ({limit + 1}) {select}
            FROM (SELECT {select}, DENSE_RANK() OVER ({partition}ORDER BY d.[MDATE] DESC) AS [__TEJ_PERIOD_RANK]
                {from}) AS d
            WHERE d.[__TEJ_PERIOD_RANK] <= {latest}
            ORDER BY {order}
            """;
    }

    private static void AddDateFilters(List<string> predicates, string frequency, DateTime? start, DateTime? end,
        DateTime[]? dates, string dateField)
    {
        var format = frequency.ToUpperInvariant() switch
        {
            "M" or "MONTH" or "月" or "月資料" or "Q" or "QUARTER" or "季" or "季資料" or
                "H" or "HALFYEAR" or "半年" or "半年資料" => "yyyyMM",
            "Y" or "YEAR" or "年" or "年資料" => "yyyy",
            _ => "yyyyMMdd",
        };
        string Bucket(DateTime date) => date.ToString(format, CultureInfo.InvariantCulture);
        var field = "d.[" + FieldCode(dateField) + "]";
        // Some annual TEJ tables store year labels as YYYYMM rather than YYYY.
        if (start is { } lower)
            predicates.Add($"{field} >= {SqlText(Bucket(lower))}");
        if (end is { } upper)
            predicates.Add($"{field} <= {SqlText(Bucket(upper) + (format == "yyyy" ? "12" : ""))}");
        if (dates?.Length > 0)
        {
            if (format == "yyyy")
                predicates.Add("(" + string.Join(" OR ", dates.Select(Bucket).Distinct().Select(year =>
                    $"{field} BETWEEN {SqlText(year)} AND {SqlText(year + "12")}")) + ")");
            else
                predicates.Add($"{field} IN ({string.Join(", ", dates.Select(Bucket).Distinct().Select(SqlText))})");
        }
    }

    private static string MarketScope(TableDefinition table, string country)
    {
        var scopes = new List<string>
        {
            "g.[MKTGROUP] = 'ALL'",
            $"EXISTS (SELECT 1 FROM {MarketCodes} AS m WHERE m.[MKTGROUP] = g.[MKTGROUP] AND m.[CODE] = d.[COID])",
        };

        // TEJ Pro resolves a country/ID-type grant through both company catalogs.
        foreach (var idType in table.GrantedIdTypes)
        {
            var group = SqlText(country + "-" + idType);
            var countryCodes = "[COID001].[dbo]." + SqlIdentifier(country + "_" + idType);
            var topicCodes = "[COID002].[dbo]." + SqlIdentifier(table.Id + "_COID");
            scopes.Add($"""
                (g.[MKTGROUP] = {group}
                    AND EXISTS (SELECT 1 FROM {countryCodes} AS t WHERE t.[COID] = d.[COID])
                    AND EXISTS (SELECT 1 FROM {topicCodes} AS c WHERE c.[COID] = d.[COID]))
                """);
        }
        return string.Join(" OR ", scopes);
    }

    private static string TopicScope(Session session, string country) =>
        $"d.[COUNTRY] = {SqlText(country)} AND d.[TOPIC_TYPE] = {SqlText(session.TopicType)} " +
        "AND d.[LANGUAGE] = 'TC' AND d.[DBID] IS NOT NULL AND d.[DBID] <> '' AND d.[TOPIC_ID] <> ''";

    private static string GrantScope(Session session, string tableExpression) =>
        $"g.[USERID] = {SqlText(session.Entity)} AND g.[TOPICID] = {tableExpression} " +
        $"AND g.[ENDDATE] >= {SqlText(DateText(TaipeiToday()))}";

    internal static DateTime TaipeiToday() => TaipeiNow().Date;

    internal static DateTimeOffset TaipeiNow() =>
        TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Taipei Standard Time");

    private static string DateText(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string Text(DataRow row, string column) => Convert.ToString(row[column], CultureInfo.InvariantCulture)?.Trim() ?? "";
    private static string SqlText(string value) => "N'" + value.Replace("'", "''") + "'";
    private static string SearchPattern(string? value, bool required = false)
    {
        var keyword = value?.Trim() ?? "";
        if ((required && keyword.Length == 0) || keyword.Length > 100 || keyword.Any(char.IsControl))
            throw new TejException("請輸入有效的搜尋關鍵字，最多 100 字元。");
        return SqlText("%" + keyword.Replace("~", "~~").Replace("%", "~%")
            .Replace("_", "~_").Replace("[", "~[") + "%");
    }
    private static string SqlIdentifier(string value) => "[" + Identifier(value, "TEJ 資料庫識別碼") + "]";

    private static string Country(string value) => Normalized(value, @"\A[A-Z]{2}\z", "市場代碼須為兩個英文字母，例如 TW。");
    internal static string CompanyCode(string value) => Normalized(value, @"\A[A-Z0-9][A-Z0-9._-]{0,23}\z", "公司代碼格式不正確。");
    private static string FieldCode(string value) => Normalized(value, @"\A[A-Z0-9_][A-Z0-9_%\-]{0,63}\z", "欄位代碼格式不正確。");
    private static string Identifier(string value, string label) => Normalized(value, @"\A[A-Z0-9_]{1,64}\z", label + "格式不正確。");

    internal static string[] CompanyCodes(string[] codes, int maximum = 30)
    {
        if (codes == null || codes.Length < 1 || codes.Length > maximum)
            throw new TejException($"請提供 1 到 {maximum} 個公司代碼，例如 2330 或 0050。");
        return codes.Select(CompanyCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Normalized(string value, string pattern, string error)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? "";
        return Regex.IsMatch(normalized, pattern, RegexOptions.CultureInvariant) ? normalized : throw new TejException(error);
    }

    internal static DateTime ParseDate(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed : throw new TejException("日期格式須為 YYYY-MM-DD。");

    internal static DateTime? DataDate(object? value)
    {
        if (value is DateTime timestamp)
            return timestamp.Date;
        return DateTime.TryParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(), DataDateFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    internal static void CheckLimit(int limit, int maximum)
    {
        if (limit < 1 || limit > maximum)
            throw new TejException($"筆數上限須介於 1 到 {maximum}。");
    }

    private static object? ToJsonValue(object value) => value switch
    {
        DBNull => null,
        DateTime date when date.TimeOfDay == TimeSpan.Zero => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => value,
    };
}
