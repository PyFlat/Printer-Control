using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal sealed class ConnectionAction(PrinterRegistry registry) : PrinterAction(registry), IStateProviderActionDefinition
{
	public const string CommandParameter = "command";

	public override string Id => "connection";

	public override LocalizedText Name => Strings.Actions.Connection.Name();

	public override LocalizedText Description => Strings.Actions.Connection.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Choice(
			CommandParameter,
			[
				new() { Value = "connect", Label = Strings.Actions.Connection.Connect() },
				new() { Value = "disconnect", Label = Strings.Actions.Connection.Disconnect() },
				new() { Value = "toggle", Label = Strings.Actions.Connection.Toggle() },
			],
			label: Strings.Actions.Connection.Command(),
			defaultValue: "connect",
			required: true),
	];

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new("connected", Strings.States.Connected()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2f855a" } },
		new("connecting", Strings.Status.Connecting()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#b7791f" } },
		new("disconnected", Strings.Status.Disconnected()) { DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2d3748" } },
		new("unavailable", MacroDeckStrings.States.Unavailable())
		{
			DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568", LabelColor = "#cbd5e0" },
		},
	];

	protected override ActionStateSnapshot? ReadState(PrinterConnection printer, IReadOnlyDictionary<string, object?> parameters) =>
		new(States, printer.Snapshot switch
		{
			{ Online: false } or { Status: PrinterStatus.Offline } => "unavailable",
			{ Status: PrinterStatus.Disconnected or PrinterStatus.Error } => "disconnected",
			{ Status: PrinterStatus.Connecting } => "connecting",
			_ => "connected",
		});

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var snapshot = printer.Snapshot;
		var connected = snapshot.Status is not (PrinterStatus.Disconnected or PrinterStatus.Error);
		var command = Text(context, CommandParameter) switch
		{
			"toggle" => connected ? "disconnect" : "connect",
			var value => value ?? "connect",
		};

		switch (command)
		{
			case "connect":
				if (snapshot.IsPrinterConnected)
				{
					return ActionResult.Success("connected");
				}

				await Require<IPrinterLink>(printer).ConnectPrinterAsync(context.CancellationToken);
				return await ConfirmAsync(printer, s => s.IsPrinterConnected, Strings.Actions.Connection.Connecting(), context.CancellationToken, "connected");

			case "disconnect":
				if (snapshot.IsJobActive)
				{
					return ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Errors.AlreadyPrinting());
				}

				await Require<IPrinterLink>(printer).DisconnectPrinterAsync(context.CancellationToken);
				return await ConfirmAsync(printer, s => s.Status == PrinterStatus.Disconnected, Strings.Actions.Connection.Disconnecting(), context.CancellationToken, "disconnected");

			default:
				return Invalid(Strings.Actions.Connection.Command());
		}
	}
}

internal sealed class SystemCommandAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string CommandParameter = "command";

	public override string Id => "system-command";

	public override LocalizedText Name => Strings.Actions.SystemCommand.Name();

	public override LocalizedText Description => Strings.Actions.SystemCommand.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.DynamicChoice(CommandParameter, label: Strings.Actions.SystemCommand.Command(), required: true),
	];

	protected override async Task<DynamicOptionsResult> GetOptionsAsync(
		PrinterConnection printer,
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		var commands = await Require<ISystemCommands>(printer).GetSystemCommandsAsync(cancellationToken);
		return new DynamicOptionsResult
		{
			Options = [.. commands.Select(c => new ActionParameterOption { Value = c.Id, Label = c.Name })],
			CacheSeconds = 30,
		};
	}

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		if (Text(context, CommandParameter) is not { } command)
		{
			return Invalid(Strings.Actions.SystemCommand.Command());
		}

		await Require<ISystemCommands>(printer).RunSystemCommandAsync(command, context.CancellationToken);
		return ActionResult.Success();
	}
}
