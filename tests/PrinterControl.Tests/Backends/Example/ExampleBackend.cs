using System.Net.Http.Json;
using System.Text.Json;
using MacroDeck.Localization;
using PrinterControl.Backends;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Tests.Backends.Example;

// The smallest complete backend, kept working by the contract. To start one, copy this folder to
// src/PrinterControl/Backends/<Name>/ and swap the made-up "example" protocol for the server's API.
internal sealed class ExampleBackend(IHttpClientFactory httpClients) : IPrinterBackend
{
	public const string BackendId = "example";

	public string Id => BackendId;

	public LocalizedText Name => LocalizedText.FromLiteral("Example printer software");

	public bool RequiresApiKey => false;

	// An endpoint only this software answers, asked without credentials.
	public async Task<bool> DetectAsync(Uri baseUri, CancellationToken cancellationToken)
	{
		using var info = await ExampleHttp.TryGetJsonAsync(Http(), new Uri(baseUri, "example/info"), cancellationToken);
		return info?.RootElement.Str("software") == "example-printer";
	}

	public IPrinterSetup CreateSetup(Uri baseUri, bool editing) => new ExampleSetup(Http(), baseUri);

	public PrinterConnection Connect(PrinterConfig config, HttpClient http, ILogger logger) => new ExampleConnection(config, http, logger);

	private HttpClient Http() => httpClients.CreateClient(PrinterRegistry.HttpClientName);
}

// No login: done right after the address, with the name the server gives the printer.
internal sealed class ExampleSetup(HttpClient http, Uri baseUri) : IPrinterSetup
{
	public async Task<PrinterSetupResult> StartAsync(CancellationToken cancellationToken)
	{
		using var info = await ExampleHttp.TryGetJsonAsync(http, new Uri(baseUri, "example/info"), cancellationToken);
		return PrinterSetupResult.Done(info?.RootElement.Str("name"));
	}

	public Task<PrinterSetupResult> SubmitAsync(string stepId, IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken) =>
		StartAsync(cancellationToken);
}

internal sealed class ExampleConnection(PrinterConfig config, HttpClient http, ILogger logger)
	: PrinterConnection(config, logger), IJobControl, IMotionControl
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

	// Returning or throwing ends the session; the base class reconnects with backoff.
	protected override async Task RunSessionAsync(CancellationToken cancellationToken)
	{
		var connected = false;
		while (true)
		{
			using var status = await ExampleHttp.GetJsonAsync(http, new Uri(Config.BaseUri, "example/status"), cancellationToken);
			if (!connected)
			{
				ReportConnected();
				connected = true;
			}

			PublishSnapshot(ReadSnapshot(status.RootElement));
			foreach (var received in ReadEvents(status.RootElement))
			{
				PublishEvent(received);
			}

			await Task.Delay(PollInterval, cancellationToken);
		}
	}

	// The server's states and heater names mapped onto the core ones.
	private PrinterSnapshot ReadSnapshot(JsonElement status) => Snapshot with
	{
		Online = true,
		Status = status.Str("state") switch
		{
			"ready" => PrinterStatus.Operational,
			"printing" => PrinterStatus.Printing,
			"paused" => PrinterStatus.Paused,
			"error" => PrinterStatus.Error,
			_ => PrinterStatus.Disconnected,
		},
		StateText = status.Str("state") ?? string.Empty,
		Temperatures = new Dictionary<string, Temperature>
		{
			[HeaterIds.Extruder] = new(status.Obj("hotend")?.Num("actual"), status.Obj("hotend")?.Num("target")),
			[HeaterIds.Bed] = new(status.Obj("bed")?.Num("actual"), status.Obj("bed")?.Num("target")),
		},
	};

	private static IEnumerable<PrinterEvent> ReadEvents(JsonElement status)
	{
		foreach (var name in status.Arr("events")?.EnumerateArray() ?? default)
		{
			var type = name.GetString() ?? string.Empty;
			yield return new PrinterEvent(type, default)
			{
				Kind = type switch
				{
					"print-started" => PrinterEventKind.PrintStarted,
					"print-done" => PrinterEventKind.PrintDone,
					"print-paused" => PrinterEventKind.PrintPaused,
					_ => null,
				},
			};
		}
	}

	public Task RunJobCommandAsync(JobCommand command, CancellationToken cancellationToken) => command switch
	{
		JobCommand.Pause => SendAsync(new { command = "pause" }, cancellationToken),
		JobCommand.Resume => SendAsync(new { command = "resume" }, cancellationToken),
		JobCommand.Cancel => SendAsync(new { command = "cancel" }, cancellationToken),
		_ => throw NotSupported($"the job command {command}"),
	};

	// IMotionControl's home, jog, extrude and rates come from its G-code defaults on top of this.
	public Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken) =>
		SendAsync(new { gcode = commands }, cancellationToken);

	private async Task SendAsync(object body, CancellationToken cancellationToken)
	{
		using var response = await ExampleHttp.SendAsync(http, new Uri(Config.BaseUri, "example/command"), body, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new PrinterException(PrinterFailure.BadRequest, $"The example server refused the command ({(int)response.StatusCode}).");
		}
	}
}

// HTTP failures become PrinterException(Unreachable), which setup and actions know how to report.
internal static class ExampleHttp
{
	public static async Task<JsonDocument> GetJsonAsync(HttpClient http, Uri uri, CancellationToken cancellationToken) =>
		await TryGetJsonAsync(http, uri, cancellationToken)
		?? throw new PrinterException(PrinterFailure.InvalidResponse, $"{uri} did not answer with JSON.");

	public static async Task<JsonDocument?> TryGetJsonAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
	{
		using var response = await Guard(uri, () => http.GetAsync(uri, cancellationToken));
		if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
		{
			return null;
		}

		try
		{
			return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, object body, CancellationToken cancellationToken) =>
		Guard(uri, () => http.PostAsJsonAsync(uri, body, cancellationToken));

	private static async Task<HttpResponseMessage> Guard(Uri uri, Func<Task<HttpResponseMessage>> send)
	{
		try
		{
			return await send();
		}
		catch (HttpRequestException exception)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"Could not reach {uri}: {exception.Message}", exception);
		}
	}
}
