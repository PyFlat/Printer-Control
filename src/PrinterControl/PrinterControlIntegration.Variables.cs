using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk.Variables;
using PrinterControl.Core;
using PrinterControl.Variables;

namespace PrinterControl;

// Eager variables, one set per printer and grouped by it, like the built-in OBS and Twitch integrations.
// The host polls ReadAsync every second; that only reads the snapshot the push socket keeps current.
internal sealed partial class PrinterControlIntegration : IVariableProvider
{
	private string? _announcedPrinters;

	public IReadOnlyList<VariableDefinition> Variables =>
		PrinterVariables.Build(
			[.. _registry.Printers.Select(p => p.Config)],
			config => _registry.Find(config.Key)?.Settings.Outputs ?? []);

	public IReadOnlyList<VariableDefinition> DeclaredVariables =>
		_registry.Printers.Count > 0 ? Variables : PrinterVariables.Templates();

	public bool VariablesDependOnConfiguration => true;

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(
			PrinterVariables.TryResolve(localId, out var key, out var field)
				? PrinterVariables.Read(field, _registry.Find(key)?.Snapshot)
				: PrinterVariables.TryResolveOutput(localId, out key, out var output)
					? PrinterVariables.ReadOutput(_registry.Find(key), output)
					: VariableReading.Unavailable);

	// The first describe can run before InitializeAsync read the printers, so the set is announced once it
	// returned (from inside, older hosts re-initialize). Outputs arrive later and announce the same way.
	private void AnnounceVariablesAfterInitialization()
	{
		var printers = string.Join('\n', _registry.Printers.Select(p =>
			$"{p.Config.Key}={p.Config.DisplayName}:{string.Join(',', p.Settings.Outputs.Select(o => $"{o.Id}={o.Name}"))}"));
		if (Interlocked.Exchange(ref _announcedPrinters, printers) == printers)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromSeconds(1));
			_catalogNotifier.CatalogChanged(CapabilityKinds.Variables, reason: "Printers changed");
		});
	}

	private void OnSettingsChanged(object? sender, PrinterEventArgs<EventArgs> e) => AnnounceVariablesAfterInitialization();
}
