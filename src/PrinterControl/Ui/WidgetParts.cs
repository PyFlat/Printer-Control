using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.References;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;

namespace PrinterControl.Ui;

internal static class WidgetParts
{
	private const double BreathingRoomPx = 8;
	private static readonly double CornerClearance = 1 - (1 / Math.Sqrt(2));

	public const double FullTurn = 360;
	public const double ArcStart = -135;
	public const double ArcEnd = 135;

	public static UiSize SafeArea(int cornerRadius)
	{
		var inset = Math.Max(BreathingRoomPx, CornerClearance * Math.Max(0, cornerRadius));
		return UiSize.Of(UiLength.Capped(inset / UiLength.Cell, inset));
	}

	// The model is templated as a one-item repeat, so a state change rebuilds the body in place.
	public static UiStack Layout(string key, UiState<WidgetModel> state, int cornerRadius, Func<WidgetModel, string, UiElement> body) => new()
	{
		Key = key,
		Direction = UiComponentDirections.Vertical,
		Align = UiComponentAlignments.Stretch,
		Justify = UiComponentJustify.Center,
		Padding = SafeArea(cornerRadius),
		Children =
		[
			new UiRepeat<WidgetModel>
			{
				Key = key + "-model",
				Items = UiValue.From<IReadOnlyList<WidgetModel>>(() => [state.Value]),
				KeySelector = _ => "model",
				Template = body,
			},
		],
	};

	public static UiStack Column(string key, IReadOnlyList<UiElement> children, double gap = 0.035, string? align = null) => new()
	{
		Key = key,
		Direction = UiComponentDirections.Vertical,
		Align = align ?? UiComponentAlignments.Stretch,
		Justify = UiComponentJustify.Center,
		Fill = true,
		Gap = gap,
		Children = children,
	};

	public static UiStack Row(string key, IReadOnlyList<UiElement> children, double gap, string? justify = null, bool fill = false) => new()
	{
		Key = key,
		Direction = UiComponentDirections.Horizontal,
		Align = UiComponentAlignments.Center,
		Justify = justify ?? UiComponentJustify.Start,
		Fill = fill,
		Gap = gap,
		Children = children,
	};

	public static UiTextRun Text(
		string key,
		UiText text,
		double size,
		string? weight = null,
		string? role = null,
		string? color = null,
		string? align = null,
		int maxLines = 1,
		double? width = null) => new()
	{
		Key = key,
		Text = text,
		Size = UiSize.FromBasis(size, 0.9),
		MinSize = size * 0.6,
		Weight = weight ?? UiComponentTextWeights.Regular,
		Role = role ?? UiComponentTextRoles.Primary,
		Color = color is null ? default(UiValue<string>) : color,
		MaxLines = maxLines,
		Wrap = maxLines > 1,
		Align = align ?? (width is null ? UiComponentAlignments.Center : UiComponentAlignments.Start),
		MainSize = width is { } fixedWidth ? fixedWidth : default(UiSize),
	};

	public static UiModifier Glyph(string key, string path, string color, double size) => new()
	{
		Key = key,
		MainSize = size,
		Frame = new UiFrame { Width = UiLength.OfBasis(size), Height = UiLength.OfBasis(size) },
		Child = new UiShape { Key = key + "-shape", Shape = UiComponentShapes.Path, Path = path, Color = color },
	};

	// A level drawn along an arc in a square of its own, with a face drawn over its middle.
	public static UiModifier Ring(
		string key,
		double diameter,
		double level,
		string color,
		IReadOnlyList<UiElement> face,
		double thickness = 0.085,
		double startAngle = 0,
		double endAngle = FullTurn)
	{
		var layers = new List<UiElement>
		{
			new UiGauge
			{
				Key = "gauge",
				Level = level,
				StartAngle = startAngle,
				EndAngle = endAngle,
				LevelColor = color,
				Thickness = UiSize.FromBasis(diameter * thickness),
			},
			new UiStack
			{
				Key = "face",
				Direction = UiComponentDirections.Vertical,
				Justify = UiComponentJustify.Center,
				Align = UiComponentAlignments.Center,
				Gap = UiSize.FromBasis(diameter * 0.02),
				Children = face,
			},
		};

		return new UiModifier
		{
			Key = key,
			MainSize = diameter,
			Frame = new UiFrame { Width = UiLength.OfBasis(diameter), Height = UiLength.OfBasis(diameter) },
			Child = new UiLayer { Key = key + "-layers", Children = layers },
		};
	}

	public static IReadOnlyList<(string Id, string Glyph, Temperature Heater)> Heaters(PrinterSnapshot snapshot)
	{
		var heaters = new List<(string, string, Temperature)>();
		if (snapshot.Heater(HeaterIds.Extruder) is { } tool)
		{
			heaters.Add((HeaterIds.Extruder, Glyphs.Nozzle, tool));
		}

		if (snapshot.Heater(HeaterIds.Bed) is { } bed)
		{
			heaters.Add((HeaterIds.Bed, Glyphs.Bed, bed));
		}

		return heaters;
	}

	public static UiText HeaterLabel(string id, Temperature heater) => id == HeaterIds.Bed
		? Strings.Widgets.Temps.Bed(WidgetFormat.Heater(heater))
		: Strings.Widgets.Temps.Hotend(WidgetFormat.Heater(heater));

	// One glyph and reading per heater; a heater still on its way up shows its target beside it.
	public static UiStack Readings(string key, PrinterSnapshot snapshot, double size, string justify)
	{
		var readings = Heaters(snapshot).Select(h =>
		{
			var color = WidgetFormat.HeaterColor(h.Heater);
			var actual = WidgetFormat.Degrees(h.Heater.Actual);
			var parts = new List<UiElement>
			{
				Glyph("glyph", h.Glyph, color, size * 1.2),
				Text("actual", actual, size, UiComponentTextWeights.SemiBold, color: color, width: TextWidth.Of(actual, size)),
			};
			if (WidgetFormat.IsHeating(h.Heater))
			{
				var target = "/" + WidgetFormat.Degrees(h.Heater.Target);
				parts.Add(Text("target", target, size * 0.8, role: UiComponentTextRoles.Muted, width: TextWidth.Of(target, size * 0.8)));
			}

			return (UiElement)new UiModifier
			{
				Key = h.Id + "-label",
				AccessibilityLabel = HeaterLabel(h.Id, h.Heater),
				Child = Row(h.Id, parts, size * 0.2),
			};
		}).ToList();

		return Row(key, readings, size * 0.9, justify);
	}

	// The renderer always paints a gradient, so both ends get the same colour.
	public static UiProgressBar Bar(string key, double level, string color, double thickness) => new()
	{
		Key = key,
		MainSize = thickness,
		Thickness = thickness,
		Value = UiProgressReference.Halted((long)Math.Round(level * 1000), DateTimeOffset.UtcNow, 1000),
		StartColor = color,
		EndColor = color,
	};
}
