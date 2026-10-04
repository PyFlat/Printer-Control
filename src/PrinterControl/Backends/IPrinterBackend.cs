using MacroDeck.Localization;
using Microsoft.Extensions.DependencyInjection;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends;

// A kind of printer server. Adding one: start from the template in tests/PrinterControl.Tests/Backends/
// Example/ and follow "Adding a backend" in AGENTS.md; it is done when its BackendContract tests pass.
internal interface IPrinterBackend
{
	// Stored in every config entry; never rename one.
	string Id { get; }

	LocalizedText Name { get; }

	bool RequiresApiKey { get; }

	// Asked during setup, without credentials. Throws PrinterException(Unreachable) when nothing answers.
	Task<bool> DetectAsync(Uri baseUri, CancellationToken cancellationToken);

	IPrinterSetup CreateSetup(Uri baseUri, bool editing);

	PrinterConnection Connect(PrinterConfig config, HttpClient http, ILogger logger);
}

internal sealed class PrinterBackends(IEnumerable<IPrinterBackend> backends)
{
	public IReadOnlyList<IPrinterBackend> All { get; } = [.. backends];

	public IPrinterBackend? Find(string? id) => All.FirstOrDefault(b => b.Id == id);
}

internal static class PrinterBackendRegistration
{
	public static IServiceCollection AddPrinterBackends(this IServiceCollection services)
	{
		services.AddSingleton<IPrinterBackend, OctoPrintBackend>();
		services.AddSingleton<PrinterBackends>();
		services.AddSingleton<PrinterRegistry>();
		return services;
	}
}
