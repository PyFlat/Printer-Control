using System.Text.Json;
using NUnit.Framework;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.Core;

namespace PrinterControl.Tests;

[TestFixture]
public sealed class PushMessagesTests
{
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

	[Test]
	public void A_printing_frame_fills_the_snapshot()
	{
		var frame = Parse(Support.FakeOctoPrint.CurrentFrame("Printing", Support.FakeOctoPrint.Printing, 42.37, "benchy.gcode", 214.6, 215))
			.GetProperty("current");

		var snapshot = PushMessages.ApplyCurrent(PrinterSnapshot.Offline, frame);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(snapshot.Online, Is.True);
			Assert.That(snapshot.Status, Is.EqualTo(PrinterStatus.Printing));
			Assert.That(snapshot.IsJobActive, Is.True);
			Assert.That(snapshot.FileName, Is.EqualTo("benchy.gcode"));
			Assert.That(snapshot.FileId, Is.EqualTo("local:benchy.gcode"), "the id the file list uses");
			Assert.That(snapshot.Completion, Is.EqualTo(42.37));
			Assert.That(snapshot.PrintTimeLeft, Is.EqualTo(3480));
			Assert.That(snapshot.CurrentZ, Is.EqualTo(0.2));
			Assert.That(snapshot.Heater(HeaterIds.Extruder), Is.EqualTo(new Temperature(214.6, 215)));
			Assert.That(snapshot.Heater(HeaterIds.Bed), Is.EqualTo(new Temperature(60, 60)));
		}
	}

	[Test]
	public void A_frame_without_temperatures_keeps_the_previous_readings()
	{
		var first = PushMessages.ApplyCurrent(
			PrinterSnapshot.Offline,
			Parse(Support.FakeOctoPrint.CurrentFrame("Operational", Support.FakeOctoPrint.Operational, tool: 30)).GetProperty("current"));

		var second = PushMessages.ApplyCurrent(first, Parse("""{"state": {"text": "Operational", "flags": {"operational": true}}, "temps": []}"""));

		Assert.That(second.Heater(HeaterIds.Extruder)?.Actual, Is.EqualTo(30));
	}

	[Test]
	public void A_heater_reporting_nothing_is_removed()
	{
		var snapshot = PushMessages.ApplyCurrent(
			PrinterSnapshot.Offline,
			Parse("""{"temps": [{"time": 1, "tool0": {"actual": 25, "target": 0}, "chamber": {"actual": null, "target": null}}]}"""));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(snapshot.Heater(HeaterIds.Extruder), Is.Not.Null);
			Assert.That(snapshot.Heater(HeaterIds.Chamber), Is.Null);
		}
	}

	[TestCase("tool0", HeaterIds.Extruder)]
	[TestCase("tool1", "extruder1")]
	[TestCase("bed", HeaterIds.Bed)]
	[TestCase("chamber", HeaterIds.Chamber)]
	public void OctoPrint_heaters_map_to_heater_ids_and_back(string octoPrint, string heaterId)
	{
		using (Assert.EnterMultipleScope())
		{
			Assert.That(OctoPrintHeaters.ToHeaterId(octoPrint), Is.EqualTo(heaterId));
			Assert.That(OctoPrintHeaters.FromHeaterId(heaterId), Is.EqualTo(octoPrint));
		}
	}

	[TestCase("""{"error": true, "operational": false}""", "Offline after error", "Error")]
	[TestCase("""{"cancelling": true, "printing": true}""", "Cancelling", "Cancelling")]
	[TestCase("""{"pausing": true, "printing": true}""", "Pausing", "Pausing")]
	[TestCase("""{"paused": true, "operational": true}""", "Paused", "Paused")]
	[TestCase("""{"printing": true, "operational": true}""", "Printing", "Printing")]
	[TestCase("""{"operational": true}""", "Operational", "Operational")]
	[TestCase("""{"closedOrError": true}""", "Offline", "Disconnected")]
	[TestCase("""{"closedOrError": false}""", "Opening serial connection", "Connecting")]
	public void Flags_map_to_a_status(string flags, string text, string expected) =>
		Assert.That(PushMessages.StatusFrom(Parse(flags), text).ToString(), Is.EqualTo(expected));

	[Test]
	public void An_event_frame_is_read()
	{
		var received = PushMessages.ReadEvent(Parse("""{"type": "PrintDone", "payload": {"name": "a.gcode", "time": 61.5}}"""));

		using (Assert.EnterMultipleScope())
		{
			Assert.That(received?.Type, Is.EqualTo("PrintDone"));
			Assert.That(received?.Payload.Num("time"), Is.EqualTo(61.5));
		}
	}
}

[TestFixture]
public sealed class SettingsParsingTests
{
	private static OctoPrintSettings Parse(string json) => OctoPrintApi.ParseSettings(JsonDocument.Parse(json).RootElement);

	[Test]
	public void Legacy_webcam_settings_and_profiles_are_read()
	{
		var settings = Parse("""
			{"appearance": {"name": "  Prusa  "},
			 "webcam": {"webcamEnabled": true, "streamUrl": "/webcam/?action=stream", "rotate90": true, "streamRatio": "4:3"},
			 "temperature": {"profiles": [{"name": "PLA", "extruder": 215, "bed": 60, "chamber": null}, {"name": ""}]}}
			""");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(settings.Common.InstanceName, Is.EqualTo("Prusa"));
			Assert.That(settings.Common.Webcam.Enabled, Is.True);
			Assert.That(settings.Common.Webcam.StreamUrl, Is.EqualTo("/webcam/?action=stream"));
			Assert.That(settings.Common.Webcam.Rotation, Is.EqualTo(270));
			Assert.That(settings.Common.Webcam.Ratio, Is.EqualTo("4:3"));
			Assert.That(settings.Common.TemperatureProfiles, Is.EqualTo(new[] { new TemperatureProfile("PLA", 215, 60, null) }));
		}
	}

	[Test]
	public void The_classic_webcam_plugin_wins_over_legacy_settings()
	{
		var settings = Parse("""
			{"webcam": {"webcamEnabled": true, "streamUrl": "/old"},
			 "plugins": {"classicwebcam": {"stream": "http://cam.local:8080/?action=stream", "flipH": true, "flipV": true}}}
			""");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(settings.Common.Webcam.StreamUrl, Is.EqualTo("http://cam.local:8080/?action=stream"));
			Assert.That(settings.Common.Webcam.FlipH && settings.Common.Webcam.FlipV, Is.True);
		}
	}

	[Test]
	public void A_disabled_webcam_has_no_stream()
	{
		var settings = Parse("""{"webcam": {"webcamEnabled": false, "streamUrl": "/webcam/?action=stream"}}""");
		Assert.That(settings.Common.Webcam.Enabled, Is.False);
	}

	[Test]
	public void Files_are_flattened_to_printable_ones()
	{
		var files = new List<PrintFile>();
		OctoPrintApi.CollectFiles(JsonDocument.Parse("""
			[{"type": "machinecode", "path": "a.gcode", "origin": "local", "date": 1},
			 {"type": "folder", "path": "f", "children": [{"type": "machinecode", "path": "f/b.gcode", "origin": "sdcard"}]},
			 {"type": "model", "path": "c.stl"}]
			""").RootElement, files);

		Assert.That(files.Select(f => (f.Id, f.Display)), Is.EqualTo(new[] { ("local:a.gcode", "a.gcode"), ("sdcard:f/b.gcode", "f/b.gcode (SD)") }));
	}
}

[TestFixture]
public sealed class OctoPrintUrlsTests
{
	[TestCase("octopi.local", "http://octopi.local/")]
	[TestCase(" 192.168.1.20:5000 ", "http://192.168.1.20:5000/")]
	[TestCase("https://print.example/octoprint", "https://print.example/octoprint/")]
	[TestCase("http://octopi.local/?x=1#y", "http://octopi.local/")]
	public void Addresses_users_paste_are_accepted(string input, string expected)
	{
		Assert.That(PrinterUrls.TryParse(input, out var uri), Is.True);
		Assert.That(uri.ToString(), Is.EqualTo(expected));
	}

	[TestCase("")]
	[TestCase("ftp://octopi.local")]
	[TestCase("http://")]
	public void Nonsense_is_refused(string input) =>
		Assert.That(PrinterUrls.TryParse(input, out _), Is.False);

	[Test]
	public void The_push_socket_keeps_a_path_prefix()
	{
		using (Assert.EnterMultipleScope())
		{
			Assert.That(OctoPrintUrls.PushSocket(new Uri("https://h/octoprint/")).ToString(), Is.EqualTo("wss://h/octoprint/sockjs/websocket"));
			Assert.That(OctoPrintUrls.PushSocket(new Uri("http://h:5000")).ToString(), Is.EqualTo("ws://h:5000/sockjs/websocket"));
		}
	}

	[Test]
	public void Gpio_outputs_keep_their_position_and_skip_invalid_pins()
	{
		using var json = JsonDocument.Parse("""
			{ "plugins": { "gpiocontrol": { "gpio_configurations": [
			  { "name": "Unset", "pin": -1 },
			  { "name": " Light ", "pin": "27" },
			  { "pin": 22 }
			] } } }
			""");

		Assert.That(
			OctoPrintApi.ParseGpioOutputs(json.RootElement),
			Is.EqualTo(new[] { new GpioOutput(1, 27, "Light"), new GpioOutput(2, 22, "GPIO22") }));
	}

	[Test]
	public void Settings_without_gpio_control_have_no_outputs()
	{
		using var json = JsonDocument.Parse("""{ "plugins": {} }""");
		Assert.That(OctoPrintApi.ParseGpioOutputs(json.RootElement), Is.Empty);
	}

	[Test]
	public void A_relative_stream_resolves_against_octoprint() =>
		Assert.That(
			PrinterUrls.ResolveStream(new Uri("http://octopi.local/"), "/webcam/?action=stream")?.ToString(),
			Is.EqualTo("http://octopi.local/webcam/?action=stream"));

	[Test]
	public void A_loopback_stream_is_reached_through_the_octoprint_host() =>
		Assert.That(
			PrinterUrls.ResolveStream(new Uri("http://octopi.local:5000/"), "http://127.0.0.1:8080/?action=stream")?.ToString(),
			Is.EqualTo("http://octopi.local:8080/?action=stream"));

	[Test]
	public void A_loopback_stream_stays_when_octoprint_runs_next_to_macro_deck() =>
		Assert.That(
			PrinterUrls.ResolveStream(new Uri("http://localhost:5000/"), "http://127.0.0.1:8080/?action=stream")?.ToString(),
			Is.EqualTo("http://127.0.0.1:8080/?action=stream"));

	[Test]
	public void A_stream_with_credentials_is_refused() =>
		Assert.That(PrinterUrls.ResolveStream(new Uri("http://octopi.local/"), "http://user:pw@cam.local/stream"), Is.Null);

	[Test]
	public void No_stream_resolves_to_nothing() =>
		Assert.That(PrinterUrls.ResolveStream(new Uri("http://octopi.local/"), " "), Is.Null);
}
