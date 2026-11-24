using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TejMcp;

public sealed record TejSavedReport(string Name, TejReportDefinition Definition);

internal sealed class TejSavedReports(TejConfiguration configuration)
{
    internal TejSavedReport Save(string name, TejReportDefinition definition)
    {
        name = NormalizeName(name);
        TejReports.ValidateDefinition(definition);
        var saved = new TejSavedReport(name, definition);
        var path = Location(name);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(configuration.ReportDirectory);
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, saved);
            try
            {
                File.Move(staging, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Replace(staging, path, null);
            }
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TejException("無法儲存本機報表設定。");
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    internal TejSavedReport Get(string name)
    {
        name = NormalizeName(name);
        try
        {
            return Read(Location(name));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TejException($"找不到報表設定「{name}」。");
        }
    }

    internal TejSavedReport[] List()
    {
        try
        {
            if (!Directory.Exists(configuration.ReportDirectory))
                return [];
            return Directory.EnumerateFiles(configuration.ReportDirectory, "*.json").Select(Read)
                .OrderBy(report => report.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TejException("無法讀取本機報表設定清單。");
        }
    }

    private static TejSavedReport Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<TejSavedReport>(stream) ?? throw new JsonException();
        }
        catch (Exception error) when ((error is IOException && error is not (FileNotFoundException or DirectoryNotFoundException))
            || error is UnauthorizedAccessException or JsonException)
        {
            throw new TejException("無法讀取本機報表設定。");
        }
    }

    private string Location(string name)
    {
        using var hash = SHA256.Create();
        var key = hash.ComputeHash(Encoding.UTF8.GetBytes(name.ToUpperInvariant()));
        return Path.Combine(configuration.ReportDirectory, BitConverter.ToString(key).Replace("-", "") + ".json");
    }

    private static string NormalizeName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new TejException("報表名稱須為 1 到 40 個字元。");
        return name;
    }
}
