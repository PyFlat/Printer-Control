using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal sealed class JobAction(PrinterRegistry registry) : PrinterAction(registry), IStateProviderActionDefinition
{
	public const string CommandParameter = "command";

	public const string Pause = "pause";
	public const string Resume = "resume";
	public const string TogglePause = "toggle-pause";
	public const string Cancel = "cancel";
	public const string Start = "start";
	public const string Restart = "restart";

	public override string Id => "job";

	public override LocalizedText Name => Strings.Actions.Job.Name();

	public override LocalizedText Description => Strings.Actions.Job.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Choice(
			CommandParameter,
			[
				new() { Value = TogglePause, Label = Strings.Actions.Job.TogglePause() },
				new() { Value = Pause, Label = Strings.Actions.Job.Pause() },
				new() { Value = Resume, Label = Strings.Actions.Job.Resume() },
				new() { Value = Cancel, Label = Strings.Actions.Job.Cancel() },
				new() { Value = Start, Label = Strings.Actions.Job.Start() },
				new() { Value = Restart, Label = Strings.Actions.Job.Restart() },
			],
			label: Strings.Actions.Job.Command(),
			defaultValue: TogglePause,
			required: true),
	];

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new("printing", Strings.Status.Printing()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2f855a" } },
		new("paused", MacroDeckStrings.States.Paused()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#b7791f" } },
		new("idle", Strings.Status.Operational()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2d3748" } },
		new("unavailable", MacroDeckStrings.States.Unavailable())
		{
			DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568", LabelColor = "#cbd5e0" },
		},
	];

	protected override ActionStateSnapshot? ReadState(PrinterConnection printer, IReadOnlyDictionary<string, object?> parameters) =>
		new(States, printer.Snapshot.Status switch
		{
			PrinterStatus.Printing or PrinterStatus.Resuming or PrinterStatus.Cancelling or PrinterStatus.Finishing => "printing",
			PrinterStatus.Paused or PrinterStatus.Pausing => "paused",
			PrinterStatus.Operational => "idle",
			_ => "unavailable",
		});

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var token = context.CancellationToken;
		var status = printer.Snapshot.Status;
		var command = Text(context, CommandParameter) ?? TogglePause;
		if (command == TogglePause)
		{
			command = status is PrinterStatus.Paused or PrinterStatus.Pausing ? Resume : Pause;
		}

		switch (command)
		{
			case Pause:
				if (status is PrinterStatus.Paused or PrinterStatus.Pausing)
				{
					return ActionResult.Success("paused");
				}

				await Require<IJobControl>(printer).RunJobCommandAsync(JobCommand.Pause, token);
				return await ConfirmAsync(printer, s => s.Status == PrinterStatus.Paused, Strings.Actions.Job.Pausing(), token, "paused");

			case Resume:
				if (status is PrinterStatus.Printing or PrinterStatus.Resuming)
				{
					return ActionResult.Success("printing");
				}

				await Require<IJobControl>(printer).RunJobCommandAsync(JobCommand.Resume, token);
				return await ConfirmAsync(printer, s => s.Status == PrinterStatus.Printing, Strings.Actions.Job.Resuming(), token, "printing");

			case Cancel:
				await Require<IJobControl>(printer).RunJobCommandAsync(JobCommand.Cancel, token);
				return await ConfirmAsync(printer, s => !s.IsJobActive, Strings.Actions.Job.Cancelling(), token, "idle");

			case Start:
				await Require<IJobControl>(printer).RunJobCommandAsync(JobCommand.Start, token);
				return await ConfirmAsync(printer, s => s.IsJobActive, Strings.Actions.Job.Starting(), token, "printing");

			case Restart:
				await Require<IJobControl>(printer).RunJobCommandAsync(JobCommand.Restart, token);
				return await ConfirmAsync(printer, s => s.Status == PrinterStatus.Printing, Strings.Actions.Job.Starting(), token, "printing");

			default:
				return Invalid(Strings.Actions.Job.Command());
		}
	}
}
