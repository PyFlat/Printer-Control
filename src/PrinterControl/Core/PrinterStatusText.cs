using MacroDeck.Localization;

namespace PrinterControl.Core;

internal static class PrinterStatusText
{
	// Written to the status variable and event payloads, so automations can compare against them.
	public static string Id(PrinterStatus status) => status switch
	{
		PrinterStatus.Offline => "offline",
		PrinterStatus.Disconnected => "disconnected",
		PrinterStatus.Connecting => "connecting",
		PrinterStatus.Operational => "operational",
		PrinterStatus.Printing => "printing",
		PrinterStatus.Pausing => "pausing",
		PrinterStatus.Paused => "paused",
		PrinterStatus.Resuming => "resuming",
		PrinterStatus.Cancelling => "cancelling",
		PrinterStatus.Finishing => "finishing",
		PrinterStatus.Error => "error",
		_ => "unknown",
	};

	public static LocalizedText Label(PrinterStatus status) => status switch
	{
		PrinterStatus.Offline => Strings.Status.Offline(),
		PrinterStatus.Disconnected => Strings.Status.Disconnected(),
		PrinterStatus.Connecting => Strings.Status.Connecting(),
		PrinterStatus.Operational => Strings.Status.Operational(),
		PrinterStatus.Printing => Strings.Status.Printing(),
		PrinterStatus.Pausing => Strings.Status.Pausing(),
		PrinterStatus.Paused => Strings.Status.Paused(),
		PrinterStatus.Resuming => Strings.Status.Resuming(),
		PrinterStatus.Cancelling => Strings.Status.Cancelling(),
		PrinterStatus.Finishing => Strings.Status.Finishing(),
		PrinterStatus.Error => Strings.Status.Error(),
		_ => Strings.Status.Offline(),
	};
}
