using Custodian.Core.Presentation;

namespace Custodian.Tui;

internal static class TerminalChartRenderer
{
    public static string Render(ChartDataset? dataset, int width = 40)
    {
        if (dataset is null || dataset.TotalBytes <= 0 || dataset.Slices.Count == 0)
        {
            return "No chart data.";
        }

        var labelWidth = Math.Min(24, Math.Max(12, width / 3));
        var barWidth = Math.Max(8, width - labelWidth - 20);
        var lines = new List<string>
        {
            $"{dataset.Title} ({dataset.TotalSize})"
        };

        foreach (var slice in dataset.Slices)
        {
            var filled = (int)Math.Round(Math.Clamp(slice.Percent, 0, 100) / 100 * barWidth);
            var bar = new string('█', filled).PadRight(barWidth);
            var label = Truncate(slice.Label, labelWidth).PadRight(labelWidth);
            lines.Add($"{label} {bar} {slice.PercentText,6} {slice.FormattedSize}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Truncate(string? text, int length)
    {
        if (text is null)
        {
            return string.Empty;
        }

        if (text.Length <= length)
        {
            return text;
        }

        return length <= 3 ? text[..length] : text[..(length - 3)] + "...";
    }
}
