using MacroDeck.Localization;
using MacroDeck.Sdk.ConfigFlow;
using NUnit.Framework;
using PrinterControl.Backends;
using PrinterControl.Backends.Moonraker;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests.Backends;

[TestFixture]
public sealed class MoonrakerContractTests : BackendContract
{
	private protected override IPrinterBackend CreateBackend(IHttpClientFactory http) => new MoonrakerBackend(http);

	private protected override async Task<IFakePrinterServer> StartServerAsync() => await FakeMoonraker.StartAsync();
}

[TestFixture]
public sealed class MoonrakerTests
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private FakeMoonraker _server = null!;
	private MoonrakerConnection? _printer;

	[SetUp]
	public async Task StartServer() => _server = await FakeMoonraker.StartAsync();

	[TearDown]
	public async Task StopServer()
	{
		if (_printer is { } printer)
		{
			_printer = null;
			await printer.DisposeAsync();
		}

		await _server.DisposeAsync();
	}

	private static PrinterConfigFlow Flow() => new(
		new PrinterBackends([
			new OctoPrintBackend(new TestHttpClientFactory(), Serilog.Core.Logger.None),
			new MoonrakerBackend(new TestHttpClientFactory()),
		]),
		Serilog.Core.Logger.None);

	private Task<ConfigFlowResult> SubmitAddressAsync(PrinterConfigFlow flow) =>
		flow.SubmitAsync(
			PrinterConfigFlow.ConnectionStepId,
			new Dictionary<string, object?> { [ConfigKeys.Url] = _server.BaseUri.ToString() },
			new FakeFlowContext(),
			CancellationToken.None);

	private async Task<MoonrakerConnection> OnlineAsync()
	{
		_printer = new MoonrakerConnection(
			new PrinterConfig(Guid.NewGuid(), MoonrakerBackend.BackendId, "Voron", _server.BaseUri, _server.ApiKey, null),
			Serilog.Core.Logger.None);
		_printer.Start();
		var deadline = DateTime.UtcNow + Patience;
		while (!_printer.Snapshot.Online || _printer.Settings.InstanceName is null)
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "never came online");
			await Task.Delay(20);
		}

		return _printer;
	}

	[Test]
	public async Task A_trusted_client_is_set_up_without_a_key()
	{
		_server.ApiKey = null;
		await using var flow = Flow();

		var result = await SubmitAddressAsync(flow);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(result.EntryTitle, Is.EqualTo("voron"), "the host name Moonraker reports");
			Assert.That(result.Values?[ConfigKeys.Backend].Value, Is.EqualTo(MoonrakerBackend.BackendId));
		}
	}

	[Test]
	public async Task Without_trust_setup_asks_for_the_api_key_and_checks_it()
	{
		await using var flow = Flow();

		var ask = await SubmitAddressAsync(flow);
		var wrong = await flow.SubmitAsync(
			MoonrakerSetup.ApiKeyStepId,
			new Dictionary<string, object?> { [ConfigKeys.ApiKey] = "wrong" },
			new FakeFlowContext(),
			CancellationToken.None);
		var right = await flow.SubmitAsync(
			MoonrakerSetup.ApiKeyStepId,
			new Dictionary<string, object?> { [ConfigKeys.ApiKey] = FakeMoonraker.Key },
			new FakeFlowContext(),
			CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(ask.NextStep?.StepId, Is.EqualTo(MoonrakerSetup.ApiKeyStepId), "recognized as Moonraker although it refused the anonymous request");
			Assert.That(wrong.FieldErrors?.Keys, Does.Contain(ConfigKeys.ApiKey));
			Assert.That(right.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
		}
	}

	[Test]
	public async Task The_job_reports_progress_file_and_position()
	{
		await _server.ReportAsync(PrinterStatus.Printing);
		var printer = await OnlineAsync();

		var snapshot = printer.Snapshot;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(snapshot.Status, Is.EqualTo(PrinterStatus.Printing));
			Assert.That(snapshot.FileName, Is.EqualTo("benchy.gcode"));
			Assert.That(snapshot.FileId, Is.EqualTo("benchy.gcode"));
			Assert.That(snapshot.Completion, Is.EqualTo(50));
			Assert.That(snapshot.PrintTime, Is.EqualTo(120));
			Assert.That(snapshot.PrintTimeLeft, Is.EqualTo(120), "from the progress, without slicer metadata");
			Assert.That(snapshot.CurrentZ, Is.EqualTo(0.2));
		}
	}

	[Test]
	public async Task The_webcam_and_power_devices_come_from_moonraker()
	{
		var printer = await OnlineAsync();

		using (Assert.EnterMultipleScope())
		{
			Assert.That(printer.Settings.Webcam.StreamUrl, Is.EqualTo("/webcam/?action=stream"));
			Assert.That(printer.Settings.Webcam.Rotation, Is.EqualTo(90));
			Assert.That(printer.Settings.Outputs.Select(o => o.Id), Is.EqualTo((string[])["power-chamber-light"]));
		}
	}

	[Test]
	public async Task A_webcam_changed_in_moonraker_is_read_again()
	{
		var printer = await OnlineAsync();

		_server.WebcamRotation = 180;
		await _server.ChangeWebcamsAsync();

		var deadline = DateTime.UtcNow + Patience;
		while (printer.Settings.Webcam.Rotation != 180)
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the webcam was never read again");
			await Task.Delay(20);
		}
	}

	[Test]
	public async Task Switching_a_power_device_addresses_it_by_name()
	{
		var printer = await OnlineAsync();

		await printer.SetOutputAsync("power-chamber-light", true, CancellationToken.None);
		var states = await printer.ReadOutputsAsync(CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(_server.PowerDevices["Chamber Light"], Is.EqualTo("on"));
			Assert.That(states["power-chamber-light"], Is.True);
		}
	}

	[Test]
	public async Task Temperatures_are_set_with_klippers_heater_names()
	{
		var printer = await OnlineAsync();

		await printer.SetTemperatureAsync(HeaterIds.Bed, 60, CancellationToken.None);
		await printer.SetTemperatureAsync(HeaterIds.Extruder, 215.5, CancellationToken.None);

		Assert.That(_server.Calls, Is.SupersetOf((string[])[
			"printer.gcode.script SET_HEATER_TEMPERATURE HEATER=heater_bed TARGET=60",
			"printer.gcode.script SET_HEATER_TEMPERATURE HEATER=extruder TARGET=215.5",
		]));
	}

	[Test]
	public async Task A_chamber_without_a_heater_cannot_be_set()
	{
		var printer = await OnlineAsync();

		var failure = Assert.ThrowsAsync<PrinterException>(() => printer.SetTemperatureAsync(HeaterIds.Chamber, 40, CancellationToken.None));

		Assert.That(failure?.Failure, Is.EqualTo(PrinterFailure.NotSupported));
	}

	[Test]
	public async Task System_commands_have_names_and_reach_moonraker()
	{
		var printer = await OnlineAsync();

		var commands = await printer.GetSystemCommandsAsync(CancellationToken.None);
		await printer.RunSystemCommandAsync("klipper/firmware-restart", CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(commands.Select(c => c.Name), Has.None.EqualTo(default(LocalizedText)));
			Assert.That(_server.Calls, Does.Contain("printer.firmware_restart"));
		}
	}

	[Test]
	public async Task A_resumed_print_is_not_reported_as_started_again()
	{
		await _server.ReportAsync(PrinterStatus.Paused);
		var printer = await OnlineAsync();
		var received = new TaskCompletionSource<PrinterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		printer.EventReceived += (_, e) => received.TrySetResult(e.Event);

		await _server.RaiseAsync(PrinterEventKind.PrintStarted);

		Assert.That((await received.Task.WaitAsync(Patience)).Kind, Is.EqualTo(PrinterEventKind.PrintResumed));
	}
}
