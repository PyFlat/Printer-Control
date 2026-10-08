using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PrinterControl.Core;

namespace PrinterControl.Tests.Support;

// Moonraker's HTTP info endpoints and JSON-RPC websocket, plus /api/version the way its
// octoprint_compat imitates OctoPrint.
internal sealed class FakeMoonraker : IFakePrinterServer
{
	public const string Key = "moonraker-api-key";

	private readonly WebApplication _app;
	private readonly ConcurrentQueue<string> _calls = new();
	private readonly ConcurrentDictionary<int, Channel<string>> _sockets = new();
	private readonly Lock _gate = new();
	private int _nextSocket;
	private string _klippy = "ready";
	private string _printState = "standby";

	private FakeMoonraker(WebApplication app, Uri baseUri)
	{
		_app = app;
		BaseUri = baseUri;
	}

	public Uri BaseUri { get; }

	// Null: this computer is a trusted client and needs no key.
	public string? ApiKey { get; set; } = Key;

	public int CommandsReceived => Calls.Count(c => !c.StartsWith("server.", StringComparison.Ordinal)
		&& !c.StartsWith("printer.objects.", StringComparison.Ordinal)
		&& c is not ("printer.info" or "machine.device_power.devices"));

	// Every JSON-RPC method called, with G-code scripts as "printer.gcode.script <script>".
	public IReadOnlyList<string> Calls => [.. _calls];

	public Dictionary<string, string> PowerDevices { get; } = new() { ["Chamber Light"] = "off" };

	public int WebcamRotation { get; set; } = 90;

	public static async Task<FakeMoonraker> StartAsync()
	{
		FakeMoonraker? fake = null;
		var (app, baseUri) = await FakeServerHost.StartAsync(app => Map(app, () => fake!), webSockets: true);
		fake = new FakeMoonraker(app, baseUri);
		return fake;
	}

	private static void Map(WebApplication app, Func<FakeMoonraker> fake)
	{
		app.MapGet("/server/info", (HttpContext http) => fake().Authorized(http)
			? Results.Json(new { result = fake().ServerInfo() })
			: Unauthorized());
		app.MapGet("/printer/info", (HttpContext http) => fake().Authorized(http)
			? Results.Json(new { result = new { state = "ready", state_message = "Printer is ready", hostname = "voron" } })
			: Unauthorized());
		app.MapGet("/api/version", () => Results.Json(new { server = "1.5.0", api = "0.1", text = "OctoPrint (Moonraker v0.9.3)" }));
		app.Map("/websocket", (HttpContext http) => fake().ServeSocketAsync(http));
	}

	private static IResult Unauthorized() =>
		Results.Json(new { error = new { code = 401, message = "Unauthorized", traceback = "" } }, statusCode: 401);

	private bool Authorized(HttpContext http) =>
		ApiKey is null || (http.Request.Headers.TryGetValue("X-Api-Key", out var key) && key == ApiKey);

	private object ServerInfo()
	{
		lock (_gate)
		{
			return new { klippy_connected = _klippy != "disconnected", klippy_state = _klippy, moonraker_version = "v0.9.3", api_version_string = "1.5.0" };
		}
	}

	public async Task ReportAsync(PrinterStatus status)
	{
		if (status == PrinterStatus.Disconnected)
		{
			lock (_gate)
			{
				_klippy = "disconnected";
			}

			await NotifyAsync(new { jsonrpc = "2.0", method = "notify_klippy_disconnected" });
			return;
		}

		var wasReady = SetKlippy("ready");
		await SetPrintStateAsync(status switch
		{
			PrinterStatus.Operational => "standby",
			PrinterStatus.Printing => "printing",
			PrinterStatus.Paused => "paused",
			_ => throw new ArgumentOutOfRangeException(nameof(status)),
		});
		if (!wasReady)
		{
			await NotifyAsync(new { jsonrpc = "2.0", method = "notify_klippy_ready" });
		}
	}

	public Task ChangeWebcamsAsync() => NotifyAsync(new { jsonrpc = "2.0", method = "notify_webcams_changed", @params = Array.Empty<object>() });

	// Klipper has no job events; the backend derives them from print_stats.state changing.
	public Task RaiseAsync(PrinterEventKind kind) => SetPrintStateAsync(kind switch
	{
		PrinterEventKind.PrintStarted => "printing",
		PrinterEventKind.PrintDone => "complete",
		PrinterEventKind.PrintPaused => "paused",
		PrinterEventKind.PrintCancelled => "cancelled",
		PrinterEventKind.PrintFailed => "error",
		_ => throw new ArgumentOutOfRangeException(nameof(kind)),
	});

	private bool SetKlippy(string state)
	{
		lock (_gate)
		{
			var was = _klippy == state;
			_klippy = state;
			return was;
		}
	}

	private Task SetPrintStateAsync(string state)
	{
		lock (_gate)
		{
			_printState = state;
		}

		return NotifyAsync(new
		{
			jsonrpc = "2.0",
			method = "notify_status_update",
			@params = new object[] { new { print_stats = new { state, filename = "benchy.gcode" } }, 1234.5 },
		});
	}

	private async Task NotifyAsync(object notification)
	{
		var text = JsonSerializer.Serialize(notification);
		foreach (var socket in _sockets.Values)
		{
			await socket.Writer.WriteAsync(text);
		}
	}

	private async Task ServeSocketAsync(HttpContext http)
	{
		if (!http.WebSockets.IsWebSocketRequest)
		{
			http.Response.StatusCode = 400;
			return;
		}

		if (!Authorized(http))
		{
			http.Response.StatusCode = 401;
			return;
		}

		using var socket = await http.WebSockets.AcceptWebSocketAsync();
		var id = Interlocked.Increment(ref _nextSocket);
		var outgoing = Channel.CreateUnbounded<string>();
		_sockets[id] = outgoing;
		try
		{
			var sending = Task.Run(async () =>
			{
				await foreach (var text in outgoing.Reader.ReadAllAsync())
				{
					await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
				}
			});

			var buffer = new byte[16 * 1024];
			while (socket.State == WebSocketState.Open)
			{
				WebSocketReceiveResult received;
				try
				{
					received = await socket.ReceiveAsync(buffer, http.RequestAborted);
				}
				catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
				{
					break;
				}

				if (received.MessageType == WebSocketMessageType.Close)
				{
					break;
				}

				using var request = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
				await outgoing.Writer.WriteAsync(Answer(request.RootElement));
			}

			outgoing.Writer.TryComplete();
			await sending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		}
		finally
		{
			_sockets.TryRemove(id, out _);
		}
	}

	private string Answer(JsonElement request)
	{
		var method = request.GetProperty("method").GetString()!;
		var parameters = request.TryGetProperty("params", out var p) ? p : default;
		_calls.Enqueue(method == "printer.gcode.script" ? $"{method} {parameters.GetProperty("script").GetString()}" : method);

		object? result = method switch
		{
			"server.connection.identify" => new { connection_id = 1 },
			"server.info" => ServerInfo(),
			"printer.info" => new { state = "ready", state_message = "Printer is ready", hostname = "voron" },
			"printer.objects.list" => new { objects = new[] { "webhooks", "print_stats", "virtual_sdcard", "gcode_move", "extruder", "heater_bed", "fan" } },
			"printer.objects.subscribe" => Subscription(),
			"server.webcams.list" => new
			{
				webcams = new[]
				{
					new { name = "cam", enabled = true, service = "mjpegstreamer-adaptive", stream_url = "/webcam/?action=stream", snapshot_url = "/webcam/?action=snapshot", flip_horizontal = false, flip_vertical = false, rotation = WebcamRotation, aspect_ratio = "4:3" },
				},
			},
			"server.files.list" => new[] { new { path = "benchy.gcode", modified = 1700000000.5, size = 1024, permissions = "rw" } },
			"machine.device_power.devices" => new { devices = PowerDevices.Select(d => new { device = d.Key, status = d.Value, locked_while_printing = false, type = "gpio" }).ToArray() },
			"machine.device_power.post_device" => SetPower(parameters),
			"printer.print.start" or "printer.print.pause" or "printer.print.resume" or "printer.print.cancel"
				or "printer.gcode.script" or "printer.firmware_restart" or "printer.restart"
				or "machine.reboot" or "machine.shutdown" or "machine.services.restart" => "ok",
			_ => null,
		};

		var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.GetProperty("id").GetInt32() };
		if (result is null)
		{
			answer["error"] = new JsonObject { ["code"] = 404, ["message"] = $"Method not found: {method}" };
		}
		else
		{
			answer["result"] = JsonSerializer.SerializeToNode(result);
		}

		return answer.ToJsonString();
	}

	private object Subscription()
	{
		lock (_gate)
		{
			return new
			{
				eventtime = 1234.5,
				status = new
				{
					print_stats = new { state = _printState, filename = _printState == "standby" ? "" : "benchy.gcode", print_duration = 120.0, message = "" },
					virtual_sdcard = new { progress = 0.5 },
					gcode_move = new { gcode_position = new[] { 10.0, 20.0, 0.2, 0.0 } },
					extruder = new { temperature = 215.0, target = 215.0 },
					heater_bed = new { temperature = 60.0, target = 60.0 },
				},
			};
		}
	}

	private Dictionary<string, string> SetPower(JsonElement parameters)
	{
		var device = parameters.GetProperty("device").GetString()!;
		PowerDevices[device] = parameters.GetProperty("action").GetString()!;
		return new Dictionary<string, string> { [device] = PowerDevices[device] };
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var socket in _sockets.Values)
		{
			socket.Writer.TryComplete();
		}

		await _app.StopAsync();
		await _app.DisposeAsync();
	}
}
