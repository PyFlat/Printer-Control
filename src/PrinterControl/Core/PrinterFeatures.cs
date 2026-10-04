using System.Globalization;

namespace PrinterControl.Core;

// A backend's connection implements the interfaces its server supports; an action asks for the one it
// needs (PrinterAction.Require) and is unavailable on a printer without it.

internal interface IJobControl
{
	// May throw PrinterFailure.NotSupported for a single command the server lacks (restart, say).
	Task RunJobCommandAsync(JobCommand command, CancellationToken cancellationToken);
}

internal interface IFileControl
{
	Task<IReadOnlyList<PrintFile>> GetFilesAsync(CancellationToken cancellationToken);

	Task SelectFileAsync(string fileId, bool print, CancellationToken cancellationToken);
}

internal interface ITemperatureControl
{
	// A HeaterIds name, as in the snapshot's temperatures.
	Task SetTemperatureAsync(string heater, double target, CancellationToken cancellationToken);
}

internal interface IGcodeControl
{
	Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken);
}

// Plain G-code by default, which every firmware understands; a server with its own calls for these
// overrides them.
internal interface IMotionControl : IGcodeControl
{
	Task HomeAsync(IReadOnlyList<string> axes, CancellationToken cancellationToken) =>
		SendGcodeAsync([$"G28 {string.Join(' ', axes.Select(a => a.ToUpperInvariant()))}"], cancellationToken);

	// Speed in mm/min; null keeps the firmware's current one.
	Task JogAsync(double x, double y, double z, double? speed, CancellationToken cancellationToken)
	{
		var move = string.Concat(Axis("X", x), Axis("Y", y), Axis("Z", z), speed is { } feed ? Axis("F", feed) : null);
		return SendGcodeAsync(["G91", $"G1{move}", "G90"], cancellationToken);
	}

	Task ExtrudeAsync(double amount, CancellationToken cancellationToken) =>
		SendGcodeAsync(["M83", $"G1{Axis("E", amount)} F300", "M82"], cancellationToken);

	Task SetFeedrateAsync(int percent, CancellationToken cancellationToken) =>
		SendGcodeAsync([$"M220 S{percent.ToString(CultureInfo.InvariantCulture)}"], cancellationToken);

	Task SetFlowrateAsync(int percent, CancellationToken cancellationToken) =>
		SendGcodeAsync([$"M221 S{percent.ToString(CultureInfo.InvariantCulture)}"], cancellationToken);

	private static string? Axis(string name, double value) =>
		value == 0 && name != "F" ? null : $" {name}{value.ToString("0.###", CultureInfo.InvariantCulture)}";
}

// The link between the server and the printer, for servers where it can be dropped (OctoPrint's serial port).
internal interface IPrinterLink
{
	Task ConnectPrinterAsync(CancellationToken cancellationToken);

	Task DisconnectPrinterAsync(CancellationToken cancellationToken);
}

internal interface ISystemCommands
{
	Task<IReadOnlyList<SystemCommand>> GetSystemCommandsAsync(CancellationToken cancellationToken);

	Task RunSystemCommandAsync(string commandId, CancellationToken cancellationToken);
}

// The connection publishes what it reads to OutputStates.
internal interface IOutputControl
{
	// Read fresh rather than from Settings, so an action never switches an output that was just removed.
	Task<IReadOnlyList<PrinterOutput>> GetOutputsAsync(CancellationToken cancellationToken);

	Task<IReadOnlyDictionary<string, bool>> ReadOutputsAsync(CancellationToken cancellationToken);

	Task SetOutputAsync(string outputId, bool on, CancellationToken cancellationToken);
}
