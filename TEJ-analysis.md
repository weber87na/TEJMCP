# TEJ Pro 組件分析

使用 [ILSpy CLI](https://github.com/icsharpcode/ILSpy/tree/master/ICSharpCode.ILSpyCmd) 檢查 `C:\TejPro` 的組件。實作以本機安裝版本為準。

| 組件 | 觀察 |
| --- | --- |
| `TEJPro\TEJPro.exe` | .NET 啟動器，檔案版本 `1.0.0.0` |
| `TEJPro\TEJProRun.exe` | 主程式 `1.0.0.154`，設定目標為 .NET Framework 4.7。`Frm_Main`、`Frm_Menu` 和 `UserCtrl_ViewData` 分別包含主視窗、資料選單及查詢格線的程式碼。 |
| `TEJPro\SmartWizard.exe`、`TEJ Smart Wizard\TEJAddin.exe` | Smart Wizard 及 Excel 工作流程。`TEJAddin.LoginForm.ButtonLogin_Click` 串接獨立的認證介面，從成功的登入回應取得資料主機與帳號類型。 |
| `TEJ Smart Wizard\TEJProClient.dll` | `1.2.0.5`，組件標記 `Required32Bit`。公開類別 `CommandClass` 提供 `GFunStr_Login` 及 `GFunStr_GetAPIData`，資料結果為 `DataTable`。 |
| `TEJ Smart Wizard\TEJProConnector.dll` | `MySchemaLibrary.GetLogin` 呼叫 `CommandClass.GFunStr_Login`。`ExecuteDataTable` 的錯誤路徑會呼叫 `Interaction.MsgBox`，CLI 因此直接使用底層 `CommandClass`。 |

## CLI 的查詢路徑

1. 讀取現有安裝的 `CommonFolder\TejCommonSetting\CustomerSetting_pro.ini`，向其中的 HTTPS 服務呼叫原廠登入方法。
2. 依 `LoginForm.ButtonLogin_Click` 的回應格式取得授權識別、產品類型及查詢主機。校園帳號識別依同一方法的格式建立。
3. 以 `sys_alltopic_資料項目定義`、`sys_odtopic`、`TableColumn_Info` 和 `sys_odcollist` 篩選目前帳號的資料表和欄位，並套用市場及公司群組的授權清單。市場授權若依國別及代號類型設定，例如 `TW-GSTOCK`，同時核對該類型和該資料表的證券代碼清單。
4. 以固定的唯讀查詢樣板產生公司代碼、日期及欄位篩選，呼叫原廠 `GFunStr_GetAPIData`，將 `DataTable` 轉成 JSON 或 CSV。日、週、月、季和年日期格式參考 `CommandClass.SendCommand` 的區間處理。

安裝目錄和第三方 DLL 由執行時載入。建置輸出僅包含自製 CLI 與 NuGet 相依程式庫。已用實際帳號驗證登入和 `WPRCD` 的 `TW-GSTOCK` 授權查詢。

## 重現部分靜態分析

```powershell
ilspycmd -l c 'C:\TejPro\TEJPro\TEJProRun.exe'
ilspycmd -t TEJProClient.CommandClass 'C:\TejPro\TEJ Smart Wizard\TEJProClient.dll'
ilspycmd -t TEJProConnector.MySchemaLibrary 'C:\TejPro\TEJ Smart Wizard\TEJProConnector.dll'
ilspycmd -t TEJAddin.LoginForm 'C:\TejPro\TEJ Smart Wizard\TEJAddin.exe'
```
