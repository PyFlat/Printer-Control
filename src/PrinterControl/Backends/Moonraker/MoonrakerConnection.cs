using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends.Moonraker;

// Klippy's state comes from server.info and notify_klippy_*, the printer objects from a subscription
// while it is ready. Power devices are polled, since Moonraker announces no change of theirs.
internal sealed class MoonrakerConnection(PrinterConfig config, ILogger logger)
	: PrinterConnection(config, logger), IJobControl, IFileControl, ITemperatureControl, IMotionControl, ISystemCommands, IOutputControl
{
	private const string ClientName = "Macro Deck Printer Control";
	private static readonly TimeSpan StartupPoll = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan PowerPollInterval = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan GcodeRefusalWindow = TimeSpan.FromSeconds(1);

	private static readonly string[] SystemCommandIds = ["klipper/firmware-restart", "klipper/restart", "moonraker/restart", "host/reboot", "host/shutdown"];

	private readonly Lock _gate = new();
	private readonly MoonrakerStatus _status = new();
	private readonly Channel<bool> _webcamsChanged = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
	private MoonrakerRpc? _rpc;
	private string _klippy = "disconnected";
	private string? _klippyMessage;
	private string? _printState;
	private IReadOnlyCollection<string> _printerObjects = [];
	private TaskCompletionSource _klippyChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private MoonrakerRpc Rpc => Volatile.Read(ref _rpc)
		?? throw new PrinterException(PrinterFailure.Unreachable, $"{Config.DisplayName} is not connected.");

	protected override async Task RunSessionAsync(CancellationToken cancellationToken)
	{
		await using var rpc = await MoonrakerRpc.ConnectAsync(Config.BaseUri, Config.ApiKey, OnNotification, cancellationToken);
		Volatile.Write(ref _rpc, rpc);
		try
		{
			await rpc.CallAsync(
				"server.connection.identify",
				new { client_name = ClientName, version = "1.0", type = "other", url = "https://github.com/PyFlat/Printer-Control" },
				cancellationToken);
			await RefreshSettingsAsync(rpc, cancellationToken);
			ReportConnected();

			using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var power = PollPowerAsync(session.Token);
			var webcams = FollowWebcamsAsync(rpc, session.Token);
			try
			{
				await FollowKlippyAsync(rpc, cancellationToken);
			}
			finally
			{
				await session.CancelAsync();
				await power.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
				await webcams.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			}
		}
		finally
		{
			Volatile.Write(ref _rpc, null);
			lock (_gate)
			{
				_status.Clear();
				_klippy = "disconnected";
				_printState = null;
			}
		}
	}

	// Returns when the websocket closes.
	private async Task FollowKlippyAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		while (true)
		{
			var changed = ResetKlippySignal();
			var info = await rpc.CallAsync("server.info", null, cancellationToken);
			var state = info.Str("klippy_state") ?? "disconnected";
			if (state == "ready")
			{
				await SubscribeAsync(rpc, cancellationToken);
			}
			else
			{
				var message = state is "shutdown" or "error" ? await KlippyMessageAsync(rpc, cancellationToken) : null;
				Publish(() =>
				{
					_klippy = state;
					_klippyMessage = message;
					_printState = null;
				});
			}

			// Outside "ready" Moonraker announces only some transitions, so server.info is asked again.
			var next = await Task.WhenAny(changed, rpc.Completion, Task.Delay(state == "ready" ? Timeout.InfiniteTimeSpan : StartupPoll, cancellationToken));
			if (next == rpc.Completion)
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private async Task SubscribeAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		var listed = await rpc.CallAsync("printer.objects.list", null, cancellationToken);
		var available = listed.Arr("objects")?.EnumerateArray().Select(o => o.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
			?? [];

		var objects = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			["print_stats"] = ["state", "filename", "print_duration", "message"],
			["virtual_sdcard"] = ["progress"],
			["gcode_move"] = ["gcode_position"],
		};
		foreach (var heater in available.Where(o => MoonrakerHeaters.ToHeaterId(o) is not null))
		{
			objects[heater] = ["temperature", "target"];
		}

		var subscribed = await rpc.CallAsync(
			"printer.objects.subscribe",
			new { objects = objects.Where(o => available.Contains(o.Key)).ToDictionary() },
			cancellationToken);

		Publish(() =>
		{
			_printerObjects = available;
			_status.Clear();
			_status.Merge(subscribed.Obj("status") ?? default);
			_klippy = "ready";
			_klippyMessage = null;
			_printState = _status.PrintState;
		});
	}

	private static async Task<string?> KlippyMessageAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		try
		{
			return (await rpc.CallAsync("printer.info", null, cancellationToken)).Str("state_message");
		}
		catch (PrinterException)
		{
			return null;
		}
	}

	private void OnNotification(string method, JsonElement parameters)
	{
		switch (method)
		{
			case "notify_status_update" when parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() > 0:
				var diff = parameters[0];
				Publish(() => _status.Merge(diff));
				break;
			case "notify_klippy_ready":
				Klippy(PrinterEventKind.Connected, "klippy_ready", null);
				break;
			case "notify_klippy_shutdown":
				Klippy(PrinterEventKind.Error, "klippy_shutdown", "shutdown");
				break;
			case "notify_klippy_disconnected":
				Klippy(PrinterEventKind.Disconnected, "klippy_disconnected", "disconnected");
				break;
			case "notify_webcams_changed":
				_webcamsChanged.Writer.TryWrite(true);
				break;
		}
	}

	// Notifications arrive on the socket's receive loop, which a call made from there would wait on, so
	// the settings are read again here.
	private async Task FollowWebcamsAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		while (await _webcamsChanged.Reader.WaitToReadAsync(cancellationToken))
		{
			_webcamsChanged.Reader.TryRead(out _);
			try
			{
				await RefreshSettingsAsync(rpc, cancellationToken);
			}
			catch (PrinterException)
			{
			}
		}
	}

	private void Klippy(PrinterEventKind kind, string type, string? state)
	{
		if (state is not null)
		{
			Publish(() =>
			{
				_klippy = state;
				_printState = null;
			});
		}

		PublishEvent(new PrinterEvent(type, default) { Kind = kind });
		Volatile.Read(ref _klippyChanged).TrySetResult();
	}

	private Task ResetKlippySignal()
	{
		var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Volatile.Write(ref _klippyChanged, signal);
		return signal.Task;
	}

	// Applies a change under the lock, then publishes the snapshot and the print events it implies
	// outside it. A first subscription sets the print state without claiming a transition.
	private void Publish(Action change)
	{
		PrinterSnapshot snapshot;
		PrinterEvent? printEvent = null;
		lock (_gate)
		{
			var before = _printState;
			change();
			snapshot = _status.ToSnapshot(_klippy, _klippyMessage);
			if (_klippy == "ready" && _status.PrintState is { } now && now != before)
			{
				_printState = now;
				if (before is not null)
				{
					printEvent = PrintEvent(before, now, snapshot);
				}
			}
		}

		PublishSnapshot(snapshot);
		if (printEvent is not null)
		{
			PublishEvent(printEvent);
		}
	}

	// Klipper has no job events; they follow from print_stats.state changing.
	private PrinterEvent? PrintEvent(string before, string now, PrinterSnapshot snapshot)
	{
		var kind = now switch
		{
			"printing" => before == "paused" ? PrinterEventKind.PrintResumed : PrinterEventKind.PrintStarted,
			"paused" => PrinterEventKind.PrintPaused,
			"complete" => PrinterEventKind.PrintDone,
			"cancelled" => PrinterEventKind.PrintCancelled,
			"error" => PrinterEventKind.PrintFailed,
			_ => (PrinterEventKind?)null,
		};
		if (kind is null)
		{
			return null;
		}

		var file = _status.FileName;
		var duration = snapshot.PrintTime;
		var reason = kind == PrinterEventKind.PrintFailed ? snapshot.Error : null;
		return new PrinterEvent(now, JsonSerializer.SerializeToElement(new { filename = file, print_duration = duration, message = reason }))
		{
			Kind = kind,
			File = file,
			Duration = kind is PrinterEventKind.PrintDone or PrinterEventKind.PrintCancelled or PrinterEventKind.PrintFailed ? duration : null,
			Reason = reason,
		};
	}

	private async Task RefreshSettingsAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		string? hostname = null;
		try
		{
			hostname = (await rpc.CallAsync("printer.info", null, cancellationToken)).Str("hostname");
		}
		catch (PrinterException)
		{
		}

		PublishSettings(new PrinterSettings(
			hostname,
			await ReadWebcamAsync(rpc, cancellationToken),
			[],
			await ReadOutputsQuietlyAsync(rpc, cancellationToken)));
	}

	// The first enabled MJPEG webcam; streams of other kinds cannot be relayed.
	private static async Task<WebcamSettings> ReadWebcamAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		JsonElement list;
		try
		{
			list = await rpc.CallAsync("server.webcams.list", null, cancellationToken);
		}
		catch (PrinterException)
		{
			return WebcamSettings.None;
		}

		foreach (var webcam in list.Arr("webcams")?.EnumerateArray() ?? default)
		{
			if (webcam.Bool("enabled") == false || webcam.Str("service")?.Contains("mjpeg", StringComparison.OrdinalIgnoreCase) != true)
			{
				continue;
			}

			return new WebcamSettings(
				true,
				webcam.Str("stream_url"),
				webcam.Str("snapshot_url"),
				webcam.Bool("flip_horizontal") ?? false,
				webcam.Bool("flip_vertical") ?? false,
				webcam.Num("rotation") is { } rotation ? (int)rotation : 0,
				webcam.Str("aspect_ratio"));
		}

		return WebcamSettings.None;
	}

	public Task RunJobCommandAsync(JobCommand command, CancellationToken cancellationToken) => command switch
	{
		JobCommand.Pause => Rpc.CallAsync("printer.print.pause", null, cancellationToken),
		JobCommand.Resume => Rpc.CallAsync("printer.print.resume", null, cancellationToken),
		JobCommand.Cancel => Rpc.CallAsync("printer.print.cancel", null, cancellationToken),
		JobCommand.Start or JobCommand.Restart => StartAsync(LoadedFile(), cancellationToken),
		_ => throw new ArgumentOutOfRangeException(nameof(command)),
	};

	// Klipper keeps the last printed file loaded; "start" and "restart" print it (again).
	private string LoadedFile()
	{
		lock (_gate)
		{
			return _status.FileName ?? throw new PrinterException(PrinterFailure.Conflict, $"{Config.DisplayName} has no file loaded.");
		}
	}

	private Task<JsonElement> StartAsync(string file, CancellationToken cancellationToken) =>
		Rpc.CallAsync("printer.print.start", new { filename = file }, cancellationToken);

	public async Task<IReadOnlyList<PrintFile>> GetFilesAsync(CancellationToken cancellationToken)
	{
		var files = await Rpc.CallAsync("server.files.list", new { root = "gcodes" }, cancellationToken);
		return files.ValueKind != JsonValueKind.Array
			? []
			: [.. files.EnumerateArray()
				.Where(f => f.Str("path") is { Length: > 0 })
				.Select(f => new PrintFile(
					f.Str("path")!,
					f.Str("path")!,
					f.Num("size") is { } size ? (long)size : null,
					f.Num("modified") is { } modified ? DateTimeOffset.FromUnixTimeMilliseconds((long)(modified * 1000)) : null))];
	}

	// Moonraker loads a file only by printing it.
	public Task SelectFileAsync(string fileId, bool print, CancellationToken cancellationToken) =>
		print ? StartAsync(fileId, cancellationToken) : throw NotSupported("loading a file without printing it");

	public Task SetTemperatureAsync(string heater, double target, CancellationToken cancellationToken)
	{
		IReadOnlyCollection<string> objects;
		lock (_gate)
		{
			objects = _printerObjects;
		}

		var klipperHeater = MoonrakerHeaters.ToKlipperHeater(heater, objects) ?? throw NotSupported($"setting the {heater} temperature");
		return SendGcodeAsync(
			[string.Create(CultureInfo.InvariantCulture, $"SET_HEATER_TEMPERATURE HEATER={klipperHeater} TARGET={target:0.#}")],
			cancellationToken);
	}

	// Moonraker answers a script only once it ran, which takes as long as the moves do; a refusal
	// (an unknown command, unhomed axes) arrives at once.
	public Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken) =>
		Rpc.CallWithoutWaitingAsync("printer.gcode.script", new { script = string.Join('\n', commands) }, GcodeRefusalWindow, cancellationToken);

	public Task<IReadOnlyList<SystemCommand>> GetSystemCommandsAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<SystemCommand>>(
		[
			new(SystemCommandIds[0], Strings.Moonraker.Commands.FirmwareRestart(), true),
			new(SystemCommandIds[1], Strings.Moonraker.Commands.RestartKlipper(), true),
			new(SystemCommandIds[2], Strings.Moonraker.Commands.RestartMoonraker(), true),
			new(SystemCommandIds[3], Strings.Moonraker.Commands.Reboot(), true),
			new(SystemCommandIds[4], Strings.Moonraker.Commands.Shutdown(), true),
		]);

	public Task RunSystemCommandAsync(string commandId, CancellationToken cancellationToken) => commandId switch
	{
		"klipper/firmware-restart" => Rpc.CallAsync("printer.firmware_restart", null, cancellationToken),
		"klipper/restart" => Rpc.CallAsync("printer.restart", null, cancellationToken),
		"moonraker/restart" => Rpc.CallAsync("machine.services.restart", new { service = "moonraker" }, cancellationToken),
		"host/reboot" => Rpc.CallAsync("machine.reboot", null, cancellationToken),
		"host/shutdown" => Rpc.CallAsync("machine.shutdown", null, cancellationToken),
		_ => throw new PrinterException(PrinterFailure.NotFound, $"{commandId} is not a Moonraker system command."),
	};

	// Outputs are Moonraker's [power <name>] devices.
	public async Task<IReadOnlyList<PrinterOutput>> GetOutputsAsync(CancellationToken cancellationToken) =>
		[.. (await ReadDevicesAsync(Rpc, cancellationToken)).Select(d => d.Output)];

	public async Task<IReadOnlyDictionary<string, bool>> ReadOutputsAsync(CancellationToken cancellationToken)
	{
		var states = (await ReadDevicesAsync(Rpc, cancellationToken))
			.Where(d => d.Status is "on" or "off")
			.ToDictionary(d => d.Output.Id, d => d.Status == "on", StringComparer.Ordinal);
		PublishOutputStates(states);
		return states;
	}

	public async Task SetOutputAsync(string outputId, bool on, CancellationToken cancellationToken)
	{
		var device = (await ReadDevicesAsync(Rpc, cancellationToken)).FirstOrDefault(d => d.Output.Id == outputId)
			?? throw new PrinterException(PrinterFailure.NotFound, $"No power device {outputId}.");
		await Rpc.CallAsync("machine.device_power.post_device", new { device = device.Name, action = on ? "on" : "off" }, cancellationToken);
	}

	private sealed record PowerDevice(string Name, string Status, PrinterOutput Output);

	private static async Task<IReadOnlyList<PowerDevice>> ReadDevicesAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		var result = await rpc.CallAsync("machine.device_power.devices", null, cancellationToken);
		return [.. (result.Arr("devices")?.EnumerateArray() ?? default)
			.Where(d => d.Str("device") is { Length: > 0 })
			.Select(d => new PowerDevice(d.Str("device")!, d.Str("status") ?? string.Empty, new PrinterOutput(OutputId(d.Str("device")!), d.Str("device")!)))];
	}

	private static async Task<IReadOnlyList<PrinterOutput>> ReadOutputsQuietlyAsync(MoonrakerRpc rpc, CancellationToken cancellationToken)
	{
		try
		{
			return [.. (await ReadDevicesAsync(rpc, cancellationToken)).Select(d => d.Output)];
		}
		catch (PrinterException)
		{
			return [];
		}
	}

	// An output id is a local id fragment ([a-z0-9-]); the device name is what Moonraker addresses.
	private static string OutputId(string device)
	{
		var id = new string([.. device.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
		return "power-" + (id.Length == 0 ? "device" : id);
	}

	private async Task PollPowerAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			if (Settings.Outputs.Count > 0)
			{
				try
				{
					await ReadOutputsAsync(cancellationToken);
				}
				catch (PrinterException exception)
				{
					Logger.Debug("Reading the power devices of {Printer} failed: {Message}", Config.DisplayName, exception.Message);
					PublishOutputStates(new Dictionary<string, bool>());
				}
			}

			await Task.Delay(PowerPollInterval, cancellationToken);
		}
	}
}
