using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PrinterControl.Core;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests.Backends.Example;

// The fake for ExampleBackend, and the template for a new backend's fake: it answers the detection
// endpoint, serves the state the test set, and counts commands.
internal sealed class ExampleServer : IFakePrinterServer
{
	private readonly WebApplication _app;
	private readonly ConcurrentQueue<string> _events = new();
	private int _commands;
	private string _state = "ready";

	private ExampleServer(WebApplication app, Uri baseUri)
	{
		_app = app;
		BaseUri = baseUri;
	}

	public Uri BaseUri { get; }

	public string? ApiKey => null;

	public int CommandsReceived => Volatile.Read(ref _commands);

	public static async Task<ExampleServer> StartAsync()
	{
		ExampleServer? server = null;
		var (app, baseUri) = await FakeServerHost.StartAsync(app =>
		{
			app.MapGet("/example/info", () => Results.Json(new { software = "example-printer", name = "Example MK1" }));
			app.MapGet("/example/status", () => server!.Status());
			app.MapPost("/example/command", () =>
			{
				Interlocked.Increment(ref server!._commands);
				return Results.NoContent();
			});
		});
		server = new ExampleServer(app, baseUri);
		return server;
	}

	public Task ReportAsync(PrinterStatus status)
	{
		_state = status switch
		{
			PrinterStatus.Operational => "ready",
			PrinterStatus.Printing => "printing",
			PrinterStatus.Paused => "paused",
			PrinterStatus.Disconnected => "offline",
			_ => throw new ArgumentOutOfRangeException(nameof(status)),
		};
		return Task.CompletedTask;
	}

	public Task RaiseAsync(PrinterEventKind kind)
	{
		_events.Enqueue(kind switch
		{
			PrinterEventKind.PrintStarted => "print-started",
			PrinterEventKind.PrintDone => "print-done",
			PrinterEventKind.PrintPaused => "print-paused",
			_ => throw new ArgumentOutOfRangeException(nameof(kind)),
		});
		return Task.CompletedTask;
	}

	// Events are handed out once, like a server's own event queue.
	private IResult Status()
	{
		var events = new List<string>();
		while (_events.TryDequeue(out var name))
		{
			events.Add(name);
		}

		return Results.Json(new
		{
			state = _state,
			hotend = new { actual = 215, target = 215 },
			bed = new { actual = 60, target = 60 },
			events,
		});
	}

	public async ValueTask DisposeAsync()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
	}
}
