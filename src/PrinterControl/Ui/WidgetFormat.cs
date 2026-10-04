using System.Globalization;
using PrinterControl.Core;

namespace PrinterControl.Ui;

internal readonly record struct Accent(string Color, string Light);

internal static class WidgetFormat
{
	private static readonly Accent Blue = new("#3b9eff", "#7cc4ff");
	private static readonly Accent Amber = new("#f5a524", "#fcd34d");
	private static readonly Accent Red = new("#f04848", "#fca5a5");
	private static readonly Accent Green = new("#2fd27a", "#86efac");
	private static readonly Accent Violet = new("#a78bfa", "#ddd6fe");
	private static readonly Accent Grey = new("#6b7280", "#9ca3af");

	public const string Cold = "#7c8a9e";
	private const string Warm = "#e8a33c";
	private const string Heating = "#ff9f1c";
	private const string AtTemperature = "#ff5a36";

	// A heater within this many degrees of its target counts as at temperature.
	private const double TargetTolerance = 3;

	// Where an unset heater's arc tops out, so a cold one still draws a sliver.
	private const double HotendScale = 300;
	private const double BedScale = 120;

	public static Accent StatusAccent(PrinterStatus status) => status switch
	{
		PrinterStatus.Printing or PrinterStatus.Resuming or PrinterStatus.Finishing => Blue,
		PrinterStatus.Paused or PrinterStatus.Pausing or PrinterStatus.Cancelling => Amber,
		PrinterStatus.Error => Red,
		PrinterStatus.Operational => Green,
		PrinterStatus.Connecting => Violet,
		_ => Grey,
	};

	public static string StatusColor(PrinterStatus status) => StatusAccent(status).Color;

	public static bool IsLit(PrinterStatus status) => StatusAccent(status) != Grey;

	public static string JobGlyph(PrinterStatus status) => status switch
	{
		PrinterStatus.Paused or PrinterStatus.Pausing => Glyphs.Pause,
		PrinterStatus.Cancelling => Glyphs.Stop,
		_ => Glyphs.Nozzle,
	};

	public static string HeaterColor(Temperature heater) => heater switch
	{
		{ Target: > 0, Actual: { } actual, Target: { } target } when actual >= target - TargetTolerance => AtTemperature,
		{ Target: > 0 } => Heating,
		{ Actual: >= 50 } => Warm,
		_ => Cold,
	};

	public static double HeaterLevel(string heaterId, Temperature heater)
	{
		var scale = heater.Target is { } target and > 0 ? target : heaterId == HeaterIds.Bed ? BedScale : HotendScale;
		return Math.Clamp((heater.Actual ?? 0) / scale, 0, 1);
	}

	public static bool IsHeating(Temperature heater) =>
		heater is { Target: > 0, Actual: { } actual, Target: { } target } && actual < target - TargetTolerance;

	public static string Percent(double? completion) =>
		completion is { } value ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(value):0}%") : "-";

	public static double Level(double? completion) => Math.Clamp((completion ?? 0) / 100, 0, 1);

	// "2:05h" for two hours five minutes, "12m" below an hour.
	public static string Duration(int? seconds)
	{
		if (seconds is not { } total || total < 0)
		{
			return "-";
		}

		var span = TimeSpan.FromSeconds(total);
		return span.TotalHours >= 1
			? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:D2}h")
			: string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m");
	}

	public static string ClockTime(DateTimeOffset now, int secondsFromNow) =>
		now.AddSeconds(secondsFromNow).ToString("HH:mm", CultureInfo.InvariantCulture);

	public static string Degrees(double? value) =>
		value is { } degrees ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(degrees):0}°") : "-";

	public static string Heater(Temperature? heater) =>
		heater is null ? "-"
		: heater.Target is > 0 ? $"{Degrees(heater.Actual)}/{Degrees(heater.Target)}"
		: Degrees(heater.Actual);

	public static string Title(WidgetModel model) => model.Snapshot.IsJobActive && model.Options.ShowFile && model.Snapshot.FileName is { } file
		? StripExtension(file)
		: model.PrinterName;

	private static string StripExtension(string file) =>
		file.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) ? file[..^6]
		: file.EndsWith(".bgcode", StringComparison.OrdinalIgnoreCase) ? file[..^7]
		: file;
}
