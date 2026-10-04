using PrinterControl.Core;

namespace PrinterControl.Backends.OctoPrint;

internal static class OctoPrintUrls
{
	public static Uri PushSocket(Uri baseUri)
	{
		var builder = new UriBuilder(new Uri(PrinterUrls.Normalize(baseUri), "sockjs/websocket"))
		{
			Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
		};
		return builder.Uri;
	}
}
