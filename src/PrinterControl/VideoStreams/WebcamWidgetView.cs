using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.References;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;

namespace PrinterControl.Ui;

// The video node is built once per session and never re-templated, so a progress update does not make
// the client reopen the stream.
internal static class WebcamWidgetView
{
	private const string OverlayText = "#ffffff";
	private const string Scrim = "#000000";
	private const double ScrimHeight = 0.6;
	private const double ScrimOpacity = 0.8;
	private const double CornerSize = 0.16;
	private const string RecordingRed = "#f04848";

	private const string TopLeft = "M0.1 0.9 L0.1 0.1 L0.9 0.1";
	private const string TopRight = "M0.1 0.1 L0.9 0.1 L0.9 0.9";
	private const string BottomLeft = "M0.1 0.1 L0.1 0.9 L0.9 0.9";
	private const string BottomRight = "M0.1 0.9 L0.9 0.9 L0.9 0.1";

	public static UiElement Build(UiState<WidgetModel> state, int cornerRadius, string providerId)
	{
		var model = state.Value;
		if (!model.HasPrinter || string.IsNullOrEmpty(providerId))
		{
			return WidgetParts.Layout("webcam-empty", state, cornerRadius, (m, key) => WidgetParts.Column(key,
			[
				WidgetParts.Glyph("glyph", Glyphs.Camera, WidgetFormat.Cold, 0.3),
				WidgetParts.Text(
					"caption",
					m.HasPrinter ? Strings.Widgets.Webcam.Fallback() : Strings.Widgets.NoPrinter(),
					0.075,
					UiComponentTextWeights.Medium,
					UiComponentTextRoles.Muted,
					maxLines: 3),
			], 0.06, UiComponentAlignments.Center));
		}

		return Compose(state, cornerRadius, new UiTransform
		{
			Key = "orientation",
			Rotation = UiValue.From(() => state.Value.WebcamRotation),
			Children =
			[
				new UiVideoStream
				{
					Key = "video",
					Stream = UiValue.Of(new UiVideoStreamReference { Provider = providerId, Id = model.PrinterKey }),
					Fit = model.Options.Fit,
					Fill = true,
					Fallback = WidgetParts.Text("fallback", Strings.Widgets.Webcam.Fallback(), 0.08, role: UiComponentTextRoles.Muted, maxLines: 3),
				},
			],
		});
	}

	// The picture is the webcam stream, or the viewfinder where no stream can open.
	internal static UiLayer Compose(UiState<WidgetModel> state, int cornerRadius, UiElement picture)
	{
		var children = new List<UiElement> { picture };
		if (state.Value.Options.Overlay)
		{
			children.Add(new UiStack
			{
				Key = "overlay",
				Direction = UiComponentDirections.Vertical,
				Justify = UiComponentJustify.End,
				Align = UiComponentAlignments.Stretch,
				Fill = true,
				Children =
				[
					new UiRepeat<WidgetModel>
					{
						Key = "overlay-model",
						Items = UiValue.From<IReadOnlyList<WidgetModel>>(() => state.Value.Snapshot.IsJobActive ? [state.Value] : []),
						KeySelector = _ => "model",
						Template = (m, key) => Overlay(m, key, cornerRadius),
					},
				],
			});
		}

		return new UiLayer { Key = "printer-webcam", Children = children };
	}

	// The widget picker's sample card and the Developer Tools previews have no stream to open.
	public static UiLayer Sample(UiState<WidgetModel> state, int cornerRadius) =>
		Compose(state, cornerRadius, Viewfinder(cornerRadius));

	// Camera corner marks around a lens, standing in for the picture.
	private static UiStack Viewfinder(int cornerRadius) => new()
	{
		Key = "viewfinder",
		Direction = UiComponentDirections.Vertical,
		Padding = WidgetParts.SafeArea(cornerRadius),
		Children =
		[
			new UiLayer
			{
				Key = "frame",
				Fill = true,
				Children =
				[
					new UiStack
					{
						Key = "corners",
						Direction = UiComponentDirections.Vertical,
						Justify = UiComponentJustify.SpaceBetween,
						Children =
						[
							WidgetParts.Row("top", [Corner("top-left", TopLeft), WidgetParts.Glyph("recording", Glyphs.Dot, RecordingRed, 0.05), Corner("top-right", TopRight)], 0, UiComponentJustify.SpaceBetween),
							WidgetParts.Row("bottom", [Corner("bottom-left", BottomLeft), Corner("bottom-right", BottomRight)], 0, UiComponentJustify.SpaceBetween),
						],
					},
					new UiStack
					{
						Key = "lens",
						Direction = UiComponentDirections.Vertical,
						Justify = UiComponentJustify.Center,
						Align = UiComponentAlignments.Center,
						Children = [WidgetParts.Glyph("camera", Glyphs.Camera, WidgetFormat.Cold, 0.22)],
					},
				],
			},
		],
	};

	private static UiModifier Corner(string key, string path) => new()
	{
		Key = key,
		MainSize = CornerSize,
		Frame = new UiFrame { Width = UiLength.OfBasis(CornerSize), Height = UiLength.OfBasis(CornerSize) },
		Child = new UiShape
		{
			Key = key + "-line",
			Shape = UiComponentShapes.Path,
			Path = path,
			StrokeColor = WidgetFormat.Cold,
			StrokeWidth = UiSize.FromBasis(0.022),
		},
	};

	// The progress over a shade that darkens towards the bottom edge, so the text reads on any picture.
	private static UiLayer Overlay(WidgetModel model, string key, int cornerRadius) => new()
	{
		Key = key,
		Fill = true,
		Children =
		[
			new UiStack
			{
				Key = "shade",
				Direction = UiComponentDirections.Vertical,
				Justify = UiComponentJustify.End,
				Children =
				[
					new UiModifier
					{
						Key = "scrim",
						MainSize = ScrimHeight,
						Mask = UiMask.Linear(
							0,
							new UiMaskStop { Offset = 0, Opacity = ScrimOpacity },
							new UiMaskStop { Offset = 1, Opacity = 0 }),
						Child = new UiShape { Key = "scrim-fill", Color = Scrim },
						Fallback = new UiStack { Key = "scrim-none" },
					},
				],
			},
			Strip(model, cornerRadius),
		],
	};

	private static UiStack Strip(WidgetModel model, int cornerRadius)
	{
		const double percentSize = 0.15;
		const double captionSize = 0.085;

		var snapshot = model.Snapshot;
		var accent = WidgetFormat.StatusAccent(snapshot.Status);
		var percent = WidgetFormat.Percent(snapshot.Completion);
		UiElement caption = snapshot.Status == PrinterStatus.Printing
			? WidgetParts.Text("left", WidgetFormat.Duration(snapshot.PrintTimeLeft), captionSize, UiComponentTextWeights.Medium, color: OverlayText, align: UiComponentAlignments.End) with { Fill = true }
			: WidgetParts.Text("left", PrinterStatusText.Label(snapshot.Status), captionSize, UiComponentTextWeights.SemiBold, color: accent.Light, align: UiComponentAlignments.End) with { Fill = true };

		return new UiStack
		{
			Key = "strip",
			Direction = UiComponentDirections.Vertical,
			Justify = UiComponentJustify.End,
			Padding = WidgetParts.SafeArea(cornerRadius),
			Gap = 0.035,
			Children =
			[
				WidgetParts.Row("labels",
				[
					WidgetParts.Glyph("glyph", WidgetFormat.JobGlyph(snapshot.Status), accent.Light, percentSize * 0.8),
					WidgetParts.Text("pct", percent, percentSize, UiComponentTextWeights.Bold, color: OverlayText, width: TextWidth.Of(percent, percentSize)),
					caption,
				], 0.03),
				WidgetParts.Bar("bar", WidgetFormat.Level(snapshot.Completion), accent.Color, 0.035),
			],
		};
	}
}
