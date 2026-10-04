namespace PrinterControl.Core;

// The transitional states (Connecting, Pausing, Resuming, Cancelling, Finishing) are optional: a server
// that does not report them goes straight to the next steady one.
internal enum PrinterStatus
{
	Offline,
	Disconnected,
	Connecting,
	Operational,
	Printing,
	Pausing,
	Paused,
	Resuming,
	Cancelling,
	Finishing,
	Error,
}

internal sealed record Temperature(double? Actual, double? Target);

internal sealed record PrinterSnapshot
{
	public static PrinterSnapshot Offline { get; } = new();

	// The server itself answers; the printer behind it may still be disconnected.
	public bool Online { get; init; }

	public PrinterStatus Status { get; init; } = PrinterStatus.Offline;

	public string StateText { get; init; } = string.Empty;

	public string? Error { get; init; }

	public string? FileName { get; init; }

	// The file being printed, in the id format of the backend's IFileControl.
	public string? FileId { get; init; }

	public double? Completion { get; init; }

	public int? PrintTime { get; init; }

	public int? PrintTimeLeft { get; init; }

	public double? EstimatedPrintTime { get; init; }

	public double? CurrentZ { get; init; }

	public IReadOnlyDictionary<string, Temperature> Temperatures { get; init; } =
		new Dictionary<string, Temperature>(StringComparer.Ordinal);

	public bool IsJobActive => Status is PrinterStatus.Printing or PrinterStatus.Pausing or PrinterStatus.Paused
		or PrinterStatus.Resuming or PrinterStatus.Cancelling or PrinterStatus.Finishing;

	public bool IsPrinterConnected => Online && Status is not (PrinterStatus.Offline or PrinterStatus.Disconnected
		or PrinterStatus.Connecting or PrinterStatus.Error);

	public Temperature? Heater(string name) => Temperatures.TryGetValue(name, out var value) ? value : null;
}
