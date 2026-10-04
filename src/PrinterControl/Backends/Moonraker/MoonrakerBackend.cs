using MacroDeck.Localization;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends.Moonraker;

// Klipper's API server behind Mainsail and Fluidd.
// https://moonraker.readthedocs.io/en/latest/external_api/introduction/
internal sealed class MoonrakerBackend(IHttpClientFactory httpClients) : IPrinterBackend
{
	public const string BackendId = "moonraker";

	public string Id => BackendId;

	public LocalizedText Name => LocalizedText.FromLiteral("Moonraker (Mainsail, Fluidd)");

	// A trusted client needs no key.
	public bool RequiresApiKey => false;

	public async Task<bool> DetectAsync(Uri baseUri, CancellationToken cancellationToken) =>
		MoonrakerHttp.IsMoonraker(await Http(baseUri).GetAsync("server/info", null, cancellationToken));

	public IPrinterSetup CreateSetup(Uri baseUri, bool editing) => new MoonrakerSetup(Http(baseUri), editing);

	public PrinterConnection Connect(PrinterConfig config, HttpClient http, ILogger logger) => new MoonrakerConnection(config, logger);

	private MoonrakerHttp Http(Uri baseUri) => new(httpClients.CreateClient(PrinterRegistry.HttpClientName), baseUri);
}
