using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrinterControl.Core;

namespace PrinterControl.Tests.Support;

internal sealed record RecordedCommand(string Method, string Path, JsonElement? Body);

internal sealed class FakeOctoPrint : IFakePrinterServer
{
	public const string ApiKey = "fake-api-key";
	public const string User = "johannes";
	public const string Session = "session-1";
	public const string AppToken = "app-token-1";

	private readonly WebApplication _app;
	private readonly ConcurrentQueue<RecordedCommand> _commands = new();
	private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();
	private readonly TaskCompletionSource<string> _auth = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private long _lastAppKeyPoll;
	private bool _appKeyExpired;

	private FakeOctoPrint(WebApplication app) => _app = app;

	public Uri BaseUri { get; private set; } = null!;

	public IReadOnlyList<RecordedCommand> Commands => [.. _commands];

	public Task<string> AuthReceived => _auth.Task;

	public string SettingsJson { get; set; } = """
		{
		  "appearance": { "name": "Fake MK3S" },
		  "webcam": { "webcamEnabled": true, "streamUrl": "/webcam/?action=stream", "flipH": false, "flipV": false, "rotate90": false, "streamRatio": "16:9" },
		  "temperature": { "profiles": [ { "name": "PLA", "extruder": 215, "bed": 60, "chamber": null }, { "name": "PETG", "extruder": 240, "bed": 85, "chamber": null } ] },
		  "plugins": { "gpiocontrol": { "gpio_configurations": [
		    { "name": "Fan relay", "pin": "17", "active_mode": "active_high", "default_state": "default_off" },
		    { "name": "Light", "pin": "27", "active_mode": "active_low", "default_state": "default_off" }
		  ] } }
		}
		""";

	// What the GPIO Control plugin reports, one entry per configured output; null when it is not installed.
	public string[]? GpioStates { get; set; } = ["off", "off"];

	public string FilesJson { get; set; } = """
		{ "files": [
		  { "name": "benchy.gcode", "display": "benchy.gcode", "path": "benchy.gcode", "type": "machinecode", "origin": "local", "size": 1024, "date": 1700000000 },
		  { "name": "parts", "path": "parts", "type": "folder", "origin": "local", "children": [
		    { "name": "clip.gcode", "display": "clip.gcode", "path": "parts/clip.gcode", "type": "machinecode", "origin": "local", "size": 10, "date": 1800000000 }
		  ] },
		  { "name": "model.stl", "path": "model.stl", "type": "model", "origin": "local" }
		] }
		""";

	// Polls answer "still pending" until the user allows the request in OctoPrint.
	public bool AppKeyAllowed { get; set; }

	public bool DenyAppKey { get; set; }

	// Like OctoPrint's POLL_TIMEOUT: a request not polled for this long is dropped and answers 404.
	public TimeSpan? AppKeyPollTimeout { get; set; }

	public Func<RecordedCommand, IEnumerable<object>>? OnCommand { get; set; }

	public static async Task<FakeOctoPrint> StartAsync()
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Logging.ClearProviders();
		var app = builder.Build();
		var fake = new FakeOctoPrint(app);
		fake.Map(app);
		await app.StartAsync();
		var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
		fake.BaseUri = new Uri(address + "/");
		return fake;
	}

	public ValueTask PushAsync(string json) => _frames.Writer.WriteAsync(json);

	string? IFakePrinterServer.ApiKey => ApiKey;

	int IFakePrinterServer.CommandsReceived => _commands.Count;

	Task IFakePrinterServer.ReportAsync(PrinterStatus status) => PushAsync(status switch
	{
		PrinterStatus.Operational => CurrentFrame("Operational", Operational, tool: 215, toolTarget: 215),
		PrinterStatus.Printing => CurrentFrame("Printing", Printing, 50, "benchy.gcode", 215, 215),
		PrinterStatus.Paused => CurrentFrame("Paused", Paused, 50, "benchy.gcode", 215, 215),
		PrinterStatus.Disconnected => CurrentFrame("Offline", Disconnected, tool: 215, toolTarget: 215),
		_ => throw new ArgumentOutOfRangeException(nameof(status)),
	}).AsTask();

	// OctoPrint's event names are the PrinterEventKind names.
	Task IFakePrinterServer.RaiseAsync(PrinterEventKind kind) =>
		PushAsync(JsonSerializer.Serialize(new { @event = new { type = kind.ToString(), payload = new { name = "benchy.gcode" } } })).AsTask();

	public static string CurrentFrame(
		string text,
		string flags,
		double? completion = null,
		string? file = null,
		double tool = 25,
		double toolTarget = 0) =>
		$$$"""
		{"current": {
		  "state": {"text": "{{{text}}}", "flags": { {{{flags}}} }},
		  "job": {"file": {"name": {{{Json(file)}}}, "display": {{{Json(file)}}}, "path": {{{Json(file)}}}, "origin": "local"}, "estimatedPrintTime": 3600},
		  "progress": {"completion": {{{Json(completion)}}}, "printTime": 120, "printTimeLeft": 3480},
		  "currentZ": 0.2,
		  "temps": [{"time": 1, "tool0": {"actual": {{{Json(tool)}}}, "target": {{{Json(toolTarget)}}}}, "bed": {"actual": 60, "target": 60}}]
		}}
		""";

	public const string Operational = """ "operational": true, "printing": false, "paused": false, "pausing": false, "cancelling": false, "error": false, "closedOrError": false """;
	public const string Printing = """ "operational": true, "printing": true, "paused": false, "pausing": false, "cancelling": false, "error": false, "closedOrError": false """;
	public const string Paused = """ "operational": true, "printing": false, "paused": true, "pausing": false, "cancelling": false, "error": false, "closedOrError": false """;
	public const string Disconnected = """ "operational": false, "printing": false, "paused": false, "pausing": false, "cancelling": false, "error": false, "closedOrError": true """;

	private static string Json(object? value) => JsonSerializer.Serialize(value);

	private void Map(WebApplication app)
	{
		app.UseWebSockets();

		app.MapPost("/api/login", (HttpContext http) => Authorized(http)
			? Results.Json(new { name = User, session = Session })
			: Results.StatusCode(403));

		app.MapGet("/api/settings", (HttpContext http) => Authorized(http) ? Results.Text(SettingsJson, "application/json") : Results.StatusCode(403));
		app.MapGet("/api/files", (HttpContext http) => Authorized(http) ? Results.Text(FilesJson, "application/json") : Results.StatusCode(403));
		app.MapGet("/api/system/commands", (HttpContext http) => Results.Text(
			"""{"core": [{"action": "restart", "name": "Restart OctoPrint", "source": "core", "confirm": "Sure?"}], "custom": []}""",
			"application/json"));

		app.MapGet("/api/plugin/gpiocontrol", (HttpContext http) => !Authorized(http)
			? Results.StatusCode(403)
			: GpioStates is null ? Results.NotFound() : Results.Json(GpioStates));

		app.MapPost("/api/{**path}", async (HttpContext http, string path) =>
		{
			if (!Authorized(http))
			{
				return Results.StatusCode(403);
			}

			JsonElement? body = null;
			if (http.Request.ContentLength is > 0 || http.Request.ContentType is not null)
			{
				using var document = await JsonDocument.ParseAsync(http.Request.Body);
				body = document.RootElement.Clone();
			}

			var command = new RecordedCommand("POST", "/api/" + path, body);
			_commands.Enqueue(command);
			if (path == "plugin/gpiocontrol" && GpioStates is { } states && body is { } gpio)
			{
				states[gpio.GetProperty("id").GetInt32()] = gpio.GetProperty("command").GetString() == "turnGpioOn" ? "on" : "off";
			}

			foreach (var frame in OnCommand?.Invoke(command) ?? [])
			{
				await PushAsync(frame as string ?? JsonSerializer.Serialize(frame));
			}

			return Results.NoContent();
		});

		// Like OctoPrint: the version needs access, and refusing it is a JSON error.
		app.MapGet("/api/version", (HttpContext http) => Authorized(http)
			? Results.Json(new { api = "0.1", server = "1.10.2", text = "OctoPrint 1.10.2" })
			: Results.Json(new { error = "You don't have the permission to access the requested resource." }, statusCode: 403));
		app.MapGet("/plugin/appkeys/probe", () => Results.NoContent());
		app.MapPost("/plugin/appkeys/request", () =>
		{
			Interlocked.Exchange(ref _lastAppKeyPoll, Stopwatch.GetTimestamp());
			return Results.Json(new { app_token = AppToken }, statusCode: 201);
		});
		app.MapGet("/plugin/appkeys/request/{token}", (string token) =>
		{
			var previous = Interlocked.Exchange(ref _lastAppKeyPoll, Stopwatch.GetTimestamp());
			_appKeyExpired |= AppKeyPollTimeout is { } timeout && Stopwatch.GetElapsedTime(previous) > timeout;
			if (token != AppToken || DenyAppKey || _appKeyExpired)
			{
				return Results.NotFound();
			}

			return AppKeyAllowed
				? Results.Json(new { api_key = ApiKey })
				: Results.StatusCode(202);
		});

		app.Map("/sockjs/websocket", async (HttpContext http) =>
		{
			if (!http.WebSockets.IsWebSocketRequest)
			{
				http.Response.StatusCode = 400;
				return;
			}

			using var socket = await http.WebSockets.AcceptWebSocketAsync();
			await SendAsync(socket, """{"connected": {"version": "1.10.0", "apikey": null}}""");

			var buffer = new byte[4096];
			var received = await socket.ReceiveAsync(buffer, http.RequestAborted);
			using (var auth = JsonDocument.Parse(buffer.AsMemory(0, received.Count)))
			{
				_auth.TrySetResult(auth.RootElement.GetProperty("auth").GetString()!);
			}

			await foreach (var frame in _frames.Reader.ReadAllAsync(http.RequestAborted))
			{
				await SendAsync(socket, frame);
			}
		});
	}

	private static bool Authorized(HttpContext http) =>
		http.Request.Headers.TryGetValue("X-Api-Key", out var key) && key == ApiKey;

	private static Task SendAsync(WebSocket socket, string text) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

	public async ValueTask DisposeAsync()
	{
		_frames.Writer.TryComplete();
		await _app.StopAsync();
		await _app.DisposeAsync();
	}
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
	public HttpClient CreateClient(string name) => new(SameHostRedirectHandler.CreatePipeline()) { Timeout = TimeSpan.FromSeconds(5) };
}
