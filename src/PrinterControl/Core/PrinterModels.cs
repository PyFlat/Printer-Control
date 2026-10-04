using System.Text.Json;
using MacroDeck.Localization;

namespace PrinterControl.Core;

internal sealed record TemperatureProfile(string Name, double? Extruder, double? Bed, double? Chamber);

// Rotation in clockwise degrees.
internal sealed record WebcamSettings(
	bool Enabled,
	string? StreamUrl,
	string? SnapshotUrl,
	bool FlipH,
	bool FlipV,
	int Rotation,
	string? Ratio)
{
	public static WebcamSettings None { get; } = new(false, null, null, false, false, 0, null);
}

// A light, relay or power switch the backend can turn on and off. Id is a local id fragment
// ([a-z0-9-]) that stays put when the output is renamed or reordered; variables and actions store it.
internal sealed record PrinterOutput(string Id, string Name);

// What the server tells about itself, as opposed to the live state in PrinterSnapshot.
internal sealed record PrinterSettings(
	string? InstanceName,
	WebcamSettings Webcam,
	IReadOnlyList<TemperatureProfile> TemperatureProfiles,
	IReadOnlyList<PrinterOutput> Outputs)
{
	public static PrinterSettings Empty { get; } = new(null, WebcamSettings.None, [], []);

	public bool SameAs(PrinterSettings other) =>
		InstanceName == other.InstanceName
		&& Webcam == other.Webcam
		&& TemperatureProfiles.SequenceEqual(other.TemperatureProfiles)
		&& Outputs.SequenceEqual(other.Outputs);
}

// Id is opaque to everything but the backend that listed the file; actions store it.
internal sealed record PrintFile(string Id, string Display, long? Size, DateTimeOffset? Date);

// Id is opaque to everything but the backend that listed the command; actions store it. Name is the
// server's own text or, for commands the server does not name, the backend's.
internal sealed record SystemCommand(string Id, LocalizedText Name, bool NeedsConfirmation);

internal enum JobCommand
{
	Pause,
	Resume,
	Cancel,
	Start,
	Restart,
}

internal enum PrinterEventKind
{
	PrintStarted,
	PrintDone,
	PrintFailed,
	PrintCancelled,
	PrintPaused,
	PrintResumed,
	Connected,
	Disconnected,
	Error,
}

// An event as the backend reported it (Type, Payload), plus what it means when the backend could map it.
internal sealed record PrinterEvent(string Type, JsonElement Payload)
{
	public PrinterEventKind? Kind { get; init; }

	public string? File { get; init; }

	public double? Duration { get; init; }

	public string? Reason { get; init; }
}
