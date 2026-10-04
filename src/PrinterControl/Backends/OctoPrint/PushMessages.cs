using System.Text.Json;
using PrinterControl.Core;

namespace PrinterControl.Backends.OctoPrint;

// https://docs.octoprint.org/en/master/api/push.html, https://docs.octoprint.org/en/master/events/
internal static class PushMessages
{
	private static readonly Dictionary<string, PrinterEventKind> MappedEvents = new(StringComparer.Ordinal)
	{
		["PrintStarted"] = PrinterEventKind.PrintStarted,
		["PrintDone"] = PrinterEventKind.PrintDone,
		["PrintFailed"] = PrinterEventKind.PrintFailed,
		["PrintCancelled"] = PrinterEventKind.PrintCancelled,
		["PrintPaused"] = PrinterEventKind.PrintPaused,
		["PrintResumed"] = PrinterEventKind.PrintResumed,
		["Connected"] = PrinterEventKind.Connected,
		["Disconnected"] = PrinterEventKind.Disconnected,
		["Error"] = PrinterEventKind.Error,
	};

	public static PrinterSnapshot ApplyCurrent(PrinterSnapshot previous, JsonElement current)
	{
		var state = current.Obj("state");
		var flags = state?.Obj("flags");
		var stateText = state?.Str("text") ?? previous.StateText;
		var job = current.Obj("job");
		var file = job?.Obj("file");
		var progress = current.Obj("progress");

		var fileName = file?.Str("display") ?? file?.Str("name");
		var error = state?.Str("error");

		return previous with
		{
			Online = true,
			Status = flags is { } f ? StatusFrom(f, stateText) : previous.Status,
			StateText = stateText,
			Error = string.IsNullOrWhiteSpace(error) ? null : error,
			FileName = string.IsNullOrEmpty(fileName) ? null : fileName,
			FileId = file?.Str("path") is { Length: > 0 } path ? $"{file?.Str("origin") ?? "local"}:{path}" : null,
			Completion = progress?.Num("completion") is { } completion ? Math.Clamp(completion, 0, 100) : null,
			PrintTime = ToSeconds(progress?.Num("printTime")),
			PrintTimeLeft = ToSeconds(progress?.Num("printTimeLeft")),
			EstimatedPrintTime = job?.Num("estimatedPrintTime"),
			CurrentZ = current.Num("currentZ") ?? (current.TryGetProperty("currentZ", out _) ? null : previous.CurrentZ),
			Temperatures = MergeTemperatures(previous.Temperatures, current.Arr("temps")),
		};
	}

	public static PrinterEvent? ReadEvent(JsonElement eventFrame)
	{
		if (eventFrame.Str("type") is not { Length: > 0 } type)
		{
			return null;
		}

		var payload = eventFrame.TryGetProperty("payload", out var value) ? value.Clone() : default;
		var received = new PrinterEvent(type, payload);
		if (!MappedEvents.TryGetValue(type, out var kind))
		{
			return received;
		}

		// OctoPrint reports a cancel as PrintCancelled and again as PrintFailed with reason "cancelled".
		var reason = kind switch
		{
			PrinterEventKind.PrintFailed => payload.Str("reason") ?? string.Empty,
			PrinterEventKind.Error => payload.Str("error") ?? string.Empty,
			_ => null,
		};
		if (kind == PrinterEventKind.PrintFailed && reason == "cancelled")
		{
			return received;
		}

		return received with
		{
			Kind = kind,
			File = payload.Str("display") ?? payload.Str("name"),
			Duration = kind == PrinterEventKind.PrintDone && payload.Num("time") is { } seconds ? Math.Round(seconds) : null,
			Reason = reason,
		};
	}

	internal static PrinterStatus StatusFrom(JsonElement flags, string stateText)
	{
		bool Flag(string name) => flags.Bool(name) ?? false;

		if (Flag("error"))
		{
			return PrinterStatus.Error;
		}

		if (Flag("cancelling"))
		{
			return PrinterStatus.Cancelling;
		}

		if (Flag("pausing"))
		{
			return PrinterStatus.Pausing;
		}

		if (Flag("resuming"))
		{
			return PrinterStatus.Resuming;
		}

		if (Flag("paused"))
		{
			return PrinterStatus.Paused;
		}

		if (Flag("finishing"))
		{
			return PrinterStatus.Finishing;
		}

		if (Flag("printing"))
		{
			return PrinterStatus.Printing;
		}

		if (Flag("operational"))
		{
			return PrinterStatus.Operational;
		}

		// "Offline", "Closed" and "Offline after error" are closed; "Opening serial connection",
		// "Detecting serial connection" and "Connecting" are on their way.
		return Flag("closedOrError") || stateText.StartsWith("Offline", StringComparison.OrdinalIgnoreCase)
			? PrinterStatus.Disconnected
			: PrinterStatus.Connecting;
	}

	// A frame carries only the samples since the last one, often none, so an absent heater keeps its
	// previous reading.
	private static IReadOnlyDictionary<string, Temperature> MergeTemperatures(
		IReadOnlyDictionary<string, Temperature> previous,
		JsonElement? temps)
	{
		if (temps is not { } samples || samples.GetArrayLength() == 0)
		{
			return previous;
		}

		var latest = samples[samples.GetArrayLength() - 1];
		if (latest.ValueKind != JsonValueKind.Object)
		{
			return previous;
		}

		var merged = new Dictionary<string, Temperature>(previous, StringComparer.Ordinal);
		foreach (var heater in latest.EnumerateObject())
		{
			if (heater.Value.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			var id = OctoPrintHeaters.ToHeaterId(heater.Name);
			var actual = heater.Value.Num("actual");
			var target = heater.Value.Num("target");
			if (actual is null && target is null)
			{
				merged.Remove(id);
				continue;
			}

			merged[id] = new Temperature(actual, target);
		}

		return merged;
	}

	private static int? ToSeconds(double? value) => value is { } seconds && seconds >= 0 ? (int)Math.Round(seconds) : null;
}
