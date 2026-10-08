using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

internal static class MoonrakerUrls
{
	public static Uri Websocket(Uri baseUri) =>
		new UriBuilder(new Uri(PrinterUrls.Normalize(baseUri), "websocket"))
		{
			Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
		}.Uri;
}
