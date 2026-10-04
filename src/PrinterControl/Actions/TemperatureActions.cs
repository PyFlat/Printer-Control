using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal sealed class SetTemperatureAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string HeaterParameter = "heater";
	public const string TargetParameter = "target";

	public override string Id => "set-temperature";

	public override LocalizedText Name => Strings.Actions.SetTemperature.Name();

	public override LocalizedText Description => Strings.Actions.SetTemperature.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.Choice(
			HeaterParameter,
			[
				new() { Value = HeaterIds.Extruder, Label = Strings.Heaters.Tool() },
				new() { Value = HeaterIds.Bed, Label = Strings.Heaters.Bed() },
				new() { Value = HeaterIds.Chamber, Label = Strings.Heaters.Chamber() },
			],
			label: Strings.Actions.SetTemperature.Heater(),
			defaultValue: HeaterIds.Extruder,
			required: true),
		ActionParameter.Number(
			TargetParameter,
			label: Strings.Actions.SetTemperature.Target(),
			description: Strings.Actions.SetTemperature.TargetDescription(),
			min: 0,
			max: 400,
			step: 1,
			defaultValue: 0,
			required: true),
	];

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var heater = Text(context, HeaterParameter) ?? HeaterIds.Extruder;
		if (heater is not (HeaterIds.Extruder or HeaterIds.Bed or HeaterIds.Chamber))
		{
			return Invalid(Strings.Actions.SetTemperature.Heater());
		}

		if (Number(context, TargetParameter) is not { } target || target is < 0 or > 400)
		{
			return Invalid(Strings.Actions.SetTemperature.Target());
		}

		return await Temperatures.SetAsync(printer, [(heater, target)], context.CancellationToken);
	}
}

internal sealed class PreheatAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string ProfileParameter = "profile";
	public const string CoolDown = "off";

	public override string Id => "preheat";

	public override LocalizedText Name => Strings.Actions.Preheat.Name();

	public override LocalizedText Description => Strings.Actions.Preheat.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.DynamicChoice(ProfileParameter, label: Strings.Actions.Preheat.Profile(), required: true),
	];

	protected override Task<DynamicOptionsResult> GetOptionsAsync(
		PrinterConnection printer,
		DynamicOptionsContext context,
		CancellationToken cancellationToken) =>
		Task.FromResult(new DynamicOptionsResult
		{
			Options =
			[
				.. printer.Settings.TemperatureProfiles.Select(p => new ActionParameterOption { Value = p.Name, Label = p.Name }),
				new ActionParameterOption { Value = CoolDown, Label = Strings.Actions.Preheat.CoolDown() },
			],
		});

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		var name = Text(context, ProfileParameter);
		if (name is null)
		{
			return Invalid(Strings.Actions.Preheat.Profile());
		}

		var hasChamber = printer.Snapshot.Heater(HeaterIds.Chamber) is not null;
		if (name == CoolDown)
		{
			List<(string, double)> off = [(HeaterIds.Extruder, 0), (HeaterIds.Bed, 0)];
			if (hasChamber)
			{
				off.Add((HeaterIds.Chamber, 0));
			}

			return await Temperatures.SetAsync(printer, off, context.CancellationToken);
		}

		var profile = printer.Settings.TemperatureProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
		if (profile is null)
		{
			return ActionResult.Failed(ActionErrorCodes.NotFound, Strings.Actions.Preheat.UnknownProfile(name));
		}

		var targets = new List<(string, double)>();
		if (profile.Extruder is { } extruder)
		{
			targets.Add((HeaterIds.Extruder, extruder));
		}

		if (profile.Bed is { } bed)
		{
			targets.Add((HeaterIds.Bed, bed));
		}

		if (hasChamber && profile.Chamber is { } chamber)
		{
			targets.Add((HeaterIds.Chamber, chamber));
		}

		return await Temperatures.SetAsync(printer, targets, context.CancellationToken);
	}
}

internal static class Temperatures
{
	// The pushed state shows a target only once the printer took it.
	public static async Task<ActionResult> SetAsync(
		PrinterConnection printer,
		IReadOnlyList<(string Heater, double Target)> targets,
		CancellationToken cancellationToken)
	{
		var heaters = PrinterAction.Require<ITemperatureControl>(printer);
		foreach (var (heater, target) in targets)
		{
			await heaters.SetTemperatureAsync(heater, target, cancellationToken);
		}

		return await ConfirmTargets(printer, targets, cancellationToken);
	}

	private static Task<ActionResult> ConfirmTargets(
		PrinterConnection printer,
		IReadOnlyList<(string Heater, double Target)> targets,
		CancellationToken cancellationToken) =>
		PrinterAction.ConfirmAsync(
			printer,
			snapshot => targets.All(t => snapshot.Heater(t.Heater)?.Target is { } set && Math.Abs(set - t.Target) < 0.5),
			Strings.Actions.SetTemperature.Pending(),
			cancellationToken);
}
