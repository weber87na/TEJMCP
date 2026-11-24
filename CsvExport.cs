using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TejMcp;

public sealed record CsvResult(string Path, int RowCount, bool Truncated);

internal sealed class CsvExport(TejConfiguration configuration)
{
    internal string Destination(string fileName, bool multiFormat = false, bool png = false)
    {
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 120 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(png ? extension == ".png" : extension == ".csv" || (multiFormat && extension is ".txt" or ".xlsx")))
            throw new TejException(png ? "請提供 .png 檔名。" : multiFormat
                ? "請提供 .csv、.txt 或 .xlsx 檔名。" : "請提供 CSV 檔名，例如 prices.csv。");

        var stem = fileName.Split('.')[0].TrimEnd();
        if (stem.Length == 0 || new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, @"\A(?:COM|LPT)[0-9¹²³]\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new TejException("此檔名為 Windows 保留名稱。");

        var destination = Path.Combine(configuration.ExportDirectory, fileName);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new TejException("同名檔案已存在，請選擇另一個檔名。");
        return destination;
    }

    internal CsvResult Save(string destination, TejResult data, string? delimiter = null, string? nullValue = null)
    {
        var extension = Path.GetExtension(destination).ToLowerInvariant();
        if (delimiter != null && (extension == ".xlsx" || delimiter is not ("," or "\t" or "|" or ";")))
            throw new TejException("文字匯出可使用逗號、Tab、直線或分號作為分隔符號。");
        if (nullValue?.Length > 100)
            throw new TejException("空值文字最多 100 個字元。");
        SaveFile(destination, stream =>
        {
            if (extension == ".xlsx")
                WriteWorkbook(stream, data, nullValue);
            else
            {
                var separator = delimiter ?? (extension == ".txt" ? "\t" : ",");
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                writer.WriteLine(string.Join(separator, data.Columns.Select(column => Cell(column.Code))));
                foreach (var row in data.Rows)
                    writer.WriteLine(string.Join(separator, row.Select(value => Cell(value ?? nullValue))));
            }
        });
        return new CsvResult(destination, data.Rows.Length, data.Truncated);
    }

    internal void SaveFile(string destination, Action<Stream> write)
    {
        var temporary = Path.Combine(configuration.ExportDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(configuration.ExportDirectory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                write(stream);
            File.Move(temporary, destination);
        }
        catch (IOException) when (File.Exists(destination))
        {
            throw new TejException("同名檔案已存在，請選擇另一個檔名。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TejException("無法寫入檔案。請確認匯出資料夾與檔名。");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var trimmed = text.TrimStart();
        if (value is string && trimmed.Length > 0 && "=+-@".IndexOf(trimmed[0]) >= 0)
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static void WriteWorkbook(Stream stream, TejResult data, string? nullValue)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        void Part(string path, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Part("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Part("_rels/.rels", $"""
            <Relationships xmlns="{package}"><Relationship Id="rId1" Type="{office}/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Part("xl/workbook.xml", $"""
            <workbook xmlns="{main}" xmlns:r="{office}"><sheets><sheet name="TEJ 資料" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Part("xl/_rels/workbook.xml.rels", $"""
            <Relationships xmlns="{package}"><Relationship Id="rId1" Type="{office}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
            """);

        using var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
        using var xml = XmlWriter.Create(sheet, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartElement("worksheet", main);
        xml.WriteStartElement("sheetData", main);
        WriteRow(data.Columns.Select(column => (object?)column.Code).ToArray(), 1);
        for (var i = 0; i < data.Rows.Length; i++)
            WriteRow(data.Rows[i], i + 2);
        xml.WriteEndElement();
        xml.WriteEndElement();

        void WriteRow(object?[] row, int index)
        {
            xml.WriteStartElement("row", main);
            xml.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
            for (var column = 0; column < row.Length; column++)
            {
                var value = row[column] ?? nullValue;
                if (value == null)
                    continue;
                var label = "";
                for (var number = column + 1; number > 0; number = (number - 1) / 26)
                    label = (char)('A' + (number - 1) % 26) + label;
                xml.WriteStartElement("c", main);
                xml.WriteAttributeString("r", label + index.ToString(CultureInfo.InvariantCulture));
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (value is bool flag)
                {
                    xml.WriteAttributeString("t", "b");
                    xml.WriteElementString("v", main, flag ? "1" : "0");
                }
                else if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal &&
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue) &&
                    !double.IsNaN(numberValue) && !double.IsInfinity(numberValue))
                    xml.WriteElementString("v", main, text);
                else
                {
                    xml.WriteAttributeString("t", "inlineStr");
                    xml.WriteStartElement("is", main);
                    xml.WriteStartElement("t", main);
                    xml.WriteAttributeString("xml", "space", null, "preserve");
                    if (text.Length > 32_767)
                        throw new TejException("有儲存格文字超出 Excel 長度上限。請使用 CSV 或 TXT 匯出。");
                    try
                    {
                        xml.WriteString(XmlConvert.VerifyXmlChars(text));
                    }
                    catch (XmlException)
                    {
                        throw new TejException("有欄位含 Excel 無法顯示的控制字元。請使用 CSV 或 TXT 匯出。");
                    }
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
    }
}
