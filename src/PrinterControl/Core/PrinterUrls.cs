using System.Net;

namespace PrinterControl.Core;

internal static class PrinterUrls
{
	// Accepts what users paste: "octopi.local", "192.168.1.20:5000", "http://host/octoprint/".
	public static bool TryParse(string? input, out Uri uri)
	{
		uri = null!;
		var text = input?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}

		if (!text.Contains("://", StringComparison.Ordinal))
		{
			text = "http://" + text;
		}

		if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
			|| (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
			|| string.IsNullOrEmpty(parsed.Host))
		{
			return false;
		}

		uri = Normalize(parsed);
		return true;
	}

	// A trailing slash, so relative API paths keep a reverse proxy's path prefix.
	public static Uri Normalize(Uri uri)
	{
		var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
		if (!builder.Path.EndsWith('/'))
		{
			builder.Path += "/";
		}

		return builder.Uri;
	}

	// Servers configure the stream relative to themselves ("/webcam/?action=stream"). Macro Deck fetches
	// it from this machine, so a loopback stream means the server's machine and is reached through its host.
	public static Uri? ResolveStream(Uri baseUri, string? streamUrl)
	{
		if (string.IsNullOrWhiteSpace(streamUrl)
			|| !Uri.TryCreate(Normalize(baseUri), streamUrl.Trim(), out var resolved)
			|| (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps)
			|| !string.IsNullOrEmpty(resolved.UserInfo))
		{
			return null;
		}

		return IsLoopback(resolved.Host) && !IsLoopback(baseUri.Host)
			? new UriBuilder(resolved) { Host = baseUri.Host }.Uri
			: resolved;
	}

	private static bool IsLoopback(string host) =>
		string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
		|| (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
