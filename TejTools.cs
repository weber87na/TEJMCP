using System;
using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace TejMcp;

internal sealed class TejTools(TejClient client, CsvExport export, TejWatchlists watchlists, TejSavedReports savedReports)
{
    private readonly TejWorkflows workflows = new(client);
    private readonly TejReports reports = new(client);
    private readonly TejEventStudy eventStudy = new(client);

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

    [McpServerTool(Name = "tej_search_fields", ReadOnly = true, OpenWorld = true)]
    [Description("按欄位名稱或代碼跨表搜尋已授權欄位。在關鍵字前加 # 可改搜資料表名稱或代碼。")]
    public TejFieldMatch[] SearchFields(
        [Description("欄位關鍵字，例如 EPS、股利，或 #股價。指定 tableId 時可傳空字串。")] string keyword,
        [Description("兩碼市場代碼，預設 TW。")] string country = "TW",
        [Description("選填的 TEJ 資料表代碼。")] string? tableId = null,
        [Description("最多回傳 1–200 個欄位。")] int limit = 50) =>
        Run(() => client.SearchFields(keyword, country, tableId, limit));

    [McpServerTool(Name = "tej_search_companies", ReadOnly = true, OpenWorld = true)]
    [Description("依台灣公司的代碼、簡稱、中英文全名、產業分類或上市別找研究樣本，回傳上市及下市日期。")]
    public TejResult SearchCompanies(
        [Description("代碼或中英文公司名稱關鍵字。")] string? keyword = null,
        [Description("TEJ 業別或產業別欄位的關鍵字或代碼。")] string? industry = null,
        [Description("上市別欄位的關鍵字或代碼。")] string? market = null,
        [Description("最多回傳 1–200 筆。")] int limit = 50) =>
        Run(() => reports.Companies(keyword, industry, market, limit));

    [McpServerTool(Name = "tej_query_periods", ReadOnly = true, OpenWorld = true)]
    [Description("以指定日期、最近期或週期彙整查詢 TEJ。公司主檔可省略日期，新聞等資料會一併回傳附加主鍵。")]
    public TejResult QueryPeriods(
        [Description("TEJ 資料表代碼。")] string tableId,
        [Description("選填的代碼陣列。和 watchlistName 擇一。")] string[]? codes = null,
        [Description("選填的本機觀察清單。省略清單與代碼時使用預設清單。")] string? watchlistName = null,
        [Description("選填的起始日 YYYY-MM-DD。")] string? startDate = null,
        [Description("選填的結束日 YYYY-MM-DD。")] string? endDate = null,
        [Description("依輸出順序選擇欄位。")] string[]? fields = null,
        [Description("指定日期、最近期、排序或週期彙整設定。")] TejQueryOptions? options = null,
        [Description("兩碼市場代碼，預設 TW。")] string country = "TW",
        [Description("最多回傳 1–1000 筆。")] int limit = 100) =>
        Run(() => reports.Query(tableId, SelectStocks(watchlistName, codes).Codes, startDate, endDate, fields, country, options, limit));

    [McpServerTool(Name = "tej_compare_matrix", ReadOnly = true, OpenWorld = true)]
    [Description("將同家公司跨期或多家公司同一期的資料轉置成比較表，可依數值欄位為公司排序。")]
    public TejMatrix CompareMatrix(
        [Description("TEJ 資料表代碼。")] string tableId,
        [Description("選取的欄位代碼。")] string[] fields,
        [Description("指定日期陣列 YYYY-MM-DD。跨公司時指定一個日期。")] string[] dates,
        [Description("代碼陣列。和 watchlistName 擇一。")] string[]? codes = null,
        [Description("本機觀察清單名稱。")] string? watchlistName = null,
        [Description("選填的數值欄位代碼，依該欄位排名。")] string? rankField = null,
        [Description("true 由高到低或由新到舊。")] bool descending = true,
        [Description("兩碼市場代碼，預設 TW。")] string country = "TW") =>
        Run(() => reports.Matrix(tableId, SelectStocks(watchlistName, codes).Codes, fields, dates, country, rankField, descending));

    [McpServerTool(Name = "tej_query_panel", ReadOnly = true, OpenWorld = true)]
    [Description("跨資料表下載，依公司與日期或指定週期合併。可直接提供報表設定，或重跑已儲存的報表。")]
    public TejResult QueryPanel(
        [Description("報表或欄位群組設定。與 savedName 擇一。")] TejReportDefinition? report = null,
        [Description("已儲存的報表名稱。")] string? savedName = null,
        [Description("覆寫報表起日 YYYY-MM-DD。")] string? startDate = null,
        [Description("覆寫報表迄日 YYYY-MM-DD。")] string? endDate = null,
        [Description("最多回傳 1–1000 筆。")] int limit = 100,
        [Description("改用指定代碼執行報表。與 watchlistName 擇一。")] string[]? codes = null,
        [Description("改用已儲存的公司觀察清單執行報表。")] string? watchlistName = null) =>
        Run(() =>
        {
            TejClient.CheckLimit(limit, 1000);
            return ReadPanel(report, savedName, startDate, endDate, limit, codes, watchlistName);
        });

    [McpServerTool(Name = "tej_report_save", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("儲存本機欄位群組或跨表報表設定，供日後重跑。同名時取代原設定。")]
    public TejSavedReport SaveReport(
        [Description("報表或欄位群組名稱。")] string name,
        [Description("報表定義。只保存欄位群組時可省略日期與代碼。")] TejReportDefinition report) =>
        Run(() => savedReports.Save(name, report));

    [McpServerTool(Name = "tej_report_list", ReadOnly = true, OpenWorld = false)]
    [Description("列出本機已儲存的跨表報表和欄位群組。")]
    public TejSavedReport[] ListReports() => Run(savedReports.List);

    [McpServerTool(Name = "tej_export_data", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("將單表或跨表報表匯出為 CSV、TXT 或 XLSX。只在使用者要求匯出時使用。")]
    public CsvResult ExportData(
        [Description("檔名，可用 .csv、.txt 或 .xlsx。")] string fileName,
        [Description("報表設定。與 savedName 擇一。")] TejReportDefinition? report = null,
        [Description("已儲存的報表名稱。")] string? savedName = null,
        [Description("覆寫報表起日 YYYY-MM-DD。")] string? startDate = null,
        [Description("覆寫報表迄日 YYYY-MM-DD。")] string? endDate = null,
        [Description("文字檔選填分隔符號，可用逗號、Tab、直線或分號。")] string? delimiter = null,
        [Description("選填的空值輸出文字。")] string? nullValue = null,
        [Description("最多匯出 1–10000 筆。")] int limit = 10_000,
        [Description("改用指定代碼執行報表。與 watchlistName 擇一。")] string[]? codes = null,
        [Description("改用已儲存的公司觀察清單執行報表。")] string? watchlistName = null) =>
        Run(() =>
        {
            var destination = export.Destination(fileName, multiFormat: true);
            return export.Save(destination, ReadPanel(report, savedName, startDate, endDate, limit, codes, watchlistName), delimiter, nullValue);
        });

    [McpServerTool(Name = "tej_export_chart_png", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("將一個數值欄位的跨期比較畫成 PNG 折線圖，最多 8 組序列。只在使用者要求產生圖表時使用。")]
    public CsvResult ExportChart(
        [Description("輸出的 .png 檔名。")] string fileName,
        [Description("只選一個來源及一個數值欄位，最多 8 個代碼。")] TejReportDefinition report) =>
        Run(() =>
        {
            var destination = export.Destination(fileName, png: true);
            var data = ReadPanel(report, null, null, null, 10_000, null, null);
            export.SaveFile(destination, stream => TejChart.Write(stream, data));
            return new CsvResult(destination, data.Rows.Length, false);
        });

    [McpServerTool(Name = "tej_search_events", ReadOnly = true, OpenWorld = true)]
    [Description("按代碼、日期和關鍵字找 TEJ 歷史金融新聞、股票市場大事記或特定事件，可用於挑選事件樣本。")]
    public TejResult SearchEvents(
        [Description("news 查金融大事記，market_memo 查交易所歷史事件，special 查特定事件。")] string dataset,
        [Description("起始日 YYYY-MM-DD。")] string startDate,
        [Description("結束日 YYYY-MM-DD。")] string endDate,
        [Description("選填代碼。news 填公司代碼，market_memo 或 special 填事件類別代碼。")] string[]? codes = null,
        [Description("標題或內文關鍵字。")] string? keyword = null,
        [Description("news 設為 true 時也搜尋及回傳新聞內文。")] bool includeBody = false,
        [Description("special 選擇公告日 announcement 或事件日 occurrence，預設公告日。")] string? dateBasis = null,
        [Description("最多 1–1000 筆。")] int limit = 50) =>
        Run(() => reports.Events(dataset, codes, startDate, endDate, keyword, includeBody, dateBasis, limit));

    [McpServerTool(Name = "tej_event_study", ReadOnly = true, OpenWorld = true)]
    [Description("以自訂台股事件日執行日、週、月事件研究，回傳各樣本估計與異常報酬、累積值、平均及排除原因。")]
    public TejEventStudyResult EventStudy(
        [Description("1–30 組事件，每組填入 code 和 date。與 sampleText 擇一。")] TejEvent[]? events = null,
        [Description("事件期、估計期、模型、基準與缺值設定。")] TejEventOptions? options = null,
        [Description("CSV/TXT 樣本文字，每列為證券代碼、半形逗號、YYYYMMDD，省略標題列。")] string? sampleText = null) =>
        Run(() => ReadEvents(events, sampleText, options));

    [McpServerTool(Name = "tej_export_event_study", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("計算並匯出事件研究結果，支援 CSV、TXT、XLSX。只在使用者要求匯出時使用。")]
    public CsvResult ExportEventStudy(
        [Description("輸出的 .csv、.txt 或 .xlsx 檔名。")] string fileName,
        [Description("periods 為逐期明細、estimates 為估計與排除紀錄、averages 為平均異常值和統計量。")] string view = "periods",
        [Description("1–30 組事件，每組填入 code 和 date。與 sampleText 擇一。")] TejEvent[]? events = null,
        [Description("事件期、估計期、模型、基準與缺值設定。")] TejEventOptions? options = null,
        [Description("CSV/TXT 樣本文字，每列為證券代碼、半形逗號、YYYYMMDD，省略標題列。")] string? sampleText = null) =>
        Run(() =>
        {
            var destination = export.Destination(fileName, multiFormat: true);
            return export.Save(destination, TejEventStudy.ExportRows(ReadEvents(events, sampleText, options), view));
        });

    [McpServerTool(Name = "tej_disclosure_links", ReadOnly = true, OpenWorld = false)]
    [Description("產生台灣上市櫃公司財務報告書及法說會的官方查詢入口。")]
    public object DisclosureLinks(
        [Description("台灣證券代碼。")] string code,
        [Description("西元財報年度。省略時使用台北當前年份。")] int? year = null) =>
        Run(() =>
        {
            var company = TejClient.CompanyCode(code);
            var fiscalYear = year ?? TejClient.TaipeiToday().Year;
            if (fiscalYear < 1912 || fiscalYear > TejClient.TaipeiToday().Year)
                throw new TejException("年度須為 1912 年至今年的西元年份。");
            var rocYear = (fiscalYear - 1911).ToString(CultureInfo.InvariantCulture);
            return new
            {
                code = company,
                fiscalYear,
                financialReportsUrl = $"https://doc.twse.com.tw/server-java/t57sb01?step=1&colorchg=1&co_id={Uri.EscapeDataString(company)}&year={rocYear}&seamon=&mtype=A",
                investorConferencePortalUrl = "https://mops.twse.com.tw/",
                investorConferenceMenu = "單一公司 → 營運概況 → 法人說明會一覽表",
            };
        });

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

    private TejResult ReadPanel(TejReportDefinition? report, string? savedName, string? start, string? end,
        int limit, string[]? codes, string? watchlistName)
    {
        if ((report == null) == (savedName == null))
            throw new TejException("請在報表設定與已儲存報表名稱擇一。");
        var definition = report ?? savedReports.Get(savedName!).Definition;
        definition = definition with { StartDate = start ?? definition.StartDate, EndDate = end ?? definition.EndDate };
        if (codes != null || watchlistName != null)
            definition = definition with { Codes = codes, WatchlistName = watchlistName, Industry = null, Market = null };
        TejReports.ValidateDefinition(definition);
        if (definition.Industry == null)
            return reports.Panel(definition, SelectStocks(definition.WatchlistName, definition.Codes).Codes, limit);
        var sample = reports.Companies(null, definition.Industry, definition.Market, TejReports.MaximumPanelCodes);
        if (sample.Truncated)
            throw new TejException("產業樣本超過 200 家。請加上上市別或指定公司代碼。");
        if (sample.Rows.Length == 0)
            throw new TejException("找不到符合產業與上市別的公司。");
        return reports.Panel(definition, Array.ConvertAll(sample.Rows, row => TejReports.Text(row[0])), limit);
    }

    private TejEventStudyResult ReadEvents(TejEvent[]? events, string? sampleText, TejEventOptions? options)
    {
        if ((events == null) == (sampleText == null))
            throw new TejException("請在事件陣列與 CSV/TXT 樣本文字擇一。");
        return eventStudy.Run(events ?? TejEventStudy.ReadSamples(sampleText!), options);
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
