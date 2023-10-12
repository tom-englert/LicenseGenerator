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

    public static bool IsApache2LicenseUrl(this string? value)
    {
        return value?.Contains(Constants.ApacheLicenseUrl, StringComparison.OrdinalIgnoreCase) == true;
    }
    public static bool IsApache2LicenseText(this string? value)
    {
        return value?.Contains(Constants.ApacheLicenseTitle, StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains(Constants.ApacheLicenseUrl, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsMicrosoftNetLibrary(this string? value)
    {
        return value?.Equals(Constants.MicrosoftNetLibraryUrl, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsMitLicenseText(this string? value)
    {
        return value?.Contains(Constants.MitLicenseTitle, StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string FormatLicenseText(this string text)
    {
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Split('\n')
                .Aggregate(new StringBuilder(), (builder, s) => builder.AppendLine($"> {s.TrimEnd()}"))
                .ToString();
    }
}
