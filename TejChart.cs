using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TejMcp;

internal static class TejChart
{
    private static readonly Color[] Colors = [
        Color.FromArgb(25, 94, 170), Color.FromArgb(187, 76, 40), Color.FromArgb(0, 122, 105),
        Color.FromArgb(119, 71, 152), Color.FromArgb(164, 100, 10), Color.FromArgb(77, 94, 111),
        Color.FromArgb(153, 52, 98), Color.FromArgb(34, 128, 152),
    ];

    internal static void Write(Stream stream, TejResult data)
    {
        if (data.Truncated || data.Columns.Length != 3 || !TejReports.IsNumeric(data.Columns[2]))
            throw new TejException("繪圖需選取一個數值欄位，且來源資料不能截斷。");
        var points = data.Rows.Select(row => (Code: TejReports.Text(row[0]), Period: TejReports.Text(row[1]),
            Value: TejReports.Number(row[2]))).ToArray();
        var series = points.GroupBy(point => point.Code).Where(group => group.Any(point => point.Value != null)).ToArray();
        var periods = points.Select(point => point.Period).Distinct().OrderBy(period => period, StringComparer.Ordinal).ToArray();
        if (series.Length is < 1 or > 8 || periods.Length < 1)
            throw new TejException("圖表需有 1 到 8 組包含有效數值的序列。");

        var numbers = points.Where(point => point.Value.HasValue).Select(point => (double)point.Value!.Value).ToArray();
        var bottom = numbers.Min();
        var top = numbers.Max();
        var spread = top - bottom;
        var padding = spread > 0 ? spread * 0.06 : Math.Max(Math.Abs(top) * 0.05, 0.01);
        var tickSize = (spread + 2 * padding) / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(tickSize)));
        var step = new[] { 1d, 2d, 2.5d, 5d, 10d }.First(value => value * magnitude >= tickSize) * magnitude;
        bottom = Math.Floor((bottom - padding) / step) * step;
        top = Math.Ceiling((top + padding) / step) * step;
        if (numbers.All(value => value >= 0))
            bottom = Math.Max(0, bottom);
        if (numbers.All(value => value <= 0) && numbers.Any(value => value < 0))
            top = Math.Min(0, top);

        using var bitmap = new Bitmap(1080, 620);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var heading = new Font("Microsoft JhengHei", 19, FontStyle.Bold);
        using var label = new Font("Microsoft JhengHei", 10);
        using var ink = new SolidBrush(Color.FromArgb(39, 48, 63));
        using var grid = new Pen(Color.FromArgb(222, 228, 235));
        using var centered = new StringFormat { Alignment = StringAlignment.Center };
        using var rightAligned = new StringFormat { Alignment = StringAlignment.Far };
        var plot = new RectangleF(112, 100, 918, 406);
        float X(int index) => plot.Left + plot.Width * (periods.Length == 1 ? .5f : (float)index / (periods.Length - 1));
        float Y(double value) => plot.Bottom - plot.Height * (float)((value - bottom) / (top - bottom));

        var field = data.Columns[2];
        var title = string.IsNullOrEmpty(field.Unit) ? field.Title : $"{field.Title} ({field.Unit})";
        graphics.DrawString(title, heading, ink, 32, 22);
        graphics.DrawString(field.Code, label, ink, 34, 59);
        var ticks = (int)Math.Round((top - bottom) / step);
        for (var tick = 0; tick <= ticks; tick++)
        {
            var value = bottom + step * tick;
            var y = Y(value);
            graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            graphics.DrawString((Math.Abs(value) < step * 1e-9 ? 0 : value).ToString("G5", CultureInfo.InvariantCulture),
                label, ink, plot.Left - 10, y - 8, rightAligned);
        }
        for (var tick = 0; tick < Math.Min(7, periods.Length); tick++)
        {
            var index = periods.Length <= 7 ? tick : tick * (periods.Length - 1) / 6;
            graphics.DrawString(periods[index], label, ink, X(index), plot.Bottom + 8, centered);
        }

        for (var index = 0; index < series.Length; index++)
        {
            using var line = new Pen(Colors[index], 2.5f);
            using var dot = new SolidBrush(Colors[index]);
            var values = series[index].ToDictionary(point => point.Period, point => point.Value);
            var legendX = plot.Left + index % 4 * plot.Width / 4;
            var legendY = 554 + index / 4 * 24;
            graphics.DrawLine(line, legendX, legendY + 8, legendX + 25, legendY + 8);
            graphics.DrawString(series[index].Key, label, ink, legendX + 33, legendY);
            PointF? previous = null;
            for (var period = 0; period < periods.Length; period++)
            {
                if (!values.TryGetValue(periods[period], out var value) || !value.HasValue)
                {
                    previous = null;
                    continue;
                }
                var point = new PointF(X(period), Y((double)value.Value));
                if (previous is { } start)
                    graphics.DrawLine(line, start, point);
                if (previous == null || periods.Length <= 30)
                    graphics.FillEllipse(dot, point.X - 3, point.Y - 3, 6, 6);
                previous = point;
            }
        }
        bitmap.Save(stream, ImageFormat.Png);
    }
}
