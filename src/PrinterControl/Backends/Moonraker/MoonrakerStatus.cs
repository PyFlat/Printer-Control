using System.Text.Json;
using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

// Notifications carry only the fields that changed, so every field is kept until it changes again.
internal sealed class MoonrakerStatus
{
	private readonly Dictionary<string, Dictionary<string, JsonElement>> _objects = new(StringComparer.Ordinal);

	public void Clear() => _objects.Clear();

	public void Merge(JsonElement status)
	{
		if (status.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		foreach (var printerObject in status.EnumerateObject())
		{
			if (printerObject.Value.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			if (!_objects.TryGetValue(printerObject.Name, out var fields))
			{
				_objects[printerObject.Name] = fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			}

			foreach (var field in printerObject.Value.EnumerateObject())
			{
				fields[field.Name] = field.Value.Clone();
			}
		}
	}

	public string? PrintState => Text("print_stats", "state");

	public string? FileName => Text("print_stats", "filename") is { Length: > 0 } name ? name : null;

	// Klippy's state comes from server.info and its notifications; the webhooks object is unreliable for it.
	public PrinterSnapshot ToSnapshot(string klippyState, string? klippyMessage)
	{
		var printState = PrintState;
		var status = klippyState switch
		{
			"ready" => printState switch
			{
				"printing" => PrinterStatus.Printing,
				"paused" => PrinterStatus.Paused,
				_ => PrinterStatus.Operational,
			},
			"startup" => PrinterStatus.Connecting,
			"shutdown" or "error" => PrinterStatus.Error,
			_ => PrinterStatus.Disconnected,
		};

		var active = status is PrinterStatus.Printing or PrinterStatus.Paused;
		var progress = Number("virtual_sdcard", "progress");
		var printTime = Number("print_stats", "print_duration");
		var fileName = FileName;

		return new PrinterSnapshot
		{
			Online = true,
			Status = status,
			StateText = klippyState == "ready" ? printState ?? klippyState : klippyState,
			Error = klippyState is "shutdown" or "error" ? NonEmpty(klippyMessage)
				: printState == "error" ? NonEmpty(Text("print_stats", "message"))
				: null,
			FileName = fileName is null ? null : Path.GetFileName(fileName),
			FileId = fileName,
			Completion = active && progress is { } done ? Math.Clamp(done * 100, 0, 100) : null,
			PrintTime = active ? Seconds(printTime) : null,
			PrintTimeLeft = active && progress is > 0 and var p && printTime is { } elapsed ? Seconds((elapsed / p) - elapsed) : null,
			CurrentZ = Field("gcode_move", "gcode_position") is { ValueKind: JsonValueKind.Array } position
				&& position.GetArrayLength() > 2 && position[2].TryGetDouble(out var z) ? z : null,
			Temperatures = Temperatures(),
		};
	}

	private Dictionary<string, Temperature> Temperatures()
	{
		var temperatures = new Dictionary<string, Temperature>(StringComparer.Ordinal);
		foreach (var (name, fields) in _objects)
		{
			if (MoonrakerHeaters.ToHeaterId(name) is not { } heater)
			{
				continue;
			}

			var actual = fields.TryGetValue("temperature", out var t) && t.TryGetDouble(out var a) ? a : (double?)null;
			var target = fields.TryGetValue("target", out var g) && g.TryGetDouble(out var b) ? b : (double?)null;

			// A temperature_sensor chamber only reads; a heater_generic chamber wins when both exist.
			if (heater == HeaterIds.Chamber && target is null && temperatures.ContainsKey(heater))
			{
				continue;
			}

			temperatures[heater] = new Temperature(actual, target);
		}

		return temperatures;
	}

	private JsonElement? Field(string printerObject, string field) =>
		_objects.TryGetValue(printerObject, out var fields) && fields.TryGetValue(field, out var value) ? value : null;

	private string? Text(string printerObject, string field) =>
		Field(printerObject, field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

	private double? Number(string printerObject, string field) =>
		Field(printerObject, field) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

	private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

	private static int? Seconds(double? value) => value is { } seconds && seconds >= 0 ? (int)Math.Round(seconds) : null;
}
