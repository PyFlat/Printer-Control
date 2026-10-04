using System.Text.Json;
using PrinterControl.Core;

namespace PrinterControl.Ui;

// A widget's stored data. Printer holds a PrinterConfig.Key, or "first" (or nothing) for the first
// configured printer.
internal sealed record WidgetOptions(
	string Printer,
	bool ShowFile,
	bool ShowTemperatures,
	bool ShowEta,
	bool Overlay,
	string Fit)
{
	public const string FirstPrinter = "first";
	public const string FitContain = "contain";
	public const string FitCover = "cover";

	public bool UsesFirstPrinter => string.IsNullOrEmpty(Printer) || Printer == FirstPrinter;

	public static WidgetOptions Default { get; } = new(string.Empty, true, true, true, true, FitContain);

	public static WidgetOptions Parse(JsonElement? data)
	{
		if (data is not { ValueKind: JsonValueKind.Object } obj)
		{
			return Default;
		}

		bool Flag(string key, bool fallback) =>
			obj.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
				? value.GetBoolean()
				: fallback;

		string Text(string key, string fallback) =>
			obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString() ?? fallback
				: fallback;

		var fit = Text("fit", FitContain);
		return new WidgetOptions(
			Text("printer", string.Empty),
			Flag("showFile", Default.ShowFile),
			Flag("showTemperatures", Default.ShowTemperatures),
			Flag("showEta", Default.ShowEta),
			Flag("overlay", Default.Overlay),
			fit == FitCover ? FitCover : FitContain);
	}

	public PrinterConnection? Resolve(PrinterRegistry registry) =>
		UsesFirstPrinter
			? (registry.Printers.Count > 0 ? registry.Printers[0] : null)
			: registry.Find(Printer);
}

internal sealed record WidgetModel(
	string PrinterName,
	PrinterSnapshot Snapshot,
	WidgetOptions Options,
	bool HasPrinter,
	string PrinterKey = "",
	double WebcamRotation = 0)
{
	// Rotation is clockwise and flipping both axes is a half turn; a single-axis flip cannot be drawn as a
	// rotation and is left out.
	public static double RotationFor(WebcamSettings webcam) =>
		((webcam.Rotation % 360) + 360 + (webcam.FlipH && webcam.FlipV ? 180 : 0)) % 360;

	public static WidgetModel NoPrinter(WidgetOptions options) => new(string.Empty, PrinterSnapshot.Offline, options, false);
}
