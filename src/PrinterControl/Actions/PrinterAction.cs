using System.Globalization;
using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal abstract class PrinterAction(PrinterRegistry registry) : IDynamicOptionsActionDefinition
{
	public const string PrinterParameter = "printer";

	private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);
	private IReadOnlyList<ActionParameter>? _parameters;

	protected PrinterRegistry Registry { get; } = registry;

	public abstract string Id { get; }

	public abstract LocalizedText Name { get; }

	public abstract LocalizedText Description { get; }

	public IReadOnlyList<ActionParameter> Parameters => _parameters ??=
	[
		ActionParameter.DynamicChoice(
			PrinterParameter,
			label: Strings.Actions.Printer.Label(),
			description: Strings.Actions.Printer.Description(),
			placeholder: Strings.Actions.Printer.Placeholder()),
		.. ActionParameters,
	];

	protected abstract IReadOnlyList<ActionParameter> ActionParameters { get; }

	public IActionExecutor CreateExecutor() => new Executor(this);

	protected abstract Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context);

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		if (context.ParameterName == PrinterParameter)
		{
			var printers = Registry.Printers;
			return printers.Count == 0
				? new DynamicOptionsResult { Options = [], Error = Strings.Errors.NoPrinter() }
				: new DynamicOptionsResult
				{
					Options = [.. printers.Select(p => new ActionParameterOption { Value = p.Config.Key, Label = p.Config.DisplayName })],
				};
		}

		var printer = Registry.Resolve(context.CurrentParameters.GetValueOrDefault(PrinterParameter)?.ToString());
		if (printer is null)
		{
			return new DynamicOptionsResult { Options = [], Error = Strings.Errors.PickPrinterFirst() };
		}

		try
		{
			return await GetOptionsAsync(printer, context, cancellationToken);
		}
		catch (PrinterException exception)
		{
			return new DynamicOptionsResult
			{
				Options = [],
				Error = exception.Failure == PrinterFailure.NotSupported
					? Strings.Errors.NotSupported(printer.Config.DisplayName)
					: Strings.Errors.Offline(printer.Config.DisplayName),
			};
		}
	}

	protected virtual Task<DynamicOptionsResult> GetOptionsAsync(
		PrinterConnection printer,
		DynamicOptionsContext context,
		CancellationToken cancellationToken) =>
		Task.FromResult(new DynamicOptionsResult { Options = [] });

	// Answers from the pushed state only: the host polls this while the button is shown, and calls it
	// with half-filled parameters while the user edits.
	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		var printer = Registry.Resolve(Text(parameters, PrinterParameter));
		return Task.FromResult(printer is null ? null : ReadState(printer, parameters));
	}

	protected virtual ActionStateSnapshot? ReadState(PrinterConnection printer, IReadOnlyDictionary<string, object?> parameters) => null;

	// Accepted, not failed, when the server took the command but the state lags (a pause waits for
	// buffered moves).
	internal static async Task<ActionResult> ConfirmAsync(
		PrinterConnection printer,
		Func<PrinterSnapshot, bool> reached,
		LocalizedText pending,
		CancellationToken cancellationToken,
		string? expectedState = null)
	{
		if (reached(printer.Snapshot))
		{
			return Succeeded(expectedState);
		}

		var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnChanged(object? sender, PrinterSnapshotChangedEventArgs e)
		{
			if (reached(e.Current))
			{
				signal.TrySetResult();
			}
		}

		printer.SnapshotChanged += OnChanged;
		try
		{
			if (reached(printer.Snapshot))
			{
				return Succeeded(expectedState);
			}

			await signal.Task.WaitAsync(ConfirmTimeout, cancellationToken);
			return Succeeded(expectedState);
		}
		catch (TimeoutException)
		{
			return expectedState is null ? ActionResult.Accepted(pending) : ActionResult.Accepted(pending, expectedState);
		}
		finally
		{
			printer.SnapshotChanged -= OnChanged;
		}
	}

	private static ActionResult Succeeded(string? expectedState) =>
		expectedState is null ? ActionResult.Success() : ActionResult.Success(expectedState);

	// A printer without the feature fails the action as unavailable.
	internal static T Require<T>(PrinterConnection printer)
		where T : class =>
		printer as T ?? throw new PrinterException(
			PrinterFailure.NotSupported,
			$"{printer.Config.DisplayName} does not support {typeof(T).Name}.");

	protected static string? Text(ActionExecutionContext context, string name) => Text(context.Parameters!, name);

	protected static string? Text(IReadOnlyDictionary<string, object?> parameters, string name) =>
		Value(parameters.GetValueOrDefault(name)) switch
		{
			string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
			_ => null,
		};

	protected static double? Number(ActionExecutionContext context, string name) =>
		Value(context.Parameters.GetValueOrDefault(name)) switch
		{
			double d => d,
			float f => f,
			int i => i,
			long l => l,
			decimal m => (double)m,
			string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
			_ => null,
		};

	protected static bool? Flag(ActionExecutionContext context, string name) =>
		Value(context.Parameters.GetValueOrDefault(name)) switch
		{
			bool b => b,
			string s when bool.TryParse(s, out var parsed) => parsed,
			_ => null,
		};

	protected static IReadOnlyList<string> List(ActionExecutionContext context, string name) =>
		context.Parameters.GetValueOrDefault(name) switch
		{
			JsonElement { ValueKind: JsonValueKind.Array } array =>
				[.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
			IEnumerable<object?> items => [.. items.Select(Value).OfType<string>()],
			string single when !string.IsNullOrWhiteSpace(single) => [single],
			_ => [],
		};

	protected static ActionResult Invalid(LocalizedText parameterLabel) =>
		ActionResult.Failed(ActionErrorCodes.InvalidParameter, MacroDeckStrings.Validation.Required(parameterLabel));

	private static object? Value(object? raw) => raw switch
	{
		JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
		JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
		JsonElement { ValueKind: JsonValueKind.True } => true,
		JsonElement { ValueKind: JsonValueKind.False } => false,
		JsonElement => null,
		_ => raw,
	};

	private sealed class Executor(PrinterAction action) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var registry = action.Registry;
			if (registry.Printers.Count == 0)
			{
				return ActionResult.Failed(ActionErrorCodes.NotConfigured, Strings.Errors.NoPrinter());
			}

			var printer = registry.Resolve(Text(context, PrinterParameter));
			if (printer is null)
			{
				return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.UnknownPrinter());
			}

			if (!printer.Snapshot.Online)
			{
				return ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.Offline(printer.Config.DisplayName));
			}

			try
			{
				return await action.RunAsync(printer, context);
			}
			catch (PrinterException exception)
			{
				return exception.Failure switch
				{
					PrinterFailure.Unreachable => ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.Offline(printer.Config.DisplayName)),
					PrinterFailure.Unauthorized or PrinterFailure.Forbidden => ActionResult.Failed(ActionErrorCodes.PermissionDenied, Strings.Errors.PermissionDenied()),
					PrinterFailure.Conflict => ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Errors.NotReady()),
					PrinterFailure.NotFound => ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Errors.NotFound()),
					PrinterFailure.BadRequest => ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Errors.Rejected()),
					PrinterFailure.NotSupported => ActionResult.Failed(ActionErrorCodes.Unavailable, Strings.Errors.NotSupported(printer.Config.DisplayName)),
					_ => ActionResult.Failed(ActionErrorCodes.ProviderError, Strings.Errors.Unexpected()),
				};
			}
		}
	}
}
