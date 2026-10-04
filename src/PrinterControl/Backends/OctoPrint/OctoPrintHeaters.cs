using PrinterControl.Core;

namespace PrinterControl.Backends.OctoPrint;

// OctoPrint numbers its hotends "tool0", "tool1", ...; bed and chamber already match HeaterIds.
internal static class OctoPrintHeaters
{
	public static string ToHeaterId(string name) =>
		name.StartsWith("tool", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), out var index) && index >= 0
			? index == 0 ? HeaterIds.Extruder : $"{HeaterIds.Extruder}{index}"
			: name;

	public static string FromHeaterId(string heater) =>
		heater == HeaterIds.Extruder ? "tool0"
		: heater.StartsWith(HeaterIds.Extruder, StringComparison.Ordinal)
			&& int.TryParse(heater.AsSpan(HeaterIds.Extruder.Length), out var index) && index > 0
			? $"tool{index}"
			: heater;
}
