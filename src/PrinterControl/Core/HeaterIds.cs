namespace PrinterControl.Core;

// Heater names in snapshots and ITemperatureControl, stored by actions. A backend maps its own (OctoPrint's
// "tool0", Klipper's "heater_bed") onto these; a second extruder is "extruder1".
internal static class HeaterIds
{
	public const string Extruder = "extruder";
	public const string Bed = "bed";
	public const string Chamber = "chamber";
}
