using MacroDeck.Sdk.ConfigFlow;
using PrinterControl.Backends;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.ConfigFlow;

internal static class PrinterConfigReader
{
	public static async Task<IReadOnlyList<PrinterConfig>> ReadAsync(
		IIntegrationConfig config,
		PrinterBackends backends,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var printers = new List<PrinterConfig>();
		foreach (var entry in await config.GetEntriesAsync(cancellationToken))
		{
			var backendId = await config.GetStringAsync(entry.Id, ConfigKeys.Backend, cancellationToken);
			var backend = backends.Find(string.IsNullOrEmpty(backendId) ? ConfigKeys.DefaultBackend : backendId);
			if (backend is null)
			{
				logger.Warning("Skipping printer entry {Title}: this version does not know the printer type {Backend}.", entry.Title, backendId);
				continue;
			}

			var url = await config.GetStringAsync(entry.Id, ConfigKeys.Url, cancellationToken);
			var apiKey = await config.GetSecretAsync(entry.Id, ConfigKeys.ApiKey, cancellationToken);
			if (!PrinterUrls.TryParse(url, out var baseUri) || (backend.RequiresApiKey && string.IsNullOrEmpty(apiKey)))
			{
				logger.Warning("Skipping printer entry {Title}: it has no usable URL or API key.", entry.Title);
				continue;
			}

			var webcamUrl = await config.GetStringAsync(entry.Id, ConfigKeys.WebcamUrl, cancellationToken);
			printers.Add(new PrinterConfig(
				entry.Id,
				backend.Id,
				entry.Title,
				baseUri,
				string.IsNullOrEmpty(apiKey) ? null : apiKey,
				string.IsNullOrWhiteSpace(webcamUrl) ? null : webcamUrl.Trim()));
		}

		return printers;
	}
}
