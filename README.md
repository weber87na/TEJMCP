# TEJ Pro CLI 與 MCP

`TejMcp.exe` 是純 C# 的 Windows CLI 和 stdio MCP 伺服器。它直接呼叫 `C:\TejPro\TEJ Smart Wizard\TEJProClient.dll`，透過 Smart Wizard 的登入與查詢介面讀取 TEJ 資料。程式只需要安裝好的 TEJ Pro 和有權使用 Smart Wizard 的帳號，TEJ Pro 與 Excel 可以關閉。實作依據見 [反編譯分析](TEJ-analysis.md)。

## 在這台電腦啟用

專案使用 .NET Framework 4.8、x86 和 [官方 C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)。重新編譯時使用 .NET 9 SDK 與 .NET Framework 4.8 Targeting Pack。

```powershell
cd D:\TEJMCP
dotnet restore --locked-mode
dotnet build -c Release --no-restore
$tej = 'D:\TEJMCP\bin\Release\net48\TejMcp.exe'
& $tej status
& $tej configure
& $tej auth-check
```

`configure` 在終端機內隱藏密碼輸入，將憑證用 Windows DPAPI 加密後存入 `%LOCALAPPDATA%\TEJ-MCP\credentials.dat`。MCP 必須以同一個 Windows 帳號執行。也可用程序環境變數 `TEJ_USER` 和 `TEJ_PASSWORD` 提供憑證。`status` 會檢查已安裝的 DLL 和憑證設定，`auth-check` 才會連線確認登入。

### 接到 Codex

在終端機登錄這個本機 stdio 伺服器，然後重新啟動 Codex 或開新工作階段。

```powershell
codex mcp add tej-pro -- D:\TEJMCP\bin\Release\net48\TejMcp.exe mcp
```

其他支援 MCP stdio 的用戶端也可以用同一個執行檔與 `mcp` 參數。

```json
{
  "mcpServers": {
    "tej-pro": {
      "command": "D:\\TEJMCP\\bin\\Release\\net48\\TejMcp.exe",
      "args": ["mcp"]
    }
  }
}
```

完成後可以直接輸入「用 TEJ 查台積電 2330 在 2026 年 11 月 2 日至 6 日的未調整收盤價」。CSV 匯出預設存到 Windows 文件資料夾下的 `TEJ-MCP` 子目錄。

## MCP 工具

| 工具 | 功能 |
| --- | --- |
| `tej_status` | 確認用戶端組件版本、x86 執行狀態、憑證設定、匯出位置及觀察清單位置 |
| `tej_search_tables` | 依中文表名或代碼搜尋目前授權的資料表 |
| `tej_list_fields` | 取得欄位代碼、名稱、單位及主鍵資訊 |
| `tej_query` | 指定公司代碼、日期與欄位，回傳最多 1,000 筆資料 |
| `tej_export_csv` | 將查詢結果存為 UTF-8 CSV，最多 10,000 筆，同名檔案會保留 |

查詢支援單一實體資料表且主鍵為 `COID`、`MDATE` 的資料，預設市場為 `TW`。公司代碼請保留前導零。月、季、半年與年資料依該表週期篩選。回傳的 `truncated` 表示結果超過指定上限，可縮小區間後再查。

## TEJ Pro 操作手冊對照

| 手冊內容 | 對應的 MCP 功能 |
| --- | --- |
| 智能搜尋（第 19 頁） | `tej_search_fields` 按欄名或欄位代碼跨表搜尋，也接受 `#資料表關鍵字`。`tej_search_tables`、`tej_list_fields` 可接續查看已授權的表與欄位。 |
| 個股、多股比較及轉置（第 8–9、29 頁） | `tej_query_periods` 取得公司與日期明細。 `tej_compare_matrix` 將單股跨期或同一期多股轉置，並可按選定的數值欄位排名。 |
| 公司樣本與欄位群組（第 10–11、24–25 頁） | `tej_search_companies` 支援代碼、簡稱、全名、產業與上市別。以 `tej_watchlist_add` 留存固定名單，以報表的 `industry` 重新選取當下產業樣本。`tej_report_save` 和 `tej_report_list` 保存及列出欄位群組或完整報表設定。 |
| 指定日期、最近期、升降冪及頻率（第 12–14、26–27 頁） | `tej_query_periods` 支援單日、多日、區間、最近 N 期、齊頭與非齊頭，以及週、月、季、年彙整。可查公司主檔及含附加主鍵的單一實體表。 |
| 跨資料庫下載與格式設定（第 30–35 頁） | `tej_query_panel` 以公司與期間合併多表，可按來源選擇聚合方式，支援直接傳設定或重跑已存報表。 |
| 繪圖與匯出（第 15–17、47 頁） | `tej_export_data` 匯出 CSV、TXT、XLSX。`tej_export_chart_png` 畫單一數值欄位的時間序列。`tej_export_event_study` 匯出逐期明細、估計與排除紀錄，或平均異常報酬。 |
| 財務報告書與法說會（第 18 頁） | `tej_disclosure_links` 產生財報年度查詢連結與公開資訊觀測站法說會入口。 |
| 事件日挑選與計算（第 36–47 頁） | `tej_search_events` 查歷史金融新聞、特定事件及股票市場大事記。`tej_event_study` 接受自訂事件或 CSV／TXT 樣本文字，回傳異常報酬、累計值與排除原因。 |

手冊中的安裝與帳號認證由 TEJ 照原流程辦理。Excel 外掛的「小 a」、儲存格連動、巨集與活頁簿更新屬於桌面外掛功能。本機 MCP 的對應操作是重跑已儲存的報表設定，再輸出到新檔。既有 Codex 連線可在重新連線或開啟新工作階段後載入新增工具。

### 日期、跨表與匯出口徑

- `tej_query_periods` 的 `options.dates` 接受最多 100 個 `YYYY-MM-DD`。日、週資料按實際日期鍵比對，月、季、半年資料按月份鍵比對，年資料按年比對。 `options.latest` 接受 1–100。設 `aligned: true` 時先從選定公司集合取共同的日期清單，再回傳各公司在這組日期已有的列。省略時，各公司獨立取最近期。
- 週、月、季、年彙整使用曆週（週一開始）、曆月、曆季及曆年。 `first` 和 `last` 取期內第一列或最後一列。 `mean` 和 `sum` 只接受數值欄位，略過個別缺值，整期缺值時保留空白。起訖日在期中時，僅使用所查區間內的資料。
- 跨表報表最多 8 張表、200 個代碼，每張表可選 30 個欄位。 `exact` 依實際日期或期間標籤合併，其他週期先分表彙整再合併。保留任一來源有資料的公司及期間，未匹配留空。欄位以 `表代碼.欄位代碼` 命名。每張表最多讀取 10,000 筆原始列，超過時請縮短區間或減少公司。按期間合併使用查詢當下的資料，公告時點研究需另選公告日欄位並檢查版本。
- 已儲存的設定預設在 `%LOCALAPPDATA%\TEJ-MCP\reports`，可用 `TEJ_REPORT_DIR` 調整。重跑時可覆寫日期及公司樣本。動態產業樣本使用目前 TEJ 主檔的產業分類。歷史研究可根據主檔上市與下市日期挑選固定樣本。
- `tej_export_data` 的 CSV 及 TXT 有 UTF-8 BOM，可調分隔符和空值文字。XLSX 保留代碼前導零，數字儲存為數值。匯出路徑沿用 `TEJ_EXPORT_DIR`，同名檔會保留。

> 比較 2330 與 2317 在 2026 年 8 到 10 月的月營收及每月最後一筆調整收盤價，對齊月份，匯出成「營收與收盤價.xlsx」。

`tej_query_panel` 的 `report` 範例，可直接用於 `tej_export_data`：

```json
{
  "sources": [
    { "tableId": "WSALE", "fields": ["D0001", "D0003"] },
    { "tableId": "WAPRCD1", "fields": ["CLOSE"], "aggregation": "last" }
  ],
  "codes": ["2330", "2317"],
  "startDate": "2026-08-01",
  "endDate": "2026-10-31",
  "period": "month"
}
```

### 事件研究

- `tej_search_events` 可查 `news`（`WNEWS`）、`market_memo`（`WMEMO`）或 `special`（`WMKTEVT`）。新聞按公司代碼，後兩項按事件類別代碼。省略代碼可探索資料來源。 `special` 可用 `dateBasis` 選擇公告日 `announcement` 或事件日 `occurrence`。新聞和大事記的涵蓋日期依 TEJ 資料表而定。
- `tej_event_study` 支援台股日、週、月頻率，使用 TEJ 除權息調整表的簡單報酬率 `ROI`、連續複利報酬率 `ROIB` 或週轉率 `TURNOVER`。週、月採已完成的期間，並以實際期末交易日配對個股與基準。休市事件日預設移至下一個台灣交易日，也可設定排除。
- 預期模式可用平均調整 `mean_adjusted`、市場指數調整 `market_adjusted` 或單一基準 OLS `market_ols`。市場模式預設使用 `Y9999`。事件期個股缺值預設排除，也可指定補零並標記。基準缺值時排除。輸出包含 AR、CAR、AAR、CAAR、每日／每期橫斷面 t 值、估計樣本數及排除原因。t 值以同一相對期間的樣本標準差計算，需自行評估重疊事件及樣本相依性。GARCH 及 Scholes–Williams 模型仍可在原廠事件研究系統中使用。 [TEJ 事件研究技術手冊](https://www.tejwin.com/wp-content/uploads/TEJ%E4%BA%8B%E4%BB%B6%E7%A0%94%E7%A9%B6%E7%B3%BB%E7%B5%B1%E6%8A%80%E8%A1%93%E6%89%8B%E5%86%8A.pdf)

> 以 2026 年 11 月 4 日為 2330 與 2317 的事件日，用加權指數做市場模型，列出事件日前後各 3 個交易日的異常報酬與累積異常報酬。

## 已實作的台股應用

下列工具可直接透過自然語言呼叫。可用公司、資料期間和欄位依你的 TEJ 授權而定。

| 應用與工具 | TEJ 資料表 | 可以直接這樣問 |
| --- | --- | --- |
| 日行情 `tej_daily_prices` | `WPRCD` 未調整日股價、`WAPRCD1` 除權息調整日股價 | 查 2330 和 0050 在 2026 年 11 月 2 日到 6 日的開高低收和成交量，使用未調整股價。 |
| 績效比較 `tej_compare_price_performance` | `WAPRCD1` 除權息調整日股價 | 比較 2330 和 2317 在 2026 年 11 月 2 日到 6 日的區間漲跌和最大回撤，以 0050 為基準。 |
| 月營收追蹤 `tej_monthly_revenue` | `WSALE` 月營收盈餘 | 列出台積電 2026 年 8 月到 10 月的單月營收、年增率、月增率和公告日。 |
| 單季財報比較 `tej_quarterly_financials` | `WIM1AQ` IFRS 以合併為主簡表（單季） | 比較 2330 和 2317 在 2026 年前三季的單季營收、EPS、營業毛利率和營業利益率，列出幣別及發布日。 |
| 三大法人買賣超 `tej_institutional_flows` | `WTINST1` 三大法人買賣超 | 查 2330 和 2317 在 2026 年 11 月 2 日到 6 日每天的外資、投信和自營商買賣超，再按公司加總。 |
| 同日估值 `tej_valuation_snapshot` | `WPRCD` 未調整日股價 | 以 2026 年 11 月 7 日為截止日，比較 2330 和 2317 在同一交易日的本益比、股價淨值比和殖利率。 |
| 永續評等追蹤 `tej_tesg_ratings` | `WTESG` TESG 永續發展指標主表 | 查 2330 在 2025 到 2026 年的 TESG 等級、公告日和環境、社會、公司治理分數。 |
| 研究報表 `tej_export_report_csv` | 依報表類型選用上方資料表 | 把 2330 在 2026 年 8 月到 10 月的月營收另存成「台積電月營收.csv」。 |

也可以一次串多個工具。例如「幫我做 2330 和 2317 的研究摘要。先整理 2026 年前三季 EPS 和毛利率，再列 8 月到 10 月的營收年增率，最後比較 11 月 2 日到 6 日的法人買賣超。」日資料、月營收與季報都有自己的期間欄位，月營收、財報及 TESG 也會提供各自的公告日。TEJ 回傳的是查詢當下可取得的資料版本，歷史回測若要重現當時可知資訊，還需核對公告日及當時的資料版本。 [TEJ 月營收定義](https://www.tej.com.tw/tejdoc/db-document/tw/wsale.html)、[TEJ IFRS 單季財報定義](https://www.tej.com.tw/tejdoc/db-document/tw/wim1aq.html)、[TEJ 的歷史時點資料說明](https://tquant.tejwin.com/)、[TESG 評等季度與公告日](https://www.tejwin.com/qa-category/esg%E6%B0%B8%E7%BA%8C/)

### 計算和匯出口徑

- **績效比較**使用各有資料標的（含基準）都具有效調整收盤價的交易日，以首尾兩日計算區間漲跌，在同一組交易日計算最大回撤。回傳實際比較區間及 `missingCodes`。基準差以百分點表示，計算未納入交易成本。 [TEJ 還原股價說明](https://www.tejwin.com/insight/%E9%82%84%E5%8E%9F%E8%82%A1%E5%83%B9/)
- **三大法人買賣超**以千股回傳。區間合計按公司加總各日的 TEJ 欄位，單項資料有缺值時對應的區間合計也保留缺值。日資料超過上限時會要求縮短區間，避免加總到不完整的結果。 [TEJ 籌碼資料說明](https://tquant.tejwin.com/%E8%B3%87%E6%96%99%E9%9B%86/)
- **同日估值**從截止日往前最多 14 天，在有資料的最近交易日比較，並列出當日沒有資料的代碼。採用 TEJ 本益比 `PER-TEJ`、TEJ 股價淨值比 `PBR-TEJ` 及交易所殖利率 `DIV_YID`。 [TEJ 估值欄位定義](https://api.tej.com.tw/document_rest.html)
- **CSV 匯出**可選 `daily_prices`、`adjusted_prices`、`monthly_revenue`、`quarterly_financials`、`institutional_flows`、`valuation`、`tesg_ratings`。法人及估值匯出每日明細。檔案使用 UTF-8 BOM，每份最多 10,000 筆，同名檔案會保留。在 Excel 匯入時可把 `COID` 設為文字，保留 `0050` 等代碼的前導零。

## 觀察清單與盤後摘要

| 工具 | 功能 |
| --- | --- |
| `tej_watchlist_list` | 列出本機所有清單和證券代碼 |
| `tej_watchlist_add` | 建立清單或加入證券，保留既有代碼和順序 |
| `tej_watchlist_remove` | 從指定清單移除證券 |
| `tej_post_market_summary` | 使用清單或一組證券代碼取得同一天的日行情、成交量倍數和法人買賣超 |

**可以直接這樣問**

1. 「建立『台股觀察』清單，加入 2330、2317、2454。」
2. 「幫我做『台股觀察』的盤後摘要，列出收盤價、漲跌幅、成交量相對前 20 日均量的倍數，以及三大法人買賣超。」
3. 「用同一份清單查 2026 年 11 月 6 日的盤後摘要。」

每份清單最多 30 檔，省略清單名稱時使用「預設」。清單跨 MCP 工作階段保留，預設存放在 `%LOCALAPPDATA%\TEJ-MCP\watchlists.json`。可透過 `TEJ_WATCHLIST_FILE` 設定其他本機檔案，每個使用同一路徑的 MCP 程序會讀到同一份清單。

摘要的 `asOfDate` 預設為台北當天，結果會標示實際使用的交易日。台北時間 13:33 前，最近交易日上限為前一天，涵蓋證交所可能延後收盤撮合的情況。遇休市或 TEJ 尚無當日行情時，摘要使用截止日前可取得的最近一個交易日，最多往前查 90 天。 [臺灣證券交易所的交易制度](https://www.twse.com.tw/zh/products/system/trading.html)

- 收盤價、漲跌金額、報酬率和成交量使用 `WPRCD`。成交量倍數為當日成交量除以同一證券前 20 筆交易日紀錄的成交量均值，排除當日。均量資料不足或均量為零時，倍數顯示缺值。
- 法人買賣超使用 `WTINST1` 同一交易日的數值，單位為千股。TEJ 尚未提供當天法人資料時，對應欄位留空。`missingPriceCodes` 與 `missingInstitutionalCodes` 標示缺資料的代碼。

## 條件篩選與排名

工具 `tej_screen_watchlist` 可用觀察清單或指定證券代碼，依**最新已公告的月營收年增率**與**近幾個交易日的外資累計買賣超**篩選，並回傳入選排名和每檔未入選的判定。

> 從「台股觀察」找出最新已公告月營收年增超過 20%、近 5 個交易日外資累計買超的股票，按營收年增率由高到低排序。

| 參數 | 說明 |
| --- | --- |
| `watchlistName` / `codes` | 清單名稱或證券代碼擇一。兩者都省略時使用「預設」清單。 |
| `asOfDate` | 截止日 `YYYY-MM-DD`，預設台北當天。月營收只採用在截止日或之前公告的紀錄。 |
| `revenueYoYAbove` | 月營收年增率的嚴格下限。預設 `20` 代表須超過 20%。 |
| `foreignNetAbove` / `foreignTradingDays` | 外資累計買賣超的嚴格下限與窗口交易日數。預設 `0` 千股及 `5` 個交易日，日數可設 1–60。 |
| `sortBy` | `revenue_yoy` 依營收年增率由高到低，`foreign_net` 依外資累計買賣超由高到低。數值相同時依證券代碼排序。 |

每家公司取 `WSALE` 中公告日已到的最新營收月份，回傳月份、公告日及 TEJ 年增率。搜尋涵蓋截止日往前最多 3,660 天，資料使用查詢當下的 TEJ 版本。外資以 `WTINST1` 對這組股票最後已有數值的日期為終點，依 TEJ 台灣交易日曆往前取指定日數。所有股票使用同一組交易日，結果會列出實際日期。缺少任一日外資數值或缺少最新營收年增率的股票會列在 `excluded`，缺值保留為空。

## CLI 範例

```powershell
& $tej tables '未調整股價'
& $tej fields '表格代碼'
& $tej query '表格代碼' '2330,0050' '2026-11-02' '2026-11-06' '欄位代碼' 100
& $tej export '表格代碼' '2330' '2026-11-02' '2026-11-06' '台積電股價.csv'
```

`fields` 與 `query` 的代碼取自前一步的實際回傳值。CLI 的選用位置參數可用 `-` 跳過。`TEJ_EXPORT_DIR` 可改匯出資料夾。`TEJ_INSTALL_ROOT` 可改執行時安裝根目錄，重新編譯時對應的參數是 `-p:TejInstallRoot=...`。登入主機預設讀取 TEJ 的安裝設定，機構有不同的 HTTPS 登入端點時可在程序環境設定 `TEJ_AUTH_URL`。

現有安裝已驗證 DLL 載入、CLI `status` 和 MCP 握手，並以實際 TEJ 帳號測試上列各項應用及 CSV 寫入。需要額外登入步驟的機構帳號，以 Smart Wizard 的登入方式為前提。
