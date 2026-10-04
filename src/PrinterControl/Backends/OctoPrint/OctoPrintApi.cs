using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrinterControl.Core;

namespace PrinterControl.Backends.OctoPrint;

internal sealed class OctoPrintApi(HttpClient http, Uri baseUri, string? apiKey)
{
	public const string AppName = "Macro Deck";

	private readonly Uri _baseUri = PrinterUrls.Normalize(baseUri);

	public Uri BaseUri => _baseUri;

	public async Task<OctoPrintVersion> GetVersionAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/version", cancellationToken);
		var root = json.RootElement;
		return new OctoPrintVersion(root.Str("api") ?? string.Empty, root.Str("server") ?? string.Empty);
	}

	public async Task<string?> GetCurrentUserAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/currentuser", cancellationToken);
		return json.RootElement.Str("name");
	}

	// A passive login with the API key yields the session the push socket authenticates with.
	public async Task<OctoPrintLogin> LoginPassiveAsync(CancellationToken cancellationToken)
	{
		using var json = await SendJsonAsync(HttpMethod.Post, "api/login", new JsonObject { ["passive"] = true }, cancellationToken);
		var root = json!.RootElement;
		var name = root.Str("name");
		var session = root.Str("session");
		if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(session))
		{
			throw new PrinterException(PrinterFailure.Unauthorized, "OctoPrint accepted the login but returned no session; the API key has no user.");
		}

		return new OctoPrintLogin(name, session);
	}

	public async Task<OctoPrintSettings> GetSettingsAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/settings", cancellationToken);
		return ParseSettings(json.RootElement);
	}

	internal static OctoPrintSettings ParseSettings(JsonElement root)
	{
		var profiles = new List<TemperatureProfile>();
		if (root.Obj("temperature")?.Arr("profiles") is { } list)
		{
			foreach (var profile in list.EnumerateArray())
			{
				if (profile.Str("name") is { Length: > 0 } profileName)
				{
					profiles.Add(new TemperatureProfile(profileName, profile.Num("extruder"), profile.Num("bed"), profile.Num("chamber")));
				}
			}
		}

		var name = root.Obj("appearance")?.Str("name");
		var gpio = ParseGpioOutputs(root);
		return new OctoPrintSettings(
			new PrinterSettings(
				string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
				ParseWebcam(root),
				profiles,
				[.. gpio.Select(o => new PrinterOutput(o.OutputId, o.Name))]),
			gpio);
	}

	// OctoPrint 1.9 moved the stream settings into the bundled classic webcam plugin; older servers keep
	// them under "webcam".
	private static WebcamSettings ParseWebcam(JsonElement root)
	{
		var legacy = root.Obj("webcam");
		var classic = root.Obj("plugins")?.Obj("classicwebcam");
		var enabled = legacy?.Bool("webcamEnabled") ?? true;

		if (classic is { } plugin && plugin.Str("stream") is { Length: > 0 } stream)
		{
			return new WebcamSettings(
				enabled,
				stream,
				plugin.Str("snapshot"),
				plugin.Bool("flipH") ?? false,
				plugin.Bool("flipV") ?? false,
				plugin.Bool("rotate90") == true ? 270 : 0,
				plugin.Str("streamRatio"));
		}

		if (legacy is { } webcam)
		{
			return new WebcamSettings(
				enabled && !string.IsNullOrEmpty(webcam.Str("streamUrl")),
				webcam.Str("streamUrl"),
				webcam.Str("snapshotUrl"),
				webcam.Bool("flipH") ?? false,
				webcam.Bool("flipV") ?? false,
				webcam.Bool("rotate90") == true ? 270 : 0,
				webcam.Str("streamRatio"));
		}

		return WebcamSettings.None;
	}

	public async Task<IReadOnlyList<PrintFile>> GetFilesAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/files?recursive=true", cancellationToken);
		var files = new List<PrintFile>();
		if (json.RootElement.Arr("files") is { } entries)
		{
			CollectFiles(entries, files);
		}

		return files;
	}

	internal static void CollectFiles(JsonElement entries, List<PrintFile> into)
	{
		foreach (var entry in entries.EnumerateArray())
		{
			if (entry.Str("type") == "folder")
			{
				if (entry.Arr("children") is { } children)
				{
					CollectFiles(children, into);
				}

				continue;
			}

			if (entry.Str("type") != "machinecode" || entry.Str("path") is not { Length: > 0 } path)
			{
				continue;
			}

			// Stored by the print file action as "<origin>:<path>", "local:parts/benchy.gcode".
			var origin = entry.Str("origin") ?? "local";
			var date = entry.Num("date") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : (DateTimeOffset?)null;
			into.Add(new PrintFile(
				$"{origin}:{path}",
				origin == "sdcard" ? $"{path} (SD)" : path,
				entry.Num("size") is { } size ? (long)size : null,
				date));
		}
	}

	public Task SelectFileAsync(string origin, string path, bool print, CancellationToken cancellationToken) =>
		CommandAsync(
			$"api/files/{Uri.EscapeDataString(origin)}/{EscapePath(path)}",
			new JsonObject { ["command"] = "select", ["print"] = print },
			cancellationToken);

	public Task JobCommandAsync(string command, string? action, CancellationToken cancellationToken)
	{
		var body = new JsonObject { ["command"] = command };
		if (action is not null)
		{
			body["action"] = action;
		}

		return CommandAsync("api/job", body, cancellationToken);
	}

	public Task ConnectAsync(CancellationToken cancellationToken) =>
		CommandAsync("api/connection", new JsonObject { ["command"] = "connect" }, cancellationToken);

	public Task DisconnectAsync(CancellationToken cancellationToken) =>
		CommandAsync("api/connection", new JsonObject { ["command"] = "disconnect" }, cancellationToken);

	public Task HomeAsync(IReadOnlyList<string> axes, CancellationToken cancellationToken) =>
		CommandAsync(
			"api/printer/printhead",
			new JsonObject { ["command"] = "home", ["axes"] = new JsonArray([.. axes.Select(a => JsonValue.Create(a))]) },
			cancellationToken);

	public Task JogAsync(double x, double y, double z, double? speed, CancellationToken cancellationToken)
	{
		var body = new JsonObject { ["command"] = "jog", ["x"] = x, ["y"] = y, ["z"] = z };
		if (speed is { } feed)
		{
			body["speed"] = feed;
		}

		return CommandAsync("api/printer/printhead", body, cancellationToken);
	}

	public Task FeedrateAsync(int percent, CancellationToken cancellationToken) =>
		CommandAsync("api/printer/printhead", new JsonObject { ["command"] = "feedrate", ["factor"] = percent }, cancellationToken);

	public Task FlowrateAsync(int percent, CancellationToken cancellationToken) =>
		CommandAsync("api/printer/tool", new JsonObject { ["command"] = "flowrate", ["factor"] = percent }, cancellationToken);

	public Task ExtrudeAsync(double amount, CancellationToken cancellationToken) =>
		CommandAsync("api/printer/tool", new JsonObject { ["command"] = "extrude", ["amount"] = amount }, cancellationToken);

	public Task SetTemperatureAsync(string heater, double target, CancellationToken cancellationToken) => heater switch
	{
		HeaterIds.Bed => CommandAsync("api/printer/bed", new JsonObject { ["command"] = "target", ["target"] = target }, cancellationToken),
		HeaterIds.Chamber => CommandAsync("api/printer/chamber", new JsonObject { ["command"] = "target", ["target"] = target }, cancellationToken),
		_ => CommandAsync(
			"api/printer/tool",
			new JsonObject { ["command"] = "target", ["targets"] = new JsonObject { [OctoPrintHeaters.FromHeaterId(heater)] = target } },
			cancellationToken),
	};

	public Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken) =>
		CommandAsync(
			"api/printer/command",
			new JsonObject { ["commands"] = new JsonArray([.. commands.Select(c => JsonValue.Create(c))]) },
			cancellationToken);

	public async Task<IReadOnlyList<SystemCommand>> GetSystemCommandsAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/system/commands", cancellationToken);
		var commands = new List<SystemCommand>();
		foreach (var source in new[] { "core", "custom" })
		{
			if (json.RootElement.Arr(source) is not { } list)
			{
				continue;
			}

			foreach (var command in list.EnumerateArray())
			{
				// Stored by the system command action as "<source>/<action>", "core/restart".
				if (command.Str("action") is { Length: > 0 } action)
				{
					commands.Add(new SystemCommand(
						$"{command.Str("source") ?? source}/{action}",
						command.Str("name") ?? action,
						!string.IsNullOrEmpty(command.Str("confirm"))));
				}
			}
		}

		return commands;
	}

	public Task RunSystemCommandAsync(string source, string action, CancellationToken cancellationToken) =>
		CommandAsync($"api/system/commands/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(action)}", null, cancellationToken);

	// GPIO Control lists its outputs only in the settings.
	// https://github.com/catgiggle/OctoPrint-GpioControl
	public async Task<IReadOnlyList<GpioOutput>> GetGpioOutputsAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/settings", cancellationToken);
		return ParseGpioOutputs(json.RootElement);
	}

	internal static IReadOnlyList<GpioOutput> ParseGpioOutputs(JsonElement settings)
	{
		var outputs = new List<GpioOutput>();
		if (settings.Obj("plugins")?.Obj("gpiocontrol")?.Arr("gpio_configurations") is not { } list)
		{
			return outputs;
		}

		var index = 0;
		foreach (var entry in list.EnumerateArray())
		{
			if (entry.Num("pin") is { } pin and > 0)
			{
				var name = entry.Str("name");
				outputs.Add(new GpioOutput(
					index,
					(int)pin,
					string.IsNullOrWhiteSpace(name) ? string.Create(CultureInfo.InvariantCulture, $"GPIO{(int)pin}") : name.Trim()));
			}

			index++;
		}

		return outputs;
	}

	// "on", "off", or empty for an output with an invalid pin, one per configured output.
	public async Task<IReadOnlyList<string>> GetGpioStatesAsync(CancellationToken cancellationToken)
	{
		using var json = await GetJsonAsync("api/plugin/gpiocontrol", cancellationToken);
		return json.RootElement.ValueKind == JsonValueKind.Array
			? [.. json.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty)]
			: [];
	}

	public Task SetGpioAsync(int index, bool on, CancellationToken cancellationToken) =>
		CommandAsync(
			"api/plugin/gpiocontrol",
			new JsonObject { ["command"] = on ? "turnGpioOn" : "turnGpioOff", ["id"] = index },
			cancellationToken);

	// Without a key OctoPrint answers /api/version with 403 and {"error": "<text>"}; with access, its version.
	// Moonraker imitates this endpoint, so its own name in the text, or an error object, rules it out.
	public async Task<bool> IsOctoPrintAsync(CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Get, "api/version", null, authenticate: false, cancellationToken);
		if (response.Content.Headers.ContentType?.MediaType != "application/json")
		{
			return false;
		}

		try
		{
			using var json = await ReadJsonAsync(response, cancellationToken);
			var root = json.RootElement;
			return response.StatusCode switch
			{
				HttpStatusCode.OK => root.Str("text") is { } text
					&& text.StartsWith("OctoPrint", StringComparison.Ordinal)
					&& !text.Contains("Moonraker", StringComparison.OrdinalIgnoreCase),
				HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => root.Str("error") is not null,
				_ => false,
			};
		}
		catch (PrinterException exception) when (exception.Failure == PrinterFailure.InvalidResponse)
		{
			return false;
		}
	}

	// Supported since OctoPrint 1.5 as a bundled plugin; a 404 means the user disabled it.
	public async Task<bool> SupportsAppKeysAsync(CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Get, "plugin/appkeys/probe", null, authenticate: false, cancellationToken);
		return response.StatusCode == HttpStatusCode.NoContent;
	}

	public async Task<AppKeyRequest> RequestAppKeyAsync(string? user, CancellationToken cancellationToken)
	{
		var body = new JsonObject { ["app"] = AppName };
		if (!string.IsNullOrWhiteSpace(user))
		{
			body["user"] = user.Trim();
		}

		using var response = await SendAsync(HttpMethod.Post, "plugin/appkeys/request", body, authenticate: false, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
		using var json = await ReadJsonAsync(response, cancellationToken);
		return json.RootElement.Str("app_token") is { Length: > 0 } token
			? new AppKeyRequest(token)
			: throw new PrinterException(PrinterFailure.InvalidResponse, "The application key request returned no app_token.");
	}

	public async Task<AppKeyPoll> PollAppKeyAsync(AppKeyRequest request, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(
			HttpMethod.Get,
			$"plugin/appkeys/request/{Uri.EscapeDataString(request.Token)}",
			null,
			authenticate: false,
			cancellationToken);

		switch (response.StatusCode)
		{
			case HttpStatusCode.Accepted:
				return new AppKeyPoll(AppKeyDecision.Pending, null);
			case HttpStatusCode.NotFound:
				return new AppKeyPoll(AppKeyDecision.Denied, null);
		}

		await EnsureSuccessAsync(response, cancellationToken);
		using var json = await ReadJsonAsync(response, cancellationToken);
		return json.RootElement.Str("api_key") is { Length: > 0 } key
			? new AppKeyPoll(AppKeyDecision.Granted, key)
			: throw new PrinterException(PrinterFailure.InvalidResponse, "The application key response carried no api_key.");
	}

	private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Get, path, null, authenticate: true, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
		return await ReadJsonAsync(response, cancellationToken);
	}

	private async Task<JsonDocument?> SendJsonAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(method, path, body, authenticate: true, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
		return response.StatusCode == HttpStatusCode.NoContent ? null : await ReadJsonAsync(response, cancellationToken);
	}

	private async Task CommandAsync(string path, JsonObject? body, CancellationToken cancellationToken)
	{
		using var response = await SendAsync(HttpMethod.Post, path, body, authenticate: true, cancellationToken);
		await EnsureSuccessAsync(response, cancellationToken);
	}

	private async Task<HttpResponseMessage> SendAsync(
		HttpMethod method,
		string path,
		JsonObject? body,
		bool authenticate,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
		if (authenticate && !string.IsNullOrEmpty(apiKey))
		{
			request.Headers.Add("X-Api-Key", apiKey);
		}

		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		try
		{
			return await http.SendAsync(request, cancellationToken);
		}
		catch (HttpRequestException exception)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"Could not reach OctoPrint at {_baseUri}: {exception.Message}", exception);
		}
		catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"OctoPrint at {_baseUri} did not answer in time.", exception);
		}
	}

	private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode)
		{
			return;
		}

		var detail = await ErrorDetailAsync(response, cancellationToken);
		throw new PrinterException(
			PrinterException.FromStatus(response.StatusCode),
			string.Create(CultureInfo.InvariantCulture, $"OctoPrint answered {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {detail}"));
	}

	private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		var text = await response.Content.ReadAsStringAsync(cancellationToken);
		try
		{
			using var json = JsonDocument.Parse(text);
			return json.RootElement.Str("error") ?? text;
		}
		catch (JsonException)
		{
			return text.Length > 200 ? text[..200] : text;
		}
	}

	private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		}
		catch (JsonException exception)
		{
			throw new PrinterException(PrinterFailure.InvalidResponse, "OctoPrint returned something that is not JSON; is the URL an OctoPrint server?", exception);
		}
	}

	// Uri resolves "." and ".." segments, which would turn a file path into any other endpoint
	// ("../../system/commands/core/shutdown").
	private static string EscapePath(string path)
	{
		var segments = path.Split('/');
		return segments.Any(s => s is "." or "..")
			? throw new PrinterException(PrinterFailure.BadRequest, "A file path may not contain . or .. segments.")
			: string.Join('/', segments.Select(Uri.EscapeDataString));
	}
}
