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
