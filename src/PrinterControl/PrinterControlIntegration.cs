using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using PrinterControl.Actions;
using PrinterControl.Backends;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl;

internal sealed partial class PrinterControlIntegration : IPluginIntegration, IConfigFlowProvider
{
	private readonly PrinterRegistry _registry;
	private readonly PrinterBackends _backends;
	private readonly IPluginCatalogNotifier _catalogNotifier;
	private readonly ILogger _logger;
	private IIntegrationContext? _context;

	public PrinterControlIntegration(
		PrinterRegistry registry,
		PrinterBackends backends,
		IPluginCatalogNotifier catalogNotifier,
		ILogger logger)
	{
		_registry = registry;
		_backends = backends;
		_catalogNotifier = catalogNotifier;
		_logger = logger.ForContext<PrinterControlIntegration>();
		Actions =
		[
			new JobAction(registry),
			new PrintFileAction(registry),
			new SetTemperatureAction(registry),
			new PreheatAction(registry),
			new HomeAction(registry),
			new JogAction(registry),
			new ExtrudeAction(registry),
			new FanAction(registry),
			new RateAction(registry),
			new GcodeAction(registry),
			new ConnectionAction(registry),
			new SystemCommandAction(registry),
			new OutputAction(registry),
		];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public bool AllowsMultipleConfigurations => true;

	public IConfigFlow CreateConfigFlow() => new PrinterConfigFlow(_backends, _logger);

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_context = context;

		_registry.SnapshotChanged -= OnSnapshotChanged;
		_registry.SnapshotChanged += OnSnapshotChanged;
		_registry.EventReceived -= OnPrinterEvent;
		_registry.EventReceived += OnPrinterEvent;
		_registry.SettingsChanged -= OnSettingsChanged;
		_registry.SettingsChanged += OnSettingsChanged;

		try
		{
			var printers = await PrinterConfigReader.ReadAsync(context.Config, _backends, _logger, CancellationToken.None);
			await _registry.ApplyAsync(printers);
		}
		// The host calls this again after every config change; a failed read keeps the printers we have.
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_logger.Warning(exception, "Could not read the printer entries; keeping the current printers.");
		}

		_logger.Information("Initialized with {Count} printer(s).", _registry.Printers.Count);
		AnnounceVariablesAfterInitialization();
	}

	public Task ShutdownAsync()
	{
		_registry.SnapshotChanged -= OnSnapshotChanged;
		_registry.EventReceived -= OnPrinterEvent;
		_registry.SettingsChanged -= OnSettingsChanged;
		ShutdownVideoStreams();
		_context = null;
		return Task.CompletedTask;
	}

	partial void ShutdownVideoStreams();

	private void OnSnapshotChanged(object? sender, PrinterEventArgs<PrinterSnapshotChangedEventArgs> e) =>
		PublishStateEvents(e.Printer, e.Data.Previous, e.Data.Current);
}
