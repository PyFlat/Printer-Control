using System.Globalization;
using System.Text;

namespace PrinterControl.Core;

// Key comes from the config entry id, so it survives a rename; widgets, variable ids and stream ids store it.
internal sealed record PrinterConfig(Guid EntryId, string Backend, string Title, Uri BaseUri, string? ApiKey, string? WebcamUrl)
{
	public string Key => EntryId.ToString("N", CultureInfo.InvariantCulture);

	public string DisplayName => string.IsNullOrWhiteSpace(Title) ? BaseUri.Host : Title;
}

internal static class Slug
{
	public static string From(string text, string fallback)
	{
		var builder = new StringBuilder(text.Length);
		foreach (var ch in text.Normalize(NormalizationForm.FormD))
		{
			if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			var lower = char.ToLowerInvariant(ch);
			if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
			{
				builder.Append(lower);
			}
			else if (builder.Length > 0 && builder[^1] != '_')
			{
				builder.Append('_');
			}
		}

		var slug = builder.ToString().Trim('_');
		if (slug.Length > 24)
		{
			slug = slug[..24].TrimEnd('_');
		}

		return slug.Length == 0 ? fallback : slug;
	}
}
