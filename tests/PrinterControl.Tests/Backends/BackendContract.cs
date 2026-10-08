using NUnit.Framework;
using PrinterControl.Backends;
using PrinterControl.Backends.Moonraker;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.Core;
using PrinterControl.Tests.Backends.Example;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests.Backends;

// Every backend's fake server, so each backend can check it claims none but its own. Add a new
// backend's fake here.
internal static class AllFakeServers
{
	public static IReadOnlyList<(string BackendId, Func<Task<IFakePrinterServer>> Start)> All { get; } =
	[
		(OctoPrintBackend.BackendId, async () => await FakeOctoPrint.StartAsync()),
		(MoonrakerBackend.BackendId, async () => await FakeMoonraker.StartAsync()),
		(ExampleBackend.BackendId, async () => await ExampleServer.StartAsync()),
	];
}

// What every backend has to do, run against its fake server. A backend's test class derives from this
// and says how to build the backend and start its fake (see Example/ExampleContractTests).
public abstract class BackendContract
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private readonly TestHttpClientFactory _http = new();
	private IFakePrinterServer _server = null!;
	private PrinterConnection? _printer;

	private protected abstract IPrinterBackend CreateBackend(IHttpClientFactory http);

	private protected abstract Task<IFakePrinterServer> StartServerAsync();

	private IPrinterBackend Backend => CreateBackend(_http);

	private PrinterConnection Printer => _printer ??= Connect();

	[SetUp]
	public async Task StartServer() => _server = await StartServerAsync();

	[TearDown]
	public async Task StopServer()
	{
		// NUnit reuses the fixture instance for every test.
		if (_printer is { } printer)
		{
			_printer = null;
			await printer.DisposeAsync();
		}

		await _server.DisposeAsync();
	}

	private PrinterConnection Connect()
	{
		var backend = Backend;
		var printer = backend.Connect(
			new PrinterConfig(Guid.NewGuid(), backend.Id, "Contract", _server.BaseUri, _server.ApiKey, null),
			_http.CreateClient("contract"),
			Serilog.Core.Logger.None);
		printer.Start();
		return printer;
	}

	private async Task UntilAsync(Func<PrinterSnapshot, bool> reached, string what)
	{
		var deadline = DateTime.UtcNow + Patience;
		while (!reached(Printer.Snapshot))
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), what);
			await Task.Delay(20);
		}
	}

	private async Task OnlineAsync(PrinterStatus status = PrinterStatus.Operational)
	{
		await _server.ReportAsync(status);
		await UntilAsync(s => s.Online && s.Status == status, $"the printer never reported {status}");
	}

	[Test]
	public async Task It_recognizes_its_own_server() =>
		Assert.That(await Backend.DetectAsync(_server.BaseUri, CancellationToken.None), Is.True);

	[Test]
	public async Task It_does_not_claim_a_web_server_without_printer_software()
	{
		await using var other = await NotAPrinterServer.StartAsync();

		Assert.That(await Backend.DetectAsync(other.BaseUri, CancellationToken.None), Is.False);
	}

	[Test]
	public async Task It_does_not_claim_another_backends_server()
	{
		var backend = Backend;
		foreach (var (backendId, start) in AllFakeServers.All.Where(s => s.BackendId != backend.Id))
		{
			await using var other = await start();
			Assert.That(await backend.DetectAsync(other.BaseUri, CancellationToken.None), Is.False, $"claimed the {backendId} fake");
		}
	}

	[Test]
	public async Task Nothing_answering_is_unreachable()
	{
		var failure = Assert.ThrowsAsync<PrinterException>(() => Backend.DetectAsync(new Uri("http://127.0.0.1:1/"), CancellationToken.None));

		Assert.That(failure?.Failure, Is.EqualTo(PrinterFailure.Unreachable));
	}

	// Names rather than the internal enums, which a public test method cannot take.
	[TestCase(nameof(PrinterStatus.Operational))]
	[TestCase(nameof(PrinterStatus.Printing))]
	[TestCase(nameof(PrinterStatus.Paused))]
	[TestCase(nameof(PrinterStatus.Disconnected))]
	public Task It_maps_the_steady_statuses(string status) => OnlineAsync(Enum.Parse<PrinterStatus>(status));

	[Test]
	public async Task Temperatures_use_the_heater_ids()
	{
		await OnlineAsync(PrinterStatus.Printing);
		await UntilAsync(s => s.Heater(HeaterIds.Extruder) is not null, "no extruder temperature arrived");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(Printer.Snapshot.Heater(HeaterIds.Extruder), Is.EqualTo(new Temperature(215, 215)));
			Assert.That(Printer.Snapshot.Heater(HeaterIds.Bed), Is.EqualTo(new Temperature(60, 60)));
			Assert.That(
				Printer.Snapshot.Temperatures.Keys,
				Has.All.Matches<string>(k => k is HeaterIds.Bed or HeaterIds.Chamber || k.StartsWith(HeaterIds.Extruder, StringComparison.Ordinal)),
				"heaters are named by HeaterIds, not by the server's own names");
		}
	}

	[TestCase(nameof(PrinterEventKind.PrintStarted))]
	[TestCase(nameof(PrinterEventKind.PrintDone))]
	[TestCase(nameof(PrinterEventKind.PrintPaused))]
	public async Task Events_arrive_with_their_kind(string name)
	{
		var kind = Enum.Parse<PrinterEventKind>(name);
		var received = new TaskCompletionSource<PrinterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		Printer.EventReceived += (_, e) => received.TrySetResult(e.Event);
		await OnlineAsync();

		await _server.RaiseAsync(kind);

		Assert.That((await received.Task.WaitAsync(Patience)).Kind, Is.EqualTo(kind));
	}

	[Test]
	public async Task It_goes_offline_when_the_server_goes_away()
	{
		await OnlineAsync();

		await _server.DisposeAsync();

		await UntilAsync(s => !s.Online, "the printer stayed online");
	}

	// One harmless call per feature the connection claims; a feature it does not claim is skipped.
	private static IEnumerable<TestCaseData> Features()
	{
		yield return Feature<IJobControl>(p => p.RunJobCommandAsync(JobCommand.Pause, CancellationToken.None), sends: true);
		yield return Feature<ITemperatureControl>(p => p.SetTemperatureAsync(HeaterIds.Bed, 60, CancellationToken.None), sends: true);
		yield return Feature<IGcodeControl>(p => p.SendGcodeAsync(["M117 contract"], CancellationToken.None), sends: true);
		yield return Feature<IMotionControl>(p => p.HomeAsync(["x"], CancellationToken.None), sends: true);
		yield return Feature<IPrinterLink>(p => p.ConnectPrinterAsync(CancellationToken.None), sends: true);
		yield return Feature<IFileControl>(p => p.GetFilesAsync(CancellationToken.None), sends: false);
		yield return Feature<ISystemCommands>(p => p.GetSystemCommandsAsync(CancellationToken.None), sends: false);
		yield return Feature<IOutputControl>(p => p.GetOutputsAsync(CancellationToken.None), sends: false);
	}

	private static TestCaseData Feature<T>(Func<T, Task> call, bool sends)
		where T : class =>
		new TestCaseData(typeof(T), (Func<object, Task>)(printer => call((T)printer)), sends).SetArgDisplayNames(typeof(T).Name);

	[TestCaseSource(nameof(Features))]
	public async Task Every_claimed_feature_reaches_the_server(Type feature, Func<object, Task> call, bool sends)
	{
		Assume.That(feature.IsInstanceOfType(Printer), $"{Printer.GetType().Name} does not claim {feature.Name}");
		await OnlineAsync(PrinterStatus.Printing);
		var before = _server.CommandsReceived;

		await call(Printer);

		if (sends)
		{
			Assert.That(_server.CommandsReceived, Is.GreaterThan(before), "the server received no command");
		}
	}
}
