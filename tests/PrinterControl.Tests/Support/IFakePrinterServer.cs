using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrinterControl.Core;

namespace PrinterControl.Tests.Support;

// What the shared backend contract (BackendContract) needs from a backend's fake server.
internal interface IFakePrinterServer : IAsyncDisposable
{
	Uri BaseUri { get; }

	// Stored with the printer; null for a server without a login.
	string? ApiKey { get; }

	// Requests that asked the server to do something (a command, not a read).
	int CommandsReceived { get; }

	// Reports a steady status (Operational, Printing, Paused or Disconnected) with the extruder at
	// 215/215 °C and the bed at 60/60 °C.
	Task ReportAsync(PrinterStatus status);

	Task RaiseAsync(PrinterEventKind kind);
}

internal static class FakeServerHost
{
	public static async Task<(WebApplication App, Uri BaseUri)> StartAsync(Action<WebApplication> map, bool webSockets = false)
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Logging.ClearProviders();
		var app = builder.Build();
		if (webSockets)
		{
			app.UseWebSockets();
		}

		map(app);
		await app.StartAsync();
		var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
		return (app, new Uri(address + "/"));
	}
}

// A web server that is no printer software at all: every path is a 404 page.
internal sealed class NotAPrinterServer : IAsyncDisposable
{
	private readonly WebApplication _app;

	private NotAPrinterServer(WebApplication app, Uri baseUri)
	{
		_app = app;
		BaseUri = baseUri;
	}

	public Uri BaseUri { get; }

	public static async Task<NotAPrinterServer> StartAsync()
	{
		var (app, baseUri) = await FakeServerHost.StartAsync(app =>
			app.Run(http =>
			{
				http.Response.StatusCode = 404;
				http.Response.ContentType = "text/html";
				return http.Response.WriteAsync("<!doctype html><title>404 Not Found</title>");
			}));
		return new NotAPrinterServer(app, baseUri);
	}

	public async ValueTask DisposeAsync()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
	}
}
