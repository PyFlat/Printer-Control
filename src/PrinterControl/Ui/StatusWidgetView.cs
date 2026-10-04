using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;

namespace PrinterControl.Ui;

// Every length is a fraction of the shorter side, so one square layout serves 1x1, 2x2 and tall tiles.
internal static class StatusWidgetView
{
	private const double WideAspect = 1.6;

	private enum Face
	{
		Job,
		Heaters,
		Hero,
	}

	public static UiElement Build(UiState<WidgetModel> state, int cornerRadius, Func<DateTimeOffset> clock) => new UiResponsive
	{
		Key = "printer-status",
		Default = WidgetParts.Layout("square", state, cornerRadius, Square),
		Variants =
		[
			new UiResponsiveVariant
			{
				MinAspect = WideAspect,
				Content = WidgetParts.Layout("wide", state, cornerRadius, (m, k) => Wide(m, k, clock())),
			},
		],
	};

	private static Face FaceOf(WidgetModel model) =>
		!model.HasPrinter ? Face.Hero
		: model.Snapshot.IsJobActive ? Face.Job
		: model.Options.ShowTemperatures && model.Snapshot.IsPrinterConnected && WidgetParts.Heaters(model.Snapshot).Count > 0 ? Face.Heaters
		: Face.Hero;

	private static bool ShowsReadings(WidgetModel model) =>
		model.Options.ShowTemperatures && WidgetParts.Heaters(model.Snapshot).Count > 0;

	private static UiStack Square(WidgetModel model, string key) => FaceOf(model) switch
	{
		Face.Job => SquareJob(model, key),
		Face.Heaters => SquareHeaters(model, key),
		_ => SquareHero(model, key),
	};

	private static UiStack Wide(WidgetModel model, string key, DateTimeOffset now) => FaceOf(model) switch
	{
		Face.Job => WideJob(model, key, now),
		Face.Heaters => WideHeaters(model, key),
		_ => WideHero(model, key),
	};

	private static UiStack SquareJob(WidgetModel model, string key)
	{
		var snapshot = model.Snapshot;
		var readings = ShowsReadings(model);
		var diameter = readings ? 0.6 : 0.7;
		var children = new List<UiElement>
		{
			WidgetParts.Row("hero", [JobRing(snapshot, diameter, withCaption: true)], 0, UiComponentJustify.Center),
			WidgetParts.Text("title", WidgetFormat.Title(model), 0.085, UiComponentTextWeights.SemiBold, UiComponentTextRoles.Secondary),
		};
		if (readings)
		{
			children.Add(WidgetParts.Readings("temps", snapshot, 0.068, UiComponentJustify.Center));
		}

		return WidgetParts.Column(key, children);
	}

	private static UiStack WideJob(WidgetModel model, string key, DateTimeOffset now)
	{
		const double diameter = 0.86;
		const double timeSize = 0.25;
		const double etaSize = 0.095;

		var snapshot = model.Snapshot;
		var accent = WidgetFormat.StatusAccent(snapshot.Status);
		var time = new List<UiElement>();
		if (snapshot.Status == PrinterStatus.Printing && snapshot.PrintTimeLeft is { } left)
		{
			var duration = WidgetFormat.Duration(left);
			time.Add(WidgetParts.Text("left", duration, timeSize, UiComponentTextWeights.Bold, width: TextWidth.Of(duration, timeSize)));
			if (model.Options.ShowEta)
			{
				var eta = WidgetFormat.ClockTime(now, left);
				time.Add(WidgetParts.Glyph("clock", Glyphs.Clock, WidgetFormat.Cold, etaSize));
				time.Add(WidgetParts.Text("eta", eta, etaSize, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted, width: TextWidth.Of(eta, etaSize)));
			}
		}
		else
		{
			time.Add(WidgetParts.Text("status", PrinterStatusText.Label(snapshot.Status), 0.16, UiComponentTextWeights.Bold, color: accent.Color, align: UiComponentAlignments.Start) with { Fill = true });
		}

		var details = new List<UiElement>
		{
			WidgetParts.Text("title", WidgetFormat.Title(model), 0.12, UiComponentTextWeights.SemiBold, UiComponentTextRoles.Secondary, align: UiComponentAlignments.Start),
			WidgetParts.Row("time", time, 0.03),
		};
		if (ShowsReadings(model))
		{
			details.Add(WidgetParts.Readings("temps", snapshot, 0.09, UiComponentJustify.Start));
		}

		return WidgetParts.Row(key, [JobRing(snapshot, diameter, withCaption: false), WidgetParts.Column("details", details, 0.05)], 0.08, fill: true);
	}

	// The percentage over the progress ring, with what is left (or what the printer is doing) below it.
	private static UiModifier JobRing(PrinterSnapshot snapshot, double diameter, bool withCaption)
	{
		var accent = WidgetFormat.StatusAccent(snapshot.Status);
		var face = new List<UiElement>
		{
			WidgetParts.Glyph("glyph", WidgetFormat.JobGlyph(snapshot.Status), accent.Color, diameter * (withCaption ? 0.17 : 0.2)),
			WidgetParts.Text("pct", WidgetFormat.Percent(snapshot.Completion), diameter * (withCaption ? 0.25 : 0.27), UiComponentTextWeights.Bold) with { Digits = 4 },
		};
		if (withCaption)
		{
			face.Add(snapshot.Status == PrinterStatus.Printing && snapshot.PrintTimeLeft is { } left
				? WidgetParts.Text("caption", WidgetFormat.Duration(left), diameter * 0.12, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted)
				: WidgetParts.Text("caption", PrinterStatusText.Label(snapshot.Status), diameter * 0.11, UiComponentTextWeights.SemiBold, color: accent.Color));
		}

		return WidgetParts.Ring("ring", diameter, WidgetFormat.Level(snapshot.Completion), accent.Color, face);
	}

	private static UiStack SquareHeaters(WidgetModel model, string key)
	{
		var snapshot = model.Snapshot;
		var heaters = WidgetParts.Heaters(snapshot);
		var gauge = heaters.Count > 1 ? 0.41 : 0.52;
		return WidgetParts.Column(key,
		[
			WidgetParts.Text("status", PrinterStatusText.Label(snapshot.Status), 0.095, UiComponentTextWeights.SemiBold, color: WidgetFormat.StatusColor(snapshot.Status)),
			WidgetParts.Row("gauges", [.. heaters.Select(h => HeaterGauge(h.Id, h.Glyph, h.Heater, gauge))], 0.05, UiComponentJustify.Center),
			WidgetParts.Text("name", model.PrinterName, 0.075, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted),
		], 0.05);
	}

	private static UiStack WideHeaters(WidgetModel model, string key)
	{
		var snapshot = model.Snapshot;
		var gauges = WidgetParts.Heaters(snapshot).Select(h => HeaterGauge(h.Id, h.Glyph, h.Heater, 0.58)).ToList<UiElement>();
		var details = WidgetParts.Column("details",
		[
			WidgetParts.Text("name", model.PrinterName, 0.12, UiComponentTextWeights.SemiBold, UiComponentTextRoles.Secondary, align: UiComponentAlignments.Start),
			WidgetParts.Text("status", PrinterStatusText.Label(snapshot.Status), 0.16, UiComponentTextWeights.Bold, color: WidgetFormat.StatusColor(snapshot.Status), align: UiComponentAlignments.Start),
		], 0.03);

		return WidgetParts.Row(key, [details, .. gauges], 0.06, fill: true);
	}

	// An arc open at the bottom, filled towards the target (or a fixed scale while the heater is off).
	private static UiModifier HeaterGauge(string id, string glyph, Temperature heater, double diameter)
	{
		var color = WidgetFormat.HeaterColor(heater);
		var face = new List<UiElement>
		{
			WidgetParts.Glyph("glyph", glyph, color, diameter * 0.24),
			WidgetParts.Text("actual", WidgetFormat.Degrees(heater.Actual), diameter * 0.25, UiComponentTextWeights.SemiBold),
		};
		if (heater.Target is > 0)
		{
			face.Add(WidgetParts.Text("target", WidgetFormat.Degrees(heater.Target), diameter * 0.14, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted));
		}

		return WidgetParts.Ring(
			id,
			diameter,
			WidgetFormat.HeaterLevel(id, heater),
			color,
			face,
			thickness: 0.1,
			startAngle: WidgetParts.ArcStart,
			endAngle: WidgetParts.ArcEnd) with
		{
			AccessibilityLabel = WidgetParts.HeaterLabel(id, heater),
		};
	}

	private static UiStack SquareHero(WidgetModel model, string key)
	{
		var hero = Hero.Of(model);
		return WidgetParts.Column(key,
		[
			WidgetParts.Row("hero", [HeroRing(hero, model.HasPrinter ? 0.56 : 0.46)], 0, UiComponentJustify.Center),
			WidgetParts.Text("status", hero.Title, 0.1, UiComponentTextWeights.Bold, hero.TitleRole, hero.TitleColor),
			WidgetParts.Text("caption", hero.Caption, model.HasPrinter ? 0.075 : 0.065, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted, maxLines: model.HasPrinter ? 1 : 3),
		], 0.035);
	}

	private static UiStack WideHero(WidgetModel model, string key)
	{
		var hero = Hero.Of(model);
		var details = WidgetParts.Column("details",
		[
			WidgetParts.Text("status", hero.Title, 0.16, UiComponentTextWeights.Bold, hero.TitleRole, hero.TitleColor, align: UiComponentAlignments.Start),
			WidgetParts.Text("caption", hero.Caption, model.HasPrinter ? 0.1 : 0.085, UiComponentTextWeights.Medium, UiComponentTextRoles.Muted, align: UiComponentAlignments.Start, maxLines: model.HasPrinter ? 2 : 3),
		], 0.03);

		return WidgetParts.Row(key, [HeroRing(hero, 0.8), details], 0.08, fill: true);
	}

	private static UiModifier HeroRing(Hero hero, double diameter) => WidgetParts.Ring(
		"ring",
		diameter,
		hero.Lit ? 1 : 0,
		hero.Accent.Color,
		[WidgetParts.Glyph("glyph", hero.Glyph, hero.Lit ? hero.Accent.Color : hero.Accent.Light, diameter * 0.46)],
		thickness: 0.06);

	// A printer that is not printing and shows no heaters: its state, large, over a glyph in a ring.
	private sealed record Hero(string Glyph, Accent Accent, bool Lit, UiText Title, string? TitleColor, UiText Caption)
	{
		public string TitleRole => TitleColor is null ? UiComponentTextRoles.Secondary : UiComponentTextRoles.Primary;

		public static Hero Of(WidgetModel model)
		{
			var snapshot = model.Snapshot;
			if (!model.HasPrinter)
			{
				var none = WidgetFormat.StatusAccent(PrinterStatus.Offline);
				return new Hero(Glyphs.Printer, none, false, Strings.Widgets.NoPrinterTitle(), null, Strings.Widgets.NoPrinter());
			}

			var accent = WidgetFormat.StatusAccent(snapshot.Status);
			var lit = WidgetFormat.IsLit(snapshot.Status);
			return new Hero(
				snapshot.Status == PrinterStatus.Error ? Glyphs.Warning : Glyphs.Printer,
				accent,
				lit,
				PrinterStatusText.Label(snapshot.Status),
				lit ? accent.Color : null,
				snapshot.Status == PrinterStatus.Error && !string.IsNullOrEmpty(snapshot.Error) ? snapshot.Error : model.PrinterName);
		}
	}
}
