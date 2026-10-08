using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

// Klipper's heater objects: "extruder", "extruder1", "heater_bed", and a chamber as
// "heater_generic chamber" (settable) or "temperature_sensor chamber" (read only).
internal static class MoonrakerHeaters
{
	public static string? ToHeaterId(string printerObject) => printerObject switch
	{
		"extruder" => HeaterIds.Extruder,
		"heater_bed" => HeaterIds.Bed,
		"heater_generic chamber" or "temperature_sensor chamber" => HeaterIds.Chamber,
		_ when printerObject.StartsWith("extruder", StringComparison.Ordinal)
			&& int.TryParse(printerObject.AsSpan("extruder".Length), out var index) && index > 0 => printerObject,
		_ => null,
	};

	// The HEATER name SET_HEATER_TEMPERATURE takes, or null when the printer has no such heater to set.
	public static string? ToKlipperHeater(string heater, IReadOnlyCollection<string> printerObjects) => heater switch
	{
		HeaterIds.Bed => printerObjects.Contains("heater_bed") ? "heater_bed" : null,
		HeaterIds.Chamber => printerObjects.Contains("heater_generic chamber") ? "chamber" : null,
		_ => printerObjects.Contains(heater) && ToHeaterId(heater) is not null ? heater : null,
	};
}
