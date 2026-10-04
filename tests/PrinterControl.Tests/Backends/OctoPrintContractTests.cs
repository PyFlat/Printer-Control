using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using PrinterControl.Backends;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests.Backends;

[TestFixture]
public sealed class OctoPrintContractTests : BackendContract
{
	private protected override IPrinterBackend CreateBackend(IHttpClientFactory http) =>
		new OctoPrintBackend(http, Serilog.Core.Logger.None);

	private protected override async Task<IFakePrinterServer> StartServerAsync() => await FakeOctoPrint.StartAsync();
}

[TestFixture]
public sealed class OctoPrintDetectionTests
{
	// Moonraker's octoprint_compat answers /api/version the way OctoPrint does, with its own name in the text.
	[Test]
	public async Task Moonrakers_imitation_of_the_version_endpoint_is_not_octoprint()
	{
		var (app, baseUri) = await FakeServerHost.StartAsync(app =>
			app.MapGet("/api/version", () => Results.Json(new { server = "1.5.0", api = "0.1", text = "OctoPrint (Moonraker v0.9.3)" })));
		await using (app)
		{
			Assert.That(
				await new OctoPrintBackend(new TestHttpClientFactory(), Serilog.Core.Logger.None).DetectAsync(baseUri, CancellationToken.None),
				Is.False);
		}
	}
}
