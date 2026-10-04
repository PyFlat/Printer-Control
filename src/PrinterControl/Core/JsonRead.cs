using System.Text.Json;

namespace PrinterControl.Core;

// Printer servers omit, null or retype fields between versions and plugins, so every read is tolerant.
internal static class JsonRead
{
	public static JsonElement? Obj(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.Object
			? value
			: null;

	public static JsonElement? Arr(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.Array
			? value
			: null;

	public static string? Str(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	public static double? Num(this JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
		{
			return null;
		}

		return value.ValueKind switch
		{
			JsonValueKind.Number => value.GetDouble(),
			JsonValueKind.String when double.TryParse(
				value.GetString(),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture,
				out var parsed) => parsed,
			_ => null,
		};
	}

	public static bool? Bool(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
			? value.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null,
			}
			: null;
}
