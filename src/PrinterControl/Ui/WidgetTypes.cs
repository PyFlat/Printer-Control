using MacroDeck.Sdk.Widgets;

namespace PrinterControl.Ui;

// Local ids are persisted as every placed widget's type; never rename one.
internal static class WidgetTypes
{
	public const string StatusId = "status";
	public const string WebcamId = "webcam";

	public static bool Matches(string widgetTypeAttribute, string localId) =>
		string.Equals(widgetTypeAttribute, localId, StringComparison.Ordinal)
		|| widgetTypeAttribute.EndsWith("::" + localId, StringComparison.Ordinal);

	public static IReadOnlyList<WidgetTypeDescriptor> All { get; } =
	[
		new WidgetTypeDescriptor(
			StatusId,
			Strings.Widgets.Status.Name(),
			Strings.Widgets.Status.Description(),
			DefaultData: DefaultData,
			DataSchema: Schema,
			HasConfiguration: true)
		{
			SupportsFlows = true,
		},
#if VIDEO_STREAMS
		new WidgetTypeDescriptor(
			WebcamId,
			Strings.Widgets.Webcam.Name(),
			Strings.Widgets.Webcam.Description(),
			DefaultData: DefaultData,
			DataSchema: Schema,
			HasConfiguration: true)
		{
			SupportsFlows = true,
		},
#endif
	];

	public static string? LocalId(string widgetTypeAttribute) =>
		All.Select(d => d.Id).FirstOrDefault(id => Matches(widgetTypeAttribute, id));

	private const string DefaultData =
		"""{"printer":"first","showFile":true,"showTemperatures":true,"showEta":true,"overlay":true,"fit":"contain"}""";

	private const string Schema = """
		{
		  "$schema": "https://json-schema.org/draft/2020-12/schema",
		  "type": "object",
		  "additionalProperties": false,
		  "properties": {
		    "printer": { "type": "string", "description": "The printer's key, or 'first' for the first configured printer." },
		    "showFile": { "type": "boolean", "default": true },
		    "showTemperatures": { "type": "boolean", "default": true },
		    "showEta": { "type": "boolean", "default": true },
		    "overlay": { "type": "boolean", "default": true, "description": "Draw the print progress over the webcam picture." },
		    "fit": { "type": "string", "enum": ["contain", "cover"], "default": "contain" },
		    "flows": { "type": "array" },
		    "border": { "type": "object" }
		  }
		}
		""";
}
