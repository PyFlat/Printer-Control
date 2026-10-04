using PrinterControl.Core;

namespace PrinterControl.Backends.OctoPrint;

internal sealed record OctoPrintVersion(string Api, string Server);

internal sealed record OctoPrintLogin(string Name, string Session);

// /api/settings: the shared part, plus the GPIO Control outputs with the positions its API needs.
internal sealed record OctoPrintSettings(PrinterSettings Common, IReadOnlyList<GpioOutput> Gpio);

// One output of the GPIO Control plugin. Its API addresses outputs by their position in the list; the
// output id follows the pin instead, so reordering them in OctoPrint never switches a different one.
internal sealed record GpioOutput(int Index, int Pin, string Name)
{
	public string OutputId => $"gpio{Pin}";
}

internal sealed record AppKeyRequest(string Token);

internal enum AppKeyDecision
{
	Pending,
	Granted,
	Denied,
}

internal sealed record AppKeyPoll(AppKeyDecision Decision, string? ApiKey);
