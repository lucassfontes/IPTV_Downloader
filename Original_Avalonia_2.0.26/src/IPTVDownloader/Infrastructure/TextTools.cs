using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IPTVDownloader.Infrastructure;

public static partial class TextTools
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"[\\/:*?""<>|]+")]
    private static partial Regex InvalidFileCharsRegex();

    public static string Normalize(string? value)
    {
        var source = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        }
        return SpacesRegex().Replace(sb.ToString(), " ").Trim();
    }

    public static string SafeFileName(string? value)
    {
        // Remove acentos/diacríticos dos nomes salvos no disco, mantendo
        // maiúsculas/minúsculas e o texto legível. Ex.: "Gênesis" -> "Genesis".
        var source = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark &&
                category != UnicodeCategory.SpacingCombiningMark &&
                category != UnicodeCategory.EnclosingMark)
            {
                sb.Append(ch);
            }
        }

        var withoutAccents = sb.ToString().Normalize(NormalizationForm.FormC);
        var cleaned = InvalidFileCharsRegex().Replace(withoutAccents, "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned;
    }

    public static string Clip(string? value, int max)
    {
        var text = SpacesRegex().Replace(value ?? string.Empty, " ").Trim();
        if (text.Length <= max) return text;
        return text[..Math.Max(1, max - 1)].TrimEnd(' ', ',', '.', ';', ':', '-') + "…";
    }
}
