using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;
using PrinterControl.Core;

namespace PrinterControl.Variables;

// Every printer contributes one variable per field. The local id is "p<entry key>-<field>" and survives
// a printer rename; the name ("printer_<printer>_<field>") is what users type. Both suffixes are public API.
internal static class PrinterVariables
{
	internal enum Field
	{
		Status,
		State,
		Online,
		Connected,
		Printing,
		Paused,
		Progress,
		File,
		PrintTime,
		TimeLeft,
		Eta,
		EstimatedTime,
		Z,
		ToolTemp,
		ToolTarget,
		BedTemp,
		BedTarget,
		ChamberTemp,
		ChamberTarget,
		Error,
	}

	private sealed record FieldSpec(Field Field, string Suffix, VariableType Type, string? Unit = null, string? Kind = null, int? Decimals = null);

	private static readonly FieldSpec[] Specs =
	[
		new(Field.Status, "status", VariableType.Text),
		new(Field.State, "state", VariableType.Text),
		new(Field.Online, "online", VariableType.Boolean),
		new(Field.Connected, "connected", VariableType.Boolean),
		new(Field.Printing, "printing", VariableType.Boolean),
		new(Field.Paused, "paused", VariableType.Boolean),
		new(Field.Progress, "progress", VariableType.Numeric, "%", VariableSemanticKinds.Percentage, 1),
		new(Field.File, "file", VariableType.Text),
		new(Field.PrintTime, "print_time", VariableType.Numeric, "s", VariableSemanticKinds.Duration, 0),
		new(Field.TimeLeft, "time_left", VariableType.Numeric, "s", VariableSemanticKinds.Duration, 0),
		new(Field.Eta, "eta", VariableType.Text),
		new(Field.EstimatedTime, "estimated_time", VariableType.Numeric, "s", VariableSemanticKinds.Duration, 0),
		new(Field.Z, "z", VariableType.Numeric, "mm", null, 2),
		new(Field.ToolTemp, "tool_temp", VariableType.Numeric, "°C", null, 0),
		new(Field.ToolTarget, "tool_target", VariableType.Numeric, "°C", null, 0),
		new(Field.BedTemp, "bed_temp", VariableType.Numeric, "°C", null, 0),
		new(Field.BedTarget, "bed_target", VariableType.Numeric, "°C", null, 0),
		new(Field.ChamberTemp, "chamber_temp", VariableType.Numeric, "°C", null, 0),
		new(Field.ChamberTarget, "chamber_target", VariableType.Numeric, "°C", null, 0),
		new(Field.Error, "error", VariableType.Text),
	];

	public static int FieldsPerPrinter => Specs.Length;

	public static string LocalId(PrinterConfig printer, Field field) =>
		$"p{printer.Key}-{Spec(field).Suffix.Replace('_', '-')}";

	// One more per output (light, relay), keyed by the output's id: "p<key>-output-<id>".
	public static string OutputLocalId(PrinterConfig printer, string outputId) => $"p{printer.Key}-output-{outputId}";

	public static bool TryResolveOutput(string localId, out string printerKey, out string outputId)
	{
		printerKey = string.Empty;
		outputId = string.Empty;
		if (localId.Length < 42 || localId[0] != 'p' || localId[33] != '-' || !localId.AsSpan(34).StartsWith("output-"))
		{
			return false;
		}

		printerKey = localId[1..33];
		outputId = localId[41..];
		return true;
	}

	public static IReadOnlyList<(string LocalId, Field Field)> FieldsFor(PrinterConfig printer) =>
		[.. Specs.Select(spec => (LocalId(printer, spec.Field), spec.Field))];

	public static bool TryResolve(string localId, out string printerKey, out Field field)
	{
		printerKey = string.Empty;
		field = default;
		if (localId.Length < 35 || localId[0] != 'p' || localId[33] != '-')
		{
			return false;
		}

		var suffix = localId[34..].Replace('-', '_');
		foreach (var spec in Specs)
		{
			if (spec.Suffix == suffix)
			{
				printerKey = localId[1..33];
				field = spec.Field;
				return true;
			}
		}

		return false;
	}

	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

	public static IReadOnlyList<VariableDefinition> Build(
		IReadOnlyList<PrinterConfig> printers,
		Func<PrinterConfig, IReadOnlyList<PrinterOutput>>? outputsOf = null)
	{
		var definitions = new List<VariableDefinition>(printers.Count * Specs.Length);
		foreach (var (printer, slug) in Slugs(printers))
		{
			var configuration = new VariableConfiguration(printer.Key, LocalizedText.FromLiteral(printer.DisplayName));
			foreach (var spec in Specs)
			{
				definitions.Add(Define(spec, $"printer_{slug}_{spec.Suffix}", printer.DisplayName) with
				{
					Id = LocalId(printer, spec.Field),
					Configuration = configuration,
				});
			}

			var ids = new HashSet<string>(StringComparer.Ordinal);
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var output in outputsOf?.Invoke(printer) ?? [])
			{
				if (!ids.Add(output.Id))
				{
					continue;
				}

				var baseName = $"printer_{slug}_output_{Slug.From(output.Name, "output")}";
				var name = baseName;
				for (var n = 2; !names.Add(name); n++)
				{
					name = string.Create(CultureInfo.InvariantCulture, $"{baseName}_{n}");
				}

				definitions.Add(new VariableDefinition
				{
					Id = OutputLocalId(printer, output.Id),
					Configuration = configuration,
					Name = name,
					Type = VariableType.Boolean,
					Materialization = VariableMaterialization.Eager,
					RefreshInterval = RefreshInterval,
					DisplayName = Strings.Variables.Output(printer.DisplayName, output.Name),
				});
			}
		}

		return definitions;
	}

	public static VariableReading ReadOutput(PrinterConnection? printer, string outputId) =>
		printer is not null && printer.Settings.Outputs.Any(o => o.Id == outputId)
			? VariableReading.Of(printer.OutputStates.GetValueOrDefault(outputId))
			: VariableReading.Unavailable;

	// What the integration page shows before any printer is set up.
	public static IReadOnlyList<VariableDefinition> Templates()
	{
		var printer = VariableNameTemplate.Placeholder("printer");
		return [.. Specs.Select(spec => Define(spec, $"printer_{printer}_{spec.Suffix}", printer))];
	}

	private static VariableDefinition Define(FieldSpec spec, string name, string printer) => new()
	{
		Name = name,
		Type = spec.Type,
		Materialization = VariableMaterialization.Eager,
		RefreshInterval = RefreshInterval,
		DisplayName = DisplayName(spec.Field, printer),
		Unit = spec.Unit,
		SemanticKind = spec.Kind,
		DecimalPlaces = spec.Decimals,
	};

	// Two printers with the same name get distinct variable names, numbered in configuration order.
	internal static IEnumerable<(PrinterConfig Printer, string Slug)> Slugs(IReadOnlyList<PrinterConfig> printers)
	{
		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (var printer in printers)
		{
			var baseSlug = Slug.From(printer.DisplayName, "printer");
			var slug = baseSlug;
			for (var n = 2; !used.Add(slug); n++)
			{
				slug = string.Create(CultureInfo.InvariantCulture, $"{baseSlug}_{n}");
			}

			yield return (printer, slug);
		}
	}

	public static VariableReading Read(Field field, PrinterSnapshot? snapshot)
	{
		if (snapshot is null)
		{
			return field is Field.Online or Field.Connected or Field.Printing or Field.Paused
				? VariableReading.Of(false)
				: VariableReading.Unavailable;
		}

		return field switch
		{
			Field.Status => VariableReading.Of(PrinterStatusText.Id(snapshot.Status)),
			Field.State => VariableReading.Of(snapshot.Online ? snapshot.StateText : "Offline"),
			Field.Online => VariableReading.Of(snapshot.Online),
			Field.Connected => VariableReading.Of(snapshot.IsPrinterConnected),
			Field.Printing => VariableReading.Of(snapshot.IsJobActive),
			Field.Paused => VariableReading.Of(snapshot.Status is PrinterStatus.Paused or PrinterStatus.Pausing),
			Field.Progress => snapshot.IsJobActive && snapshot.Completion is { } completion
				? VariableReading.Of(Math.Round(completion, 1), 0, 100, 0.1)
				: VariableReading.Of(0.0, 0, 100, 0.1),
			Field.File => VariableReading.Of(snapshot.FileName ?? string.Empty),
			Field.PrintTime => Seconds(snapshot.IsJobActive ? snapshot.PrintTime : null),
			Field.TimeLeft => Seconds(snapshot.IsJobActive ? snapshot.PrintTimeLeft : null),
			Field.Eta => VariableReading.Of(Eta(snapshot, DateTimeOffset.Now)),
			Field.EstimatedTime => snapshot.EstimatedPrintTime is { } estimate ? VariableReading.Of(Math.Round(estimate)) : VariableReading.Unavailable,
			Field.Z => snapshot.CurrentZ is { } z ? VariableReading.Of(z) : VariableReading.Unavailable,
			Field.ToolTemp => Degrees(snapshot.Heater(HeaterIds.Extruder)?.Actual),
			Field.ToolTarget => Degrees(snapshot.Heater(HeaterIds.Extruder)?.Target),
			Field.BedTemp => Degrees(snapshot.Heater(HeaterIds.Bed)?.Actual),
			Field.BedTarget => Degrees(snapshot.Heater(HeaterIds.Bed)?.Target),
			Field.ChamberTemp => Degrees(snapshot.Heater(HeaterIds.Chamber)?.Actual),
			Field.ChamberTarget => Degrees(snapshot.Heater(HeaterIds.Chamber)?.Target),
			Field.Error => VariableReading.Of(snapshot.Error ?? string.Empty),
			_ => VariableReading.Unavailable,
		};
	}

	internal static string Eta(PrinterSnapshot snapshot, DateTimeOffset now) =>
		snapshot.IsJobActive && snapshot.PrintTimeLeft is { } left
			? now.AddSeconds(left).ToString("HH:mm", CultureInfo.InvariantCulture)
			: string.Empty;

	private static VariableReading Seconds(int? value) =>
		value is { } seconds ? VariableReading.Of((double)seconds) : VariableReading.Unavailable;

	// Whole degrees: tenths are sensor noise, and integers chart cleanly in the built-in history graph.
	private static VariableReading Degrees(double? value) =>
		value is { } degrees ? VariableReading.Of((int)Math.Round(degrees, MidpointRounding.AwayFromZero)) : VariableReading.Unavailable;

	private static FieldSpec Spec(Field field) => Specs.First(spec => spec.Field == field);

	private static LocalizedText DisplayName(Field field, string printer) => field switch
	{
		Field.Status => Strings.Variables.Status(printer),
		Field.State => Strings.Variables.State(printer),
		Field.Online => Strings.Variables.Online(printer),
		Field.Connected => Strings.Variables.Connected(printer),
		Field.Printing => Strings.Variables.Printing(printer),
		Field.Paused => Strings.Variables.Paused(printer),
		Field.Progress => Strings.Variables.Progress(printer),
		Field.File => Strings.Variables.File(printer),
		Field.PrintTime => Strings.Variables.PrintTime(printer),
		Field.TimeLeft => Strings.Variables.TimeLeft(printer),
		Field.Eta => Strings.Variables.Eta(printer),
		Field.EstimatedTime => Strings.Variables.EstimatedTime(printer),
		Field.Z => Strings.Variables.Z(printer),
		Field.ToolTemp => Strings.Variables.ToolTemp(printer),
		Field.ToolTarget => Strings.Variables.ToolTarget(printer),
		Field.BedTemp => Strings.Variables.BedTemp(printer),
		Field.BedTarget => Strings.Variables.BedTarget(printer),
		Field.ChamberTemp => Strings.Variables.ChamberTemp(printer),
		Field.ChamberTarget => Strings.Variables.ChamberTarget(printer),
		Field.Error => Strings.Variables.Error(printer),
		_ => LocalizedText.FromLiteral(field.ToString()),
	};
}
