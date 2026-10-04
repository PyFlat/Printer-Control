using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal sealed class OutputAction(PrinterRegistry registry) : PrinterAction(registry), IStateProviderActionDefinition
{
	public const string OutputParameter = "output";
	public const string StateParameter = "state";

	public override string Id => "switch-output";

	public override LocalizedText Name => Strings.Actions.Output.Name();

	public override LocalizedText Description => Strings.Actions.Output.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.DynamicChoice(
			OutputParameter,
			label: Strings.Actions.Output.Target(),
			description: Strings.Actions.Output.TargetDescription(),
			required: true),
		ActionParameter.Choice(
			StateParameter,
			[
				new() { Value = "on", Label = Strings.Actions.Output.On() },
				new() { Value = "off", Label = Strings.Actions.Output.Off() },
				new() { Value = "toggle", Label = Strings.Actions.Output.Toggle() },
			],
			label: Strings.Actions.Output.State(),
			defaultValue: "toggle",
			required: true),
	];

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new("on", MacroDeckStrings.States.On()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#b7791f" } },
		new("off", MacroDeckStrings.States.Off()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2d3748" } },
		new("unavailable", MacroDeckStrings.States.Unavailable())
		{
			DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568", LabelColor = "#cbd5e0" },
		},
	];

	protected override ActionStateSnapshot? ReadState(PrinterConnection printer, IReadOnlyDictionary<string, object?> parameters)
	{
		if (Text(parameters, OutputParameter) is not { } outputId)
		{
			return null;
		}

		var active = printer.Snapshot.Online && printer.OutputStates.TryGetValue(outputId, out var on)
			? on ? "on" : "off"
			: "unavailable";
		return new ActionStateSnapshot(States, active);
	}

	protected override async Task<DynamicOptionsResult> GetOptionsAsync(
		PrinterConnection printer,
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		var outputs = await Require<IOutputControl>(printer).GetOutputsAsync(cancellationToken);
		return outputs.Count == 0
			? new DynamicOptionsResult { Options = [], Error = Strings.Actions.Output.NoOutputs(printer.Config.DisplayName) }
			: new DynamicOptionsResult
			{
				Options = [.. outputs.Select(o => new ActionParameterOption { Value = o.Id, Label = o.Name })],
				CacheSeconds = 30,
			};
	}

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		if (Text(context, OutputParameter) is not { } outputId)
		{
			return Invalid(Strings.Actions.Output.Target());
		}

		var state = Text(context, StateParameter) ?? "toggle";
		if (state is not ("on" or "off" or "toggle"))
		{
			return Invalid(Strings.Actions.Output.State());
		}

		var cancellationToken = context.CancellationToken;
		var outputs = Require<IOutputControl>(printer);
		if ((await outputs.GetOutputsAsync(cancellationToken)).All(o => o.Id != outputId))
		{
			return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Actions.Output.UnknownOutput());
		}

		var before = await outputs.ReadOutputsAsync(cancellationToken);
		var on = state == "toggle" ? !before.GetValueOrDefault(outputId) : state == "on";
		await outputs.SetOutputAsync(outputId, on, cancellationToken);

		// Reading back confirms the switch and updates the variable at once.
		var after = await outputs.ReadOutputsAsync(cancellationToken);
		var expected = on ? "on" : "off";
		return after.TryGetValue(outputId, out var now) && now == on
			? ActionResult.Success(expected)
			: ActionResult.Accepted(Strings.Actions.Sent(), expected);
	}
}
