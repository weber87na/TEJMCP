using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TejMcp;

internal static class Program
{
    private static readonly JsonSerializerOptions CliJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;
        try
        {
            var configuration = new TejConfiguration();
            configuration.InstallAssemblyResolver();
            var client = new TejClient(configuration);
            var export = new CsvExport(configuration);
            if (args.Length == 0 || args is ["mcp"])
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
                builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
                builder.Services.AddMcpServer().WithStdioServerTransport()
                    .WithTools(new TejTools(client, export, new TejWatchlists(configuration), new TejSavedReports(configuration)));
                using var host = builder.Build();
                await host.RunAsync().ConfigureAwait(false);
                return 0;
            }

            if (args is ["configure"])
            {
                ConfigureCredentials();
                return 0;
            }

            if (args is ["help" or "--help" or "-h"])
            {
                Console.WriteLine(Usage);
                return 0;
            }

            object result = args[0] switch
            {
                "status" when args.Length == 1 => client.Status(),
                "auth-check" when args.Length == 1 => VerifyAuth(client),
                "tables" when args.Length <= 4 => client.ListTables(Argument(args, 1) ?? "", Argument(args, 2) ?? "TW", Limit(args, 3, 30)),
                "fields" when args.Length is 2 or 3 => client.Describe(args[1], Argument(args, 2) ?? "TW"),
                "query" when args.Length is >= 5 and <= 8 => client.Query(args[1], CsvList(args[2]), args[3], args[4],
                    ListArgument(args, 5), Argument(args, 7) ?? "TW", Limit(args, 6, 100)),
                "export" when args.Length is >= 6 and <= 9 => Export(client, export, args),
                _ => throw new TejException("命令或參數不正確。請執行 TejMcp.exe help。"),
            };
            Console.WriteLine(JsonSerializer.Serialize(result, CliJson));
            return 0;
        }
        catch (TejException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Console.Error.WriteLine("無法載入所需的 .NET 組件。請檢查 TEJ Pro 安裝目錄和建置輸出。");
            return 1;
        }
    }

    private static object VerifyAuth(TejClient client)
    {
        client.CheckAuthentication();
        return new { authenticated = true };
    }

    private static CsvResult Export(TejClient client, CsvExport export, string[] args)
    {
        var destination = export.Destination(args[5]);
        var result = client.Query(args[1], CsvList(args[2]), args[3], args[4],
            ListArgument(args, 6), Argument(args, 8) ?? "TW", Limit(args, 7, 10_000));
        return export.Save(destination, result);
    }

    private static string? Argument(string[] args, int index) => args.Length > index && args[index] != "-" ? args[index] : null;
    private static string[] CsvList(string value) => value.Split(',');
    private static string[]? ListArgument(string[] args, int index) => Argument(args, index) is { } value ? CsvList(value) : null;

    private static int Limit(string[] args, int index, int fallback) => Argument(args, index) is not { } value
        ? fallback : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            ? limit : throw new TejException("筆數上限須為整數。");

    private static void ConfigureCredentials()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new TejException("請在本機互動式終端機執行 configure。");

        Console.Write("TEJ 帳號：");
        var user = (Console.ReadLine() ?? "").Trim();
        Console.Write("TEJ 密碼：");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Length--;
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
        Console.WriteLine();
        TejCredentials.Save(user, password.ToString());
        password.Clear();
        Console.WriteLine("已儲存由目前 Windows 帳號加密的 TEJ 憑證。");
    }

    private const string Usage = """
        TejMcp.exe mcp
        TejMcp.exe status
        TejMcp.exe configure
        TejMcp.exe auth-check
        TejMcp.exe tables [關鍵字] [市場] [筆數]
        TejMcp.exe fields 表格代碼 [市場]
        TejMcp.exe query 表格代碼 公司代碼清單 起日 迄日 [欄位清單] [筆數] [市場]
        TejMcp.exe export 表格代碼 公司代碼清單 起日 迄日 檔名.csv [欄位清單] [筆數] [市場]

        日期使用 YYYY-MM-DD。多個公司代碼或欄位以逗號分隔。使用 - 跳過選用參數。
        """;
}
