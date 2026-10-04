using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

// Servers only queue these and never report when they ran, so they answer Accepted, not Success.
internal sealed class HomeAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string AxesParameter = "axes";

	public override string Id => "home";

	public override LocalizedText Name => Strings.Actions.Home.Name();

	public override LocalizedText Description => Strings.Actions.Home.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.MultiSelect(
			AxesParameter,
			[
				new() { Value = "x", Label = "X" },
				new() { Value = "y", Label = "Y" },
				new() { Value = "z", Label = "Z" },
			],
			label: Strings.Actions.Home.Axes(),
			description: Strings.Actions.Home.AxesDescription()),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var axes = List(context, AxesParameter).Where(a => a is "x" or "y" or "z").Distinct().ToArray();
		await Require<IMotionControl>(printer).HomeAsync(axes.Length == 0 ? ["x", "y", "z"] : axes, context.CancellationToken);
		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}

internal sealed class JogAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string AxisParameter = "axis";
	public const string DistanceParameter = "distance";
	public const string SpeedParameter = "speed";

	public override string Id => "jog";

	public override LocalizedText Name => Strings.Actions.Jog.Name();

	public override LocalizedText Description => Strings.Actions.Jog.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Choice(
			AxisParameter,
			[
				new() { Value = "x", Label = "X" },
				new() { Value = "y", Label = "Y" },
				new() { Value = "z", Label = "Z" },
			],
			label: Strings.Actions.Jog.Axis(),
			defaultValue: "z",
			required: true),
		ActionParameter.Number(
			DistanceParameter,
			label: Strings.Actions.Jog.Distance(),
			description: Strings.Actions.Jog.DistanceDescription(),
			min: -300,
			max: 300,
			step: 0.1,
			defaultValue: 10,
			required: true),
		ActionParameter.Number(
			SpeedParameter,
			label: Strings.Actions.Jog.Speed(),
			description: Strings.Actions.Jog.SpeedDescription(),
			min: 1,
			max: 20000,
			step: 1),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		if (Number(context, DistanceParameter) is not { } distance || distance == 0 || Math.Abs(distance) > 300)
		{
			return Invalid(Strings.Actions.Jog.Distance());
		}

		var axis = Text(context, AxisParameter);
		if (axis is not ("x" or "y" or "z"))
		{
			return Invalid(Strings.Actions.Jog.Axis());
		}

		await Require<IMotionControl>(printer).JogAsync(
			axis == "x" ? distance : 0,
			axis == "y" ? distance : 0,
			axis == "z" ? distance : 0,
			Number(context, SpeedParameter) is { } speed and > 0 ? speed : null,
			context.CancellationToken);
		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}

internal sealed class ExtrudeAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string AmountParameter = "amount";

	public override string Id => "extrude";

	public override LocalizedText Name => Strings.Actions.Extrude.Name();

	public override LocalizedText Description => Strings.Actions.Extrude.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Number(
			AmountParameter,
			label: Strings.Actions.Extrude.Amount(),
			description: Strings.Actions.Extrude.AmountDescription(),
			min: -200,
			max: 200,
			step: 0.1,
			defaultValue: 5,
			required: true),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		if (Number(context, AmountParameter) is not { } amount || amount == 0 || Math.Abs(amount) > 200)
		{
			return Invalid(Strings.Actions.Extrude.Amount());
		}

		await Require<IMotionControl>(printer).ExtrudeAsync(amount, context.CancellationToken);
		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}

internal sealed class FanAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string SpeedParameter = "speed";

	public override string Id => "fan";

	public override LocalizedText Name => Strings.Actions.Fan.Name();

	public override LocalizedText Description => Strings.Actions.Fan.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Slider(SpeedParameter, 0, 100, label: Strings.Actions.Fan.Speed(), step: 5, defaultValue: 100),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var percent = Math.Clamp(Number(context, SpeedParameter) ?? 100, 0, 100);
		var command = percent <= 0
			? "M107"
			: string.Create(CultureInfo.InvariantCulture, $"M106 S{(int)Math.Round(percent * 255 / 100)}");
		await Require<IGcodeControl>(printer).SendGcodeAsync([command], context.CancellationToken);
		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}

internal sealed class RateAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string KindParameter = "kind";
	public const string PercentParameter = "percent";

	public override string Id => "rate";

	public override LocalizedText Name => Strings.Actions.Rate.Name();

	public override LocalizedText Description => Strings.Actions.Rate.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Choice(
			KindParameter,
			[
				new() { Value = "feed", Label = Strings.Actions.Rate.Feed() },
				new() { Value = "flow", Label = Strings.Actions.Rate.Flow() },
			],
			label: Strings.Actions.Rate.Kind(),
			defaultValue: "feed",
			required: true),
		ActionParameter.Slider(PercentParameter, 50, 200, label: Strings.Actions.Rate.Percent(), step: 5, defaultValue: 100),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var percent = (int)Math.Round(Math.Clamp(Number(context, PercentParameter) ?? 100, 50, 200));
		switch (Text(context, KindParameter))
		{
			case "flow":
				await Require<IMotionControl>(printer).SetFlowrateAsync(percent, context.CancellationToken);
				break;
			case "feed" or null:
				await Require<IMotionControl>(printer).SetFeedrateAsync(percent, context.CancellationToken);
				break;
			default:
				return Invalid(Strings.Actions.Rate.Kind());
		}

		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}

internal sealed class GcodeAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string CommandsParameter = "commands";

	public override string Id => "send-gcode";

	public override LocalizedText Name => Strings.Actions.Gcode.Name();

	public override LocalizedText Description => Strings.Actions.Gcode.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.MultilineText(
			CommandsParameter,
			label: Strings.Actions.Gcode.Commands(),
			description: Strings.Actions.Gcode.CommandsDescription(),
			placeholder: "M117 Hello from Macro Deck",
			required: true),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var commands = (Text(context, CommandsParameter) ?? string.Empty)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(line => !line.StartsWith(';'))
			.ToArray();
		if (commands.Length == 0)
		{
			return Invalid(Strings.Actions.Gcode.Commands());
		}

		await Require<IGcodeControl>(printer).SendGcodeAsync(commands, context.CancellationToken);
		return ActionResult.Accepted(Strings.Actions.Sent());
	}
}
