using System.Text.Json;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Previews;
using MacroDeck.Ui.Runtime;
using NUnit.Framework;
using PrinterControl.Core;
using PrinterControl.Ui;

namespace PrinterControl.Tests;

// Every tree is built through a real UiView: a malformed key only throws when a session opens, which
// conformance does not catch.
[TestFixture]
public sealed class WidgetTests
{
	private static UiSurface Surface(string kind = UiSurfaceKinds.Widget) => new()
	{
		Kind = kind,
		SessionMode = kind == UiSurfaceKinds.Config ? UiSessionModes.Exclusive : UiSessionModes.Shared,
		Attributes = new Dictionary<string, JsonElement>(),
	};

	private static readonly string[] Models = ["printing", "paused", "idle", "heating", "offline", "error", "no printer"];

	private static WidgetModel Model(string name) => name switch
	{
		"printing" => WidgetSamples.Printing,
		"paused" => WidgetSamples.Paused,
		"idle" => WidgetSamples.Idle,
		"heating" => WidgetSamples.Heating,
		"offline" => WidgetSamples.Offline,
		"error" => WidgetSamples.Error,
		_ => WidgetModel.NoPrinter(WidgetOptions.Default),
	};

	[TestCaseSource(nameof(Models))]
	public void The_status_widget_builds(string model) =>
		Assert.That(new UiView(Surface(), StatusWidgetView.Build(new UiState<WidgetModel>(Model(model)), 16, () => WidgetSamples.Now)).Tree, Is.Not.Null);


	[Test]
	public void A_state_change_patches_the_open_view()
	{
		var state = new UiState<WidgetModel>(WidgetSamples.Printing);
		var view = new UiView(Surface(), StatusWidgetView.Build(state, 16, () => WidgetSamples.Now));
		view.DrainPatches();

		state.Set(WidgetSamples.Printing with { Snapshot = WidgetSamples.Printing.Snapshot with { Completion = 64.9 } });

		Assert.That(view.DrainPatches(), Is.Not.Empty);
	}

	[TestCase(WidgetTypes.StatusId)]
	[TestCase(WidgetTypes.WebcamId)]
	public void The_configuration_view_builds(string widgetId)
	{
		var printers = new[] { new PrinterConfig(Guid.NewGuid(), "octoprint", "Prusa", new Uri("http://octopi.local/"), "key", null) };
		var data = JsonDocument.Parse("""{"printer": "", "flows": [{"trigger": "press"}]}""").RootElement;

		var view = new UiView(Surface(UiSurfaceKinds.Config), WidgetConfigView.Build(widgetId, WidgetOptions.Parse(data), data, printers));

		Assert.That(view.Tree, Is.Not.Null);
	}

	[Test]
	public void Every_widget_type_has_a_valid_schema_and_default_data()
	{
		foreach (var descriptor in WidgetTypes.All)
		{
			using var schema = JsonDocument.Parse(descriptor.DataSchema!);
			using var defaults = JsonDocument.Parse(descriptor.DefaultData!);
			var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();

			Assert.That(defaults.RootElement.EnumerateObject().Select(p => p.Name), Is.SubsetOf(allowed), descriptor.Id);
			Assert.That(allowed, Does.Contain("flows").And.Contain("border"), descriptor.Id);
			Assert.That(WidgetOptions.Parse(defaults.RootElement), Is.EqualTo(WidgetOptions.Default with { Printer = WidgetOptions.FirstPrinter }));
		}
	}

	[Test]
	public void Stored_data_is_read_with_defaults_for_what_is_missing()
	{
		var options = WidgetOptions.Parse(JsonDocument.Parse("""{"printer": "abc", "showEta": false, "fit": "cover"}""").RootElement);

		Assert.That(options, Is.EqualTo(WidgetOptions.Default with { Printer = "abc", ShowEta = false, Fit = WidgetOptions.FitCover }));
	}

	[Test]
	public void Rotation_follows_the_octoprint_webcam_settings()
	{
		using (Assert.EnterMultipleScope())
		{
			Assert.That(WidgetModel.RotationFor(WebcamSettings.None), Is.Zero);
			Assert.That(WidgetModel.RotationFor(WebcamSettings.None with { Rotation = 270 }), Is.EqualTo(270));
			Assert.That(WidgetModel.RotationFor(WebcamSettings.None with { FlipH = true, FlipV = true }), Is.EqualTo(180));
			Assert.That(WidgetModel.RotationFor(WebcamSettings.None with { Rotation = 270, FlipH = true, FlipV = true }), Is.EqualTo(90));
		}
	}

	[TestCase(59, "1m")]
	[TestCase(1500, "25m")]
	[TestCase(7500, "2:05h")]
	public void Durations_read_well(int seconds, string expected) =>
		Assert.That(WidgetFormat.Duration(seconds), Is.EqualTo(expected));

	[Test]
	public void Developer_previews_are_discovered_and_build()
	{
		var scan = UiPreviewCatalog.Scan(typeof(WidgetSamples).Assembly);

		Assert.That(scan.Diagnostics, Is.Empty);
		var ours = scan.Registrations.Where(r => r.Declaration.Id.Contains(nameof(WidgetSamples), StringComparison.Ordinal)).ToArray();
		Assert.That(ours, Has.Length.EqualTo(11));
	}
}

[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id() =>
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.pyflat.printer-control"));

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True, key);
			Assert.That(text, Is.Not.Empty, key);
		}
	}
}
