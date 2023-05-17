using System.Text;

namespace LicenseGenerator;

internal static class ExtensionMethods
{
    public static string? NullWhenEmpty(this string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static IEnumerable<string> ReadLines(this StreamReader streamReader)
    {
        while (streamReader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static bool IsApache2License(this string? value)
    {
        return value?.Contains(Constants.ApacheLicenseUrl, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string FormatLicenseText(this IEnumerable<string> lines)
    {
        return lines
            .Aggregate(new StringBuilder(), (builder, s) => builder.AppendLine($"> {s}"))
            .ToString();
    }
}
