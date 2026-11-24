using System;
using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TejMcp;

internal sealed class TejTools(TejClient client, CsvExport export, TejWatchlists watchlists)
{
    private readonly TejWorkflows workflows = new(client);

    [McpServerTool(Name = "tej_status", ReadOnly = true, OpenWorld = false)]
    [Description("檢查本機 TEJ Pro 用戶端組件與憑證設定。這個工具不會登入或查資料。")]
    public object Status() => Run(client.Status);

    [McpServerTool(Name = "tej_search_tables", ReadOnly = true, OpenWorld = true)]
    [Description("依中文表名或代碼搜尋帳號目前可用的 TEJ Pro 資料表。查詢前先用此工具確認資料表代碼。")]
    public object SearchTables(
        [Description("表名或代碼關鍵字，例如 未調整股價、月營收。空字串列出可用資料表。 ")] string keyword = "",
        [Description("兩碼市場代碼，預設 TW。 ")] string country = "TW",
        [Description("最多傳回幾個資料表，範圍 1–200。 ")] int limit = 30) =>
        Run(() => client.ListTables(keyword, country, limit));

    [McpServerTool(Name = "tej_list_fields", ReadOnly = true, OpenWorld = true)]
    [Description("列出指定 TEJ Pro 資料表的已授權欄位代碼、中文名稱、單位，以及是否為主鍵。")]
    public object ListFields(
        [Description("tej_search_tables 傳回的 tableId。 ")] string tableId,
        [Description("兩碼市場代碼，預設 TW。 ")] string country = "TW") =>
        Run(() => client.Describe(tableId, country));

    [McpServerTool(Name = "tej_query", ReadOnly = true, OpenWorld = true)]
    [Description("查詢以公司代碼及日期為主鍵的 TEJ Pro 資料表，回傳欄位定義與依欄位順序排列的資料列。")]
    public TejResult Query(
        [Description("tej_search_tables 傳回的 tableId。 ")] string tableId,
        [Description("公司代碼陣列，最多 30 個，例如 [\"2330\",\"0050\"]。保留前導零。 ")] string[] codes,
        [Description("起始日期，YYYY-MM-DD，包含當天。 ")] string startDate,
        [Description("結束日期，YYYY-MM-DD，包含當天。 ")] string endDate,
        [Description("tej_list_fields 傳回的欄位代碼。省略時回傳前 12 個可用資料欄位。 ")] string[]? fields = null,
        [Description("兩碼市場代碼，預設 TW。 ")] string country = "TW",
        [Description("回傳筆數上限 1–1000。truncated 代表還有更多資料。 ")] int limit = 100) =>
        Run(() =>
        {
            TejClient.CheckLimit(limit, 1000);
            return client.Query(tableId, codes, startDate, endDate, fields, country, limit);
        });

    [McpServerTool(Name = "tej_export_csv", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("依公司與日期查詢 TEJ Pro，將結果存成 UTF-8 CSV。只在使用者明確要求匯出時使用。")]
    public CsvResult ExportCsv(
        [Description("tej_search_tables 傳回的 tableId。 ")] string tableId,
        [Description("公司代碼陣列，最多 30 個。保留前導零。 ")] string[] codes,
        [Description("起始日期，YYYY-MM-DD，包含當天。 ")] string startDate,
        [Description("結束日期，YYYY-MM-DD，包含當天。 ")] string endDate,
        [Description("輸出檔名，例如 prices.csv。 ")] string fileName,
        [Description("tej_list_fields 傳回的欄位代碼。 ")] string[]? fields = null,
        [Description("兩碼市場代碼，預設 TW。 ")] string country = "TW",
        [Description("匯出筆數上限 1–10000。truncated 代表還有更多資料。 ")] int limit = 10000) =>
        Run(() =>
        {
            var destination = export.Destination(fileName);
            return export.Save(destination, client.Query(tableId, codes, startDate, endDate, fields, country, limit));
        });

    [McpServerTool(Name = "tej_daily_prices", ReadOnly = true, OpenWorld = true)]
    [Description("查台股每日開、高、低、收、成交量及成交值。可選除權息調整表，預設使用未調整股價。")]
    public TejResult DailyPrices(
        [Description("證券代碼，例如 [\"2330\",\"0050\"]。保留前導零。 ")] string[] codes,
        [Description("起始日 YYYY-MM-DD，包含當天。 ")] string startDate,
        [Description("結束日 YYYY-MM-DD，包含當天。 ")] string endDate,
        [Description("true 使用 TEJ 除權息調整日股價。 ")] bool adjusted = false,
        [Description("最多回傳 1–1000 筆。 ")] int limit = 100) =>
        Run(() => workflows.Series(adjusted ? "adjusted_prices" : "daily_prices", codes, startDate, endDate, limit));

    [McpServerTool(Name = "tej_compare_price_performance", ReadOnly = true, OpenWorld = true)]
    [Description("以台股除權息調整收盤價比較共通交易日的區間漲跌與最大回撤。可提供一個基準算出漲跌的百分點差。")]
    public PriceComparison ComparePricePerformance(
        [Description("要比較的證券代碼。含基準最多 30 個。 ")] string[] codes,
        [Description("起始日 YYYY-MM-DD。實際比較從第一個有資料的共通交易日開始。 ")] string startDate,
        [Description("結束日 YYYY-MM-DD。實際比較算到最後一個有資料的共通交易日。 ")] string endDate,
        [Description("選填的基準證券代碼，例如 0050。 ")] string? benchmarkCode = null) =>
        Run(() => workflows.ComparePrices(codes, startDate, endDate, benchmarkCode));

    [McpServerTool(Name = "tej_monthly_revenue", ReadOnly = true, OpenWorld = true)]
    [Description("查台股月營收、去年同月營收、年增率、月增率、累計營收及公告日。日期篩選依營收月份。")]
    public TejResult MonthlyRevenue(
        [Description("台股公司代碼陣列。 ")] string[] codes,
        [Description("起始月份內的日期 YYYY-MM-DD。 ")] string startDate,
        [Description("結束月份內的日期 YYYY-MM-DD。 ")] string endDate,
        [Description("最多回傳 1–1000 筆。 ")] int limit = 100) =>
        Run(() => workflows.Series("monthly_revenue", codes, startDate, endDate, limit));

    [McpServerTool(Name = "tej_quarterly_financials", ReadOnly = true, OpenWorld = true)]
    [Description("查 IFRS 以合併為主的單季財報，包含營業收入淨額、EPS、營業毛利率、營業利益率、稅後淨利率、稅後 ROE、幣別和財報發布日。")]
    public TejResult QuarterlyFinancials(
        [Description("台股公司代碼陣列。 ")] string[] codes,
        [Description("會計期末的區間起始日 YYYY-MM-DD。 ")] string startDate,
        [Description("會計期末的區間結束日 YYYY-MM-DD。 ")] string endDate,
        [Description("最多回傳 1–1000 筆。 ")] int limit = 100) =>
        Run(() => workflows.Series("quarterly_financials", codes, startDate, endDate, limit));

    [McpServerTool(Name = "tej_institutional_flows", ReadOnly = true, OpenWorld = true)]
    [Description("查台股外資、投信、自營商及三大法人合計的每日買賣超與區間加總。單位為千股。")]
    public InstitutionalFlows InstitutionalFlows(
        [Description("台股證券代碼陣列。 ")] string[] codes,
        [Description("起始日 YYYY-MM-DD。 ")] string startDate,
        [Description("結束日 YYYY-MM-DD。 ")] string endDate,
        [Description("日資料最多 1–1000 筆。若區間超出上限，請縮短日期後分批查詢。 ")] int limit = 1000) =>
        Run(() => workflows.Flows(codes, startDate, endDate, limit));

    [McpServerTool(Name = "tej_valuation_snapshot", ReadOnly = true, OpenWorld = true)]
    [Description("比較指定台股在同一交易日的 TEJ 本益比、股價淨值比、市值及交易所殖利率。採截止日往前十四天內有資料的最近一天。")]
    public ValuationSnapshot ValuationSnapshot(
        [Description("最多 30 個台股證券代碼。 ")] string[] codes,
        [Description("截止日 YYYY-MM-DD，包含當天。回傳使用的實際交易日。 ")] string asOfDate) =>
        Run(() => workflows.Valuation(codes, asOfDate));

    [McpServerTool(Name = "tej_tesg_ratings", ReadOnly = true, OpenWorld = true)]
    [Description("查 TEJ 的 TESG 評等、公告日、升降等及環境、社會、公司治理各構面分數。日期篩選依評等期間。")]
    public TejResult TesgRatings(
        [Description("台股公司代碼陣列。 ")] string[] codes,
        [Description("評等期間起始日 YYYY-MM-DD。 ")] string startDate,
        [Description("評等期間結束日 YYYY-MM-DD。 ")] string endDate,
        [Description("最多回傳 1–1000 筆。 ")] int limit = 100) =>
        Run(() => workflows.Series("tesg_ratings", codes, startDate, endDate, limit));

    [McpServerTool(Name = "tej_export_report_csv", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("將預設台股報表另存為 UTF-8 CSV。只在使用者要求匯出時使用，保留同名檔案。")]
    public CsvResult ExportReportCsv(
        [Description("報表類型：daily_prices、adjusted_prices、monthly_revenue、quarterly_financials、institutional_flows、valuation、tesg_ratings。估值及法人匯出為每日明細。 ")] string report,
        [Description("台股證券代碼陣列。 ")] string[] codes,
        [Description("起始日 YYYY-MM-DD。 ")] string startDate,
        [Description("結束日 YYYY-MM-DD。 ")] string endDate,
        [Description("輸出檔名，例如 月營收.csv。 ")] string fileName,
        [Description("最多匯出 1–10000 筆。truncated 表示有更多資料。 ")] int limit = 10000) =>
        Run(() =>
        {
            var destination = export.Destination(fileName);
            return export.Save(destination, workflows.ReadReport(report, codes, startDate, endDate, limit));
        });

    [McpServerTool(Name = "tej_watchlist_list", ReadOnly = true, OpenWorld = false)]
    [Description("列出本機已儲存的台股觀察清單及證券代碼。")]
    public TejWatchlist[] ListWatchlists() => Run(watchlists.List);

    [McpServerTool(Name = "tej_watchlist_add", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("加入證券到本機觀察清單。可建立新清單，原有證券和順序會保留。")]
    public TejWatchlist AddToWatchlist(
        [Description("要新增的證券代碼，例如 [\"2330\",\"0050\"]。保留前導零。 ")] string[] codes,
        [Description("清單名稱，可使用中文。省略時使用預設清單。 ")] string name = TejWatchlists.DefaultName) =>
        Run(() => watchlists.Add(name, codes));

    [McpServerTool(Name = "tej_watchlist_remove", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("從本機觀察清單移除指定代碼。只在使用者要求移除時使用。")]
    public TejWatchlist RemoveFromWatchlist(
        [Description("要移除的證券代碼。 ")] string[] codes,
        [Description("清單名稱。省略時使用預設清單。 ")] string name = TejWatchlists.DefaultName) =>
        Run(() => watchlists.Remove(name, codes));

    [McpServerTool(Name = "tej_post_market_summary", ReadOnly = true, OpenWorld = true)]
    [Description("查台股盤後摘要，列出同一天的收盤價、漲跌、成交量相對前 20 日均量的倍數，以及三大法人買賣超。可用觀察清單或指定代碼。")]
    public PostMarketSummary PostMarketSummary(
        [Description("選填的觀察清單名稱。省略名稱與代碼時使用預設清單。 ")] string? watchlistName = null,
        [Description("選填的證券代碼陣列。和觀察清單名稱擇一。 ")] string[]? codes = null,
        [Description("選填的截止日 YYYY-MM-DD，預設台北當天。回傳截止日以前 TEJ 可取得的最近交易日。 ")] string? asOfDate = null) =>
        Run(() =>
        {
            var selected = SelectStocks(watchlistName, codes);
            return workflows.PostMarket(selected.Codes, asOfDate, selected.WatchlistName);
        });

    [McpServerTool(Name = "tej_screen_watchlist", ReadOnly = true, OpenWorld = true)]
    [Description("依最新已公告月營收年增率與外資區間累計買賣超篩選台股，回傳排序名次、各檔判定及實際採用的交易日。")]
    public StockScreenResult ScreenWatchlist(
        [Description("選填的觀察清單名稱。省略名稱與代碼時使用預設清單。 ")] string? watchlistName = null,
        [Description("選填的證券代碼陣列。和觀察清單名稱擇一。 ")] string[]? codes = null,
        [Description("截止日 YYYY-MM-DD，預設台北當天。營收公告日須在當天或之前。 ")] string? asOfDate = null,
        [Description("月營收年增率須嚴格高於此值，單位為百分比，預設 20。 ")] decimal revenueYoYAbove = 20m,
        [Description("外資累計買賣超須嚴格高於此值，單位為千股，預設 0。 ")] decimal foreignNetAbove = 0m,
        [Description("外資累計窗口的交易日數，1–60，預設 5。 ")] int foreignTradingDays = 5,
        [Description("由高到低排列。revenue_yoy 為年增率，foreign_net 為外資累計買賣超。 ")] string sortBy = "revenue_yoy") =>
        Run(() =>
        {
            var selected = SelectStocks(watchlistName, codes);
            return workflows.ScreenStocks(selected.Codes, selected.WatchlistName, asOfDate, revenueYoYAbove,
                foreignNetAbove, foreignTradingDays, sortBy);
        });

    private (string[] Codes, string? WatchlistName) SelectStocks(string? watchlistName, string[]? codes)
    {
        if (codes != null)
        {
            if (watchlistName != null)
                throw new TejException("請選擇一份觀察清單，或直接提供證券代碼。");
            return (codes, null);
        }

        var watchlist = watchlists.Get(watchlistName ?? TejWatchlists.DefaultName);
        if (watchlist.Codes.Length == 0)
            throw new TejException($"觀察清單「{watchlist.Name}」還沒有證券。請先新增代碼。");
        return (watchlist.Codes, watchlist.Name);
    }

    private static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (TejException exception)
        {
            throw new McpException(exception.Message);
        }
    }
}
