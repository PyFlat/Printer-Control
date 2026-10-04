using MacroDeck.Localization;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends.OctoPrint;

// https://octoprint.org; API docs at https://docs.octoprint.org/en/master/api/.
internal sealed class OctoPrintBackend(IHttpClientFactory httpClients, ILogger logger) : IPrinterBackend
{
	public const string BackendId = "octoprint";

	public string Id => BackendId;

	public LocalizedText Name => LocalizedText.FromLiteral("OctoPrint");

	public bool RequiresApiKey => true;

	public Task<bool> DetectAsync(Uri baseUri, CancellationToken cancellationToken) =>
		Api(baseUri, null).IsOctoPrintAsync(cancellationToken);

	public IPrinterSetup CreateSetup(Uri baseUri, bool editing) =>
		new OctoPrintSetup(baseUri, editing, apiKey => Api(baseUri, apiKey), logger);

	public PrinterConnection Connect(PrinterConfig config, HttpClient http, ILogger connectionLogger) =>
		new OctoPrintConnection(config, http, connectionLogger);

	private OctoPrintApi Api(Uri baseUri, string? apiKey) =>
		new(httpClients.CreateClient(PrinterRegistry.HttpClientName), baseUri, apiKey);
}
