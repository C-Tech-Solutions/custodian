namespace Custodian.Core.Export;

public static class CsvFieldFormatter
{
    public static string Format(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = NeutralizeFormula(value);
        if (sanitized.Contains('"') || sanitized.Contains(',') || sanitized.Contains('\n') || sanitized.Contains('\r'))
        {
            return "\"" + sanitized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return sanitized;
    }

    // File names, paths, and attributes come from the scanned volume and are fully
    // attacker-controllable. Prefix spreadsheet formula triggers so CSV consumers
    // import them as literal text instead of evaluating them.
    private static string NeutralizeFormula(string value)
    {
        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
    }
}
