using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Previews;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;

namespace PrinterControl.Ui;

// Fixed models for the widget picker's sample card and the Developer Tools previews. Never read live.
internal static class WidgetSamples
{
	private const int CornerRadius = 16;

	public static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

	public static WidgetModel Printing { get; } = new(
		"Prusa MK3S",
		new PrinterSnapshot
		{
			Online = true,
			Status = PrinterStatus.Printing,
			StateText = "Printing",
			FileName = "benchy_0.2mm_PLA.gcode",
			Completion = 63.4,
			PrintTime = 2710,
			PrintTimeLeft = 1580,
			CurrentZ = 30.2,
			Temperatures = new Dictionary<string, Temperature>
			{
				[HeaterIds.Extruder] = new(214.8, 215),
				[HeaterIds.Bed] = new(59.9, 60),
			},
		},
		WidgetOptions.Default,
		true);

	public static WidgetModel Paused { get; } = Printing with
	{
		Snapshot = Printing.Snapshot with { Status = PrinterStatus.Paused, StateText = "Paused" },
	};

	public static WidgetModel Idle { get; } = Printing with
	{
		Snapshot = new PrinterSnapshot
		{
			Online = true,
			Status = PrinterStatus.Operational,
			StateText = "Operational",
			Temperatures = new Dictionary<string, Temperature>
			{
				[HeaterIds.Extruder] = new(24.1, 0),
				[HeaterIds.Bed] = new(23.8, 0),
			},
		},
	};

	public static WidgetModel Heating { get; } = Idle with
	{
		Snapshot = Idle.Snapshot with
		{
			Temperatures = new Dictionary<string, Temperature>
			{
				[HeaterIds.Extruder] = new(142.3, 215),
				[HeaterIds.Bed] = new(51.0, 60),
			},
		},
	};

	public static WidgetModel Offline { get; } = Idle with { Snapshot = PrinterSnapshot.Offline };

	public static WidgetModel Error { get; } = Idle with
	{
		Snapshot = Idle.Snapshot with { Status = PrinterStatus.Error, StateText = "Error", Error = "Thermal runaway" },
	};

	[UiPreview("Status - printing", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusPrinting() => Status(Printing);

	[UiPreview("Status - paused", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusPaused() => Status(Paused);

	[UiPreview("Status - idle", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusIdle() => Status(Idle);

	[UiPreview("Status - offline", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusOffline() => Status(Offline);

	[UiPreview("Status - error", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusError() => Status(Error);

	[UiPreview("Status - no printer set up", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusNoPrinter() => Status(WidgetModel.NoPrinter(WidgetOptions.Default));

	[UiPreview("Status - heating up", View = nameof(StatusWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement StatusHeating() => Status(Heating);

#if VIDEO_STREAMS
	[UiPreview("Webcam - printing", View = nameof(WebcamWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement WebcamPrinting() => Webcam(Printing);

	[UiPreview("Webcam - paused", View = nameof(WebcamWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement WebcamPaused() => Webcam(Paused);

	[UiPreview("Webcam - idle", View = nameof(WebcamWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement WebcamIdle() => Webcam(Idle);

	[UiPreview("Webcam - no printer set up", View = nameof(WebcamWidgetView), Profile = UiPreviewProfiles.Widget)]
	public static UiElement WebcamNoPrinter() =>
		WebcamWidgetView.Build(new UiState<WidgetModel>(WidgetModel.NoPrinter(WidgetOptions.Default)), CornerRadius, string.Empty);

	private static UiLayer Webcam(WidgetModel model) => WebcamWidgetView.Sample(new UiState<WidgetModel>(model), CornerRadius);
#endif

	private static UiElement Status(WidgetModel model) =>
		StatusWidgetView.Build(new UiState<WidgetModel>(model), CornerRadius, () => Now);
}
