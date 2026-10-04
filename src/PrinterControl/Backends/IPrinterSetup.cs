using MacroDeck.Localization;
using MacroDeck.Sdk.ConfigFlow;

namespace PrinterControl.Backends;

// Steps after the shared address step, typically signing in; a server without a login is Done at once.
// Step ids must differ from PrinterConfigFlow.ConnectionStepId; Back can resubmit an earlier step.
internal interface IPrinterSetup : IAsyncDisposable
{
	Task<PrinterSetupResult> StartAsync(CancellationToken cancellationToken);

	Task<PrinterSetupResult> SubmitAsync(string stepId, IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken);

	ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
}

// Done carries the server's own name (used when the user gave none) and values beyond the form fields,
// such as a granted API key.
internal sealed class PrinterSetupResult
{
	private PrinterSetupResult()
	{
	}

	public ConfigFlowResult? Show { get; private init; }

	public string? InstanceName { get; private init; }

	public IReadOnlyDictionary<string, ConfigFlowValue> Values { get; private init; } = new Dictionary<string, ConfigFlowValue>();

	public static PrinterSetupResult Step(ConfigFlowStep step) => new() { Show = ConfigFlowResult.Step(step) };

	public static PrinterSetupResult Error(
		ConfigFlowStep step,
		LocalizedText? message = null,
		IReadOnlyDictionary<string, LocalizedText>? fieldErrors = null) =>
		new()
		{
			Show = message is { } text
				? ConfigFlowResult.Error(step, text, fieldErrors)
				: ConfigFlowResult.Error(step, fieldErrors: fieldErrors),
		};

	public static PrinterSetupResult Done(string? instanceName = null, IReadOnlyDictionary<string, ConfigFlowValue>? values = null) =>
		new() { InstanceName = instanceName, Values = values ?? new Dictionary<string, ConfigFlowValue>() };
}
