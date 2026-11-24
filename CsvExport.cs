using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TejMcp;

public sealed record CsvResult(string Path, int RowCount, bool Truncated);

internal sealed class CsvExport(TejConfiguration configuration)
{
    internal string Destination(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 120 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new TejException("請提供 CSV 檔名，例如 prices.csv。");

        var stem = fileName.Split('.')[0].TrimEnd();
        if (stem.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(?:COM|LPT)[0-9¹²³]\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new TejException("此檔名為 Windows 保留名稱。");

        var destination = Path.Combine(configuration.ExportDirectory, fileName);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new TejException("同名檔案已存在，請選擇另一個 CSV 檔名。");
        return destination;
    }

    internal CsvResult Save(string destination, TejResult data)
    {
        Directory.CreateDirectory(configuration.ExportDirectory);
        var temporary = Path.Combine(configuration.ExportDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine(string.Join(",", data.Columns.Select(column => Cell(column.Code))));
                foreach (var row in data.Rows)
                    writer.WriteLine(string.Join(",", row.Select(Cell)));
            }
            File.Move(temporary, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            throw new TejException("同名檔案已存在，請選擇另一個 CSV 檔名。");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return new CsvResult(destination, data.Rows.Length, data.Truncated);
    }

    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var trimmed = text.TrimStart();
        if (value is string && trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0)
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
