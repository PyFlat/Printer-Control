using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends.OctoPrint;

// Logs in passively with the API key, reads /api/settings, then authenticates the push socket with
// user:session. State comes from push frames, except GPIO Control's, which is read while the socket is up.
internal sealed class OctoPrintConnection
	: PrinterConnection, IJobControl, IFileControl, ITemperatureControl, IMotionControl, IPrinterLink, ISystemCommands, IOutputControl
{
	// Frames arrive every 500 ms times this factor.
	private const int PushThrottle = 2;

	// Bounds what a misbehaving server can make the plugin buffer.
	private const int MaxFrameBytes = 16 * 1024 * 1024;
	private static readonly TimeSpan GpioPollInterval = TimeSpan.FromSeconds(2);

	private IReadOnlyList<GpioOutput> _gpio = [];

	public OctoPrintConnection(PrinterConfig config, HttpClient http, ILogger logger)
		: base(config, logger) => Api = new OctoPrintApi(http, config.BaseUri, config.ApiKey);

	public OctoPrintApi Api { get; }

	protected override async Task RunSessionAsync(CancellationToken cancellationToken)
	{
		var login = await Api.LoginPassiveAsync(cancellationToken);
		await RefreshSettingsAsync(cancellationToken);

		using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var gpio = PollGpioAsync(listening.Token);
		try
		{
			await ListenAsync(login, cancellationToken);
		}
		finally
		{
			await listening.CancelAsync();
			await gpio.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		}
	}

	public async Task RefreshSettingsAsync(CancellationToken cancellationToken)
	{
		var settings = await Api.GetSettingsAsync(cancellationToken);
		Volatile.Write(ref _gpio, settings.Gpio);
		PublishSettings(settings.Common);
	}

	internal void ApplyFrame(JsonElement frame)
	{
		foreach (var property in frame.EnumerateObject())
		{
			switch (property.Name)
			{
				case "current":
				case "history":
					if (property.Value.ValueKind == JsonValueKind.Object)
					{
						PublishSnapshot(PushMessages.ApplyCurrent(Snapshot, property.Value));
					}

					break;
				case "event":
					if (PushMessages.ReadEvent(property.Value) is { } received)
					{
						PublishEvent(received);
					}

					break;
			}
		}
	}

	public Task RunJobCommandAsync(JobCommand command, CancellationToken cancellationToken) => command switch
	{
		JobCommand.Pause => Api.JobCommandAsync("pause", "pause", cancellationToken),
		JobCommand.Resume => Api.JobCommandAsync("pause", "resume", cancellationToken),
		JobCommand.Cancel => Api.JobCommandAsync("cancel", null, cancellationToken),
		JobCommand.Start => Api.JobCommandAsync("start", null, cancellationToken),
		JobCommand.Restart => Api.JobCommandAsync("restart", null, cancellationToken),
		_ => throw new ArgumentOutOfRangeException(nameof(command)),
	};

	public Task<IReadOnlyList<PrintFile>> GetFilesAsync(CancellationToken cancellationToken) =>
		Api.GetFilesAsync(cancellationToken);

	// A bare path, as a user types it, is a local file.
	public Task SelectFileAsync(string fileId, bool print, CancellationToken cancellationToken)
	{
		var separator = fileId.IndexOf(':', StringComparison.Ordinal);
		var (origin, path) = separator > 0 && fileId[..separator] is "local" or "sdcard"
			? (fileId[..separator], fileId[(separator + 1)..])
			: ("local", fileId);
		return Api.SelectFileAsync(origin, path, print, cancellationToken);
	}

	public Task SetTemperatureAsync(string heater, double target, CancellationToken cancellationToken) =>
		Api.SetTemperatureAsync(heater, target, cancellationToken);

	public Task HomeAsync(IReadOnlyList<string> axes, CancellationToken cancellationToken) =>
		Api.HomeAsync(axes, cancellationToken);

	public Task JogAsync(double x, double y, double z, double? speed, CancellationToken cancellationToken) =>
		Api.JogAsync(x, y, z, speed, cancellationToken);

	public Task ExtrudeAsync(double amount, CancellationToken cancellationToken) =>
		Api.ExtrudeAsync(amount, cancellationToken);

	public Task SetFeedrateAsync(int percent, CancellationToken cancellationToken) =>
		Api.FeedrateAsync(percent, cancellationToken);

	public Task SetFlowrateAsync(int percent, CancellationToken cancellationToken) =>
		Api.FlowrateAsync(percent, cancellationToken);

	public Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken) =>
		Api.SendGcodeAsync(commands, cancellationToken);

	public Task ConnectPrinterAsync(CancellationToken cancellationToken) => Api.ConnectAsync(cancellationToken);

	public Task DisconnectPrinterAsync(CancellationToken cancellationToken) => Api.DisconnectAsync(cancellationToken);

	public Task<IReadOnlyList<SystemCommand>> GetSystemCommandsAsync(CancellationToken cancellationToken) =>
		Api.GetSystemCommandsAsync(cancellationToken);

	public Task RunSystemCommandAsync(string commandId, CancellationToken cancellationToken)
	{
		var separator = commandId.IndexOf('/', StringComparison.Ordinal);
		if (separator <= 0 || separator == commandId.Length - 1)
		{
			throw new PrinterException(PrinterFailure.BadRequest, $"{commandId} is not a system command.");
		}

		return Api.RunSystemCommandAsync(commandId[..separator], commandId[(separator + 1)..], cancellationToken);
	}

	public async Task<IReadOnlyList<PrinterOutput>> GetOutputsAsync(CancellationToken cancellationToken) =>
		[.. (await Api.GetGpioOutputsAsync(cancellationToken)).Select(o => new PrinterOutput(o.OutputId, o.Name))];

	public async Task<IReadOnlyDictionary<string, bool>> ReadOutputsAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<string> states;
		try
		{
			states = await Api.GetGpioStatesAsync(cancellationToken);
		}
		catch (PrinterException exception) when (exception.Failure == PrinterFailure.NotFound)
		{
			throw new PrinterException(PrinterFailure.NotSupported, "The GPIO Control plugin is not installed in OctoPrint.", exception);
		}

		var read = new Dictionary<string, bool>(StringComparer.Ordinal);
		foreach (var output in Volatile.Read(ref _gpio))
		{
			if (output.Index < states.Count && states[output.Index] is "on" or "off")
			{
				read[output.OutputId] = states[output.Index] == "on";
			}
		}

		PublishOutputStates(read);
		return read;
	}

	// Resolved against a fresh list: GPIO Control addresses outputs by position.
	public async Task SetOutputAsync(string outputId, bool on, CancellationToken cancellationToken)
	{
		var outputs = await Api.GetGpioOutputsAsync(cancellationToken);
		var output = outputs.FirstOrDefault(o => o.OutputId == outputId)
			?? throw new PrinterException(PrinterFailure.NotFound, $"No GPIO output {outputId}.");
		Volatile.Write(ref _gpio, outputs);
		await Api.SetGpioAsync(output.Index, on, cancellationToken);
	}

	private async Task ListenAsync(OctoPrintLogin login, CancellationToken cancellationToken)
	{
		using var socket = new ClientWebSocket();
		socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
		socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
		await socket.ConnectAsync(OctoPrintUrls.PushSocket(Config.BaseUri), cancellationToken);

		var buffer = new byte[16 * 1024];
		using var message = new MemoryStream();
		while (socket.State == WebSocketState.Open)
		{
			var result = await socket.ReceiveAsync(buffer, cancellationToken);
			if (result.MessageType == WebSocketMessageType.Close)
			{
				Logger.Information("OctoPrint {Printer} closed the push socket.", Config.DisplayName);
				return;
			}

			message.Write(buffer, 0, result.Count);
			if (message.Length > MaxFrameBytes)
			{
				throw new PrinterException(PrinterFailure.InvalidResponse, "OctoPrint sent a push frame over 16 MB.");
			}

			if (!result.EndOfMessage)
			{
				continue;
			}

			var frame = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
			message.SetLength(0);
			using (frame)
			{
				var root = frame.RootElement;
				if (root.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				if (root.TryGetProperty("connected", out _))
				{
					await SendAsync(socket, new JsonObject { ["auth"] = $"{login.Name}:{login.Session}" }, cancellationToken);
					await SendAsync(socket, new JsonObject { ["throttle"] = PushThrottle }, cancellationToken);
					ReportConnected();
					continue;
				}

				// The session expired: ending it makes the base class reconnect, which logs in again.
				if (root.TryGetProperty("reauthRequired", out _))
				{
					Logger.Information("OctoPrint {Printer} asked for re-authentication.", Config.DisplayName);
					return;
				}

				ApplyFrame(root);
				if (root.Obj("event")?.Str("type") == "SettingsUpdated")
				{
					_ = RefreshSettingsQuietlyAsync(cancellationToken);
				}
			}
		}
	}

	// GPIO Control pushes nothing, so its outputs are read while the push socket is up. A server without
	// the plugin lists no outputs and is never asked.
	private async Task PollGpioAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			if (Volatile.Read(ref _gpio).Count > 0)
			{
				try
				{
					await ReadOutputsAsync(cancellationToken);
				}
				catch (PrinterException exception)
				{
					Logger.Debug("Reading the GPIO outputs of OctoPrint {Printer} failed: {Message}", Config.DisplayName, exception.Message);
					PublishOutputStates(new Dictionary<string, bool>());
				}
			}

			await Task.Delay(GpioPollInterval, cancellationToken);
		}
	}

	private async Task RefreshSettingsQuietlyAsync(CancellationToken cancellationToken)
	{
		try
		{
			await RefreshSettingsAsync(cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			Logger.Debug(exception, "Re-reading the settings of OctoPrint {Printer} failed.", Config.DisplayName);
		}
	}

	private static Task SendAsync(ClientWebSocket socket, JsonObject body, CancellationToken cancellationToken) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);
}
