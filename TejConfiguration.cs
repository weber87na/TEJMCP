using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TejMcp;

internal sealed class TejConfiguration
{
    internal string InstallRoot { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("TEJ_INSTALL_ROOT") ?? @"C:\TejPro");

    internal string ClientDirectory => Path.Combine(InstallRoot, "TEJ Smart Wizard");

    internal string ExportDirectory { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("TEJ_EXPORT_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TEJ-MCP"));

    internal static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TEJ-MCP");

    internal string WatchlistPath { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("TEJ_WATCHLIST_FILE") ?? Path.Combine(UserDataDirectory, "watchlists.json"));
    internal string ReportDirectory { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("TEJ_REPORT_DIR") ?? Path.Combine(UserDataDirectory, "reports"));

    private string SettingsPath => Path.Combine(InstallRoot, "CommonFolder", "TejCommonSetting", "CustomerSetting_pro.ini");

    internal (Uri AuthUrl, string QueryPath, bool UseAes) ReadConnection()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(SettingsPath))
        {
            var section = "";
            foreach (var rawLine in File.ReadLines(SettingsPath, Encoding.GetEncoding(950)))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                var name = section + "." + line.Substring(0, separator).Trim();
                if (name is "CustomerServer.HttpProtocol" or "CustomerServer.MainServerIP" or
                    "TEJPro.ProAuthApi" or "TEJPro.ProQueryApi" or "TEJPro.ProUseAES")
                    settings[name] = line.Substring(separator + 1).Trim();
            }
        }

        string Get(string key, string fallback) =>
            settings.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

        var authUrl = Environment.GetEnvironmentVariable("TEJ_AUTH_URL") ??
            Get("CustomerServer.HttpProtocol", "https") + "://" +
            Get("CustomerServer.MainServerIP", "tejpro.tej.com.tw") +
            Get("TEJPro.ProAuthApi", "/AuthenticateV1.ashx");
        var queryPath = Get("TEJPro.ProQueryApi", "/api/GetData");
        if (!queryPath.StartsWith("/", StringComparison.Ordinal) ||
            queryPath.StartsWith("//", StringComparison.Ordinal) || queryPath.Contains("\\"))
            throw new TejException("TEJ 查詢 API 路徑格式不正確。");

        var aesSetting = Get("TEJPro.ProUseAES", "1");
        if (aesSetting != "0" && aesSetting != "1" && !bool.TryParse(aesSetting, out _))
            throw new TejException("TEJ 的 ProUseAES 設定值無法辨識。");

        return (HttpsUrl(authUrl), queryPath, aesSetting == "1" ||
            string.Equals(aesSetting, "true", StringComparison.OrdinalIgnoreCase));
    }

    internal static Uri HttpsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new TejException("TEJ 伺服器必須使用有效的 HTTPS 位址。");
        return uri;
    }

    internal void InstallAssemblyResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name is not ("TEJProClient" or "SQLite.NET" or "SmartSQL" or "Newtonsoft.Json"))
                return null;

            var path = Path.Combine(ClientDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}

internal static class TejCredentials
{
    private static string CredentialPath => Path.Combine(TejConfiguration.UserDataDirectory, "credentials.dat");

    internal static bool IsConfigured =>
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEJ_USER")) &&
         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEJ_PASSWORD"))) || File.Exists(CredentialPath);

    internal static (string User, string Password) Read()
    {
        var user = Environment.GetEnvironmentVariable("TEJ_USER");
        var password = Environment.GetEnvironmentVariable("TEJ_PASSWORD");
        if (user != null || password != null)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
                throw new TejException("請同時提供 TEJ_USER 和 TEJ_PASSWORD。");
            return (user!.Trim(), password!);
        }

        if (!File.Exists(CredentialPath))
            throw new TejException("尚未設定 TEJ 帳號。請在本機終端機執行 TejMcp.exe configure。");

        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(CredentialPath), null, DataProtectionScope.CurrentUser);
            try
            {
                var values = Encoding.UTF8.GetString(bytes).Split(new[] { '\0' }, 2);
                if (values.Length != 2 || values[0].Length == 0 || values[1].Length == 0)
                    throw new TejException("TEJ 憑證格式無效，請重新執行 configure。");
                return (values[0], values[1]);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
        catch (CryptographicException)
        {
            throw new TejException("目前的 Windows 帳號無法解密 TEJ 憑證，請重新執行 configure。");
        }
    }

    internal static void Save(string user, string password)
    {
        if (user.Length == 0 || password.Length == 0 || user.Contains("\0") || password.Contains("\0"))
            throw new TejException("請輸入有效的 TEJ 帳號與密碼。");

        var path = CredentialPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(user + "\0" + password);
        try
        {
            File.WriteAllBytes(staging, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            if (File.Exists(path))
                File.Replace(staging, path, null);
            else
                File.Move(staging, path);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }
}

internal sealed class TejException(string message) : Exception(message);
