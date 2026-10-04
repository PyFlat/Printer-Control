using System.Text.Json;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using PrinterControl.Actions;
using PrinterControl.Backends;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests;

[TestFixture]
public sealed class EndToEndTests
{
	private FakeOctoPrint _server = null!;
	private PluginTestHarness _harness = null!;
	private string _printerKey = null!;

	internal static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder =>
		{
			builder.UseLocalization(Strings.LocalizationCatalog).RegisterIntegration<PrinterControlIntegration>();
			builder.Services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory());
			builder.Services.AddPrinterBackends();
		});

	[SetUp]
	public async Task Start()
	{
		_server = await FakeOctoPrint.StartAsync();
		_harness = CreateHarness();
		var entry = _harness.Context.Config.AddEntry("Prusa");
		_harness.Context.Config.SeedString(entry, ConfigKeys.Url, _server.BaseUri.ToString());
		_harness.Context.Config.SeedSecret(entry, ConfigKeys.ApiKey, FakeOctoPrint.ApiKey);
		_printerKey = entry.ToString("N");

		await _harness.InitializeIntegrationsAsync();
		Assert.That(await _server.AuthReceived.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo($"{FakeOctoPrint.User}:{FakeOctoPrint.Session}"));
	}

	[TearDown]
	public async Task Stop()
	{
		await _harness.DisposeAsync();
		await _server.DisposeAsync();
	}

	private PrinterConnection Printer => _harness.Services.GetRequiredService<PrinterRegistry>().Printers.Single();

	private async Task PushAndWaitAsync(string frame, Func<PrinterSnapshot, bool> reached)
	{
		await _server.PushAsync(frame);
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!reached(Printer.Snapshot))
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the pushed frame never reached the snapshot");
			await Task.Delay(20);
		}
	}

	private Task PrintingAsync(double completion = 50) => PushAndWaitAsync(
		FakeOctoPrint.CurrentFrame("Printing", FakeOctoPrint.Printing, completion, "benchy.gcode", 215, 215),
		s => s.Status == PrinterStatus.Printing && s.Completion == completion);

	private static string? Status(CapabilityInvocationOutcome outcome) =>
		outcome.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("status", out var status) ? status.ToString() : null;

	[Test]
	public async Task Pushed_state_reaches_the_variables()
	{
		await PrintingAsync(42);

		var outcome = await _harness.Variables.GetAsync($"p{_printerKey}-progress");

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(outcome.Data?.GetRawText(), Does.Contain("42"));
	}

	[Test]
	public async Task The_printer_variables_are_declared_eagerly()
	{
		var outcome = await _harness.Variables.DescribeAsync();

		var described = outcome.Data?.GetRawText() ?? string.Empty;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(described, Does.Contain("printer_prusa_progress"));
			Assert.That(described, Does.Contain("printer_prusa_bed_temp"));
		}
	}

	[Test]
	public async Task OctoPrint_events_become_macro_deck_events()
	{
		await PrintingAsync();
		await _server.PushAsync("""{"event": {"type": "PrintDone", "payload": {"name": "benchy.gcode", "time": 3600.4}}}""");
		await _server.PushAsync("""{"event": {"type": "PrintFailed", "payload": {"name": "benchy.gcode", "reason": "cancelled"}}}""");
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);

		var events = _harness.Context.Events.Published.Select(e => e.EventId).ToArray();
		using (Assert.EnterMultipleScope())
		{
			Assert.That(events, Does.Contain(PrinterControlIntegration.PrintDoneEvent));
			Assert.That(events, Does.Not.Contain(PrinterControlIntegration.PrintFailedEvent), "a cancel is not also a failure");
			Assert.That(events.Count(e => e == PrinterControlIntegration.PrinterEventEvent), Is.EqualTo(2));
			Assert.That(events, Does.Contain(PrinterControlIntegration.StatusChangedEvent));
		}
	}

	[Test]
	public async Task Pausing_is_confirmed_by_the_pushed_state()
	{
		await PrintingAsync();
		_server.OnCommand = command => command.Path == "/api/job"
			? [FakeOctoPrint.CurrentFrame("Paused", FakeOctoPrint.Paused, 50, "benchy.gcode")]
			: [];

		var outcome = await _harness.Actions.ExecuteAsync("job", new Dictionary<string, object?>
		{
			[JobAction.CommandParameter] = JobAction.TogglePause,
		});

		var sent = _server.Commands.Single(c => c.Path == "/api/job");
		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.True);
			Assert.That(Status(outcome), Is.Not.EqualTo("accepted").IgnoreCase);
			Assert.That(sent.Body?.GetProperty("command").GetString(), Is.EqualTo("pause"));
			Assert.That(sent.Body?.GetProperty("action").GetString(), Is.EqualTo("pause"));
		}
	}

	[Test]
	public async Task Preheating_sends_the_profile_targets()
	{
		await PrintingAsync();
		_server.OnCommand = command => command.Path == "/api/printer/bed"
			? [FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational, tool: 30, toolTarget: 240).Replace("\"target\": 60", "\"target\": 85")]
			: [];

		var outcome = await _harness.Actions.ExecuteAsync("preheat", new Dictionary<string, object?> { [PreheatAction.ProfileParameter] = "PETG" });

		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.True);
			Assert.That(
				_server.Commands.Single(c => c.Path == "/api/printer/tool").Body?.GetProperty("targets").GetProperty("tool0").GetDouble(),
				Is.EqualTo(240),
				"the extruder reaches OctoPrint as tool0");
			Assert.That(_server.Commands.Single(c => c.Path == "/api/printer/bed").Body?.GetRawText(), Does.Contain("85"));
		}
	}

	[Test]
	public async Task Jogging_is_sent_and_reported_as_accepted()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);

		var outcome = await _harness.Actions.ExecuteAsync("jog", new Dictionary<string, object?>
		{
			[JogAction.AxisParameter] = "z",
			[JogAction.DistanceParameter] = -0.5,
		});

		var jog = _server.Commands.Single(c => c.Path == "/api/printer/printhead").Body!.Value;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.True);
			Assert.That(jog.GetProperty("z").GetDouble(), Is.EqualTo(-0.5));
			Assert.That(jog.GetProperty("x").GetDouble(), Is.Zero);
		}
	}

	[Test]
	public async Task A_file_list_is_offered_newest_first()
	{
		var outcome = await _harness.Actions.GetOptionsAsync("print-file", PrintFileAction.FileParameter);

		var text = outcome.Data?.GetRawText() ?? string.Empty;
		Assert.That(text.IndexOf("local:parts/clip.gcode", StringComparison.Ordinal), Is.LessThan(text.IndexOf("local:benchy.gcode", StringComparison.Ordinal)).And.GreaterThan(-1));
	}

	[Test]
	public async Task Gpio_outputs_are_offered_by_pin_id()
	{
		var outcome = await _harness.Actions.GetOptionsAsync("switch-output", OutputAction.OutputParameter);

		var text = outcome.Data?.GetRawText() ?? string.Empty;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(text, Does.Contain("\"gpio27\"").And.Contain("Light"));
			Assert.That(text, Does.Contain("\"gpio17\"").And.Contain("Fan relay"));
		}
	}

	[Test]
	public async Task Toggling_a_light_switches_its_output_and_is_confirmed()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		var outcome = await _harness.Actions.ExecuteAsync("switch-output", new Dictionary<string, object?>
		{
			[OutputAction.OutputParameter] = "gpio27",
			[OutputAction.StateParameter] = "toggle",
		});

		var sent = _server.Commands.Single(c => c.Path == "/api/plugin/gpiocontrol").Body!.Value;
		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.True);
			Assert.That(Status(outcome), Is.Not.EqualTo("accepted"));
			Assert.That(sent.GetProperty("command").GetString(), Is.EqualTo("turnGpioOn"));
			Assert.That(sent.GetProperty("id").GetInt32(), Is.EqualTo(1));
			Assert.That(_server.GpioStates, Is.EqualTo((string[])["off", "on"]));
		}
	}

	[Test]
	public async Task Turning_off_an_output_that_is_on_sends_off()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		_server.GpioStates = ["on", "on"];

		await _harness.Actions.ExecuteAsync("switch-output", new Dictionary<string, object?>
		{
			[OutputAction.OutputParameter] = "gpio17",
			[OutputAction.StateParameter] = "toggle",
		});

		Assert.That(_server.GpioStates, Is.EqualTo((string[])["off", "on"]));
	}

	private static string? ActiveState(CapabilityInvocationOutcome outcome) =>
		outcome.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("activeStateId", out var active) ? active.GetString() : null;

	[Test]
	public async Task A_light_provides_its_on_off_state_to_the_button()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		var light = new Dictionary<string, object?> { [OutputAction.OutputParameter] = "gpio27", [OutputAction.StateParameter] = "toggle" };

		var before = await _harness.Actions.GetActionStateAsync("switch-output", light);
		var switched = await _harness.Actions.ExecuteAsync("switch-output", light);
		var after = await _harness.Actions.GetActionStateAsync("switch-output", light);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(ActiveState(before), Is.EqualTo("off"));
			Assert.That(switched.Data?.GetProperty("expectedStateId").GetString(), Is.EqualTo("on"), "names the state it expects next");
			Assert.That(ActiveState(after), Is.EqualTo("on"));
		}
	}

	[Test]
	public async Task An_output_without_a_choice_yet_reports_no_state()
	{
		var outcome = await _harness.Actions.GetActionStateAsync("switch-output", new Dictionary<string, object?>());

		Assert.That(ActiveState(outcome), Is.Null);
	}

	[Test]
	public async Task The_job_action_provides_the_print_state()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		var idle = await _harness.Actions.GetActionStateAsync("job", new Dictionary<string, object?>());
		await PrintingAsync();
		var printing = await _harness.Actions.GetActionStateAsync("job", new Dictionary<string, object?>());

		using (Assert.EnterMultipleScope())
		{
			Assert.That(ActiveState(idle), Is.EqualTo("idle"));
			Assert.That(ActiveState(printing), Is.EqualTo("printing"));
		}
	}

	[Test]
	public async Task The_connection_action_provides_the_connection_state()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);

		var outcome = await _harness.Actions.GetActionStateAsync("connection", new Dictionary<string, object?>());

		Assert.That(ActiveState(outcome), Is.EqualTo("connected"));
	}

	[Test]
	public async Task Gpio_outputs_are_declared_as_variables()
	{
		var described = (await _harness.Variables.DescribeAsync()).Data?.GetRawText() ?? string.Empty;

		Assert.That(described, Does.Contain("printer_prusa_output_light").And.Contain("printer_prusa_output_fan_relay"));
	}

	[Test]
	public async Task Switching_a_light_shows_in_its_variable_right_away()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);

		await _harness.Actions.ExecuteAsync("switch-output", new Dictionary<string, object?>
		{
			[OutputAction.OutputParameter] = "gpio27",
			[OutputAction.StateParameter] = "on",
		});

		var outcome = await _harness.Variables.GetAsync($"p{_printerKey}-output-gpio27");
		Assert.That(outcome.Data?.GetRawText(), Does.Contain("true"));
	}

	// A light switched in OctoPrint's own sidebar shows up through the poll.
	[Test]
	public async Task A_light_switched_elsewhere_is_picked_up()
	{
		_server.GpioStates = ["off", "on"];

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!Printer.OutputStates.GetValueOrDefault("gpio27"))
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the GPIO state was never read");
			await Task.Delay(50);
		}

		var outcome = await _harness.Variables.GetAsync($"p{_printerKey}-output-gpio27");
		Assert.That(outcome.Data?.GetRawText(), Does.Contain("true"));
	}

	[Test]
	public async Task A_missing_gpio_plugin_fails_without_sending()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		_server.GpioStates = null;

		var outcome = await _harness.Actions.ExecuteAsync("switch-output", new Dictionary<string, object?>
		{
			[OutputAction.OutputParameter] = "gpio27",
			[OutputAction.StateParameter] = "on",
		});

		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.False);
			Assert.That(_server.Commands, Is.Empty);
		}
	}

	[Test]
	public async Task An_output_no_longer_set_up_fails_without_sending()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		var outcome = await _harness.Actions.ExecuteAsync("switch-output", new Dictionary<string, object?>
		{
			[OutputAction.OutputParameter] = "gpio4",
			[OutputAction.StateParameter] = "on",
		});

		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.False);
			Assert.That(_server.Commands, Is.Empty);
		}
	}

	[Test]
	public async Task An_invalid_parameter_fails_instead_of_sending()
	{
		await PushAndWaitAsync(FakeOctoPrint.CurrentFrame("Operational", FakeOctoPrint.Operational), s => s.Status == PrinterStatus.Operational);
		var outcome = await _harness.Actions.ExecuteAsync("extrude", new Dictionary<string, object?> { [ExtrudeAction.AmountParameter] = 0 });

		using (Assert.EnterMultipleScope())
		{
			Assert.That(outcome.Succeeded, Is.False);
			Assert.That(_server.Commands, Is.Empty);
		}
	}
}

[TestFixture]
public sealed class UnconfiguredPluginTests
{
	[Test]
	public async Task The_plugin_builds_and_initializes_without_printers()
	{
		await using var harness = EndToEndTests.CreateHarness();

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task Actions_fail_as_not_configured()
	{
		await using var harness = EndToEndTests.CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("job", new Dictionary<string, object?> { [JobAction.CommandParameter] = JobAction.Pause });

		Assert.That(outcome.Succeeded, Is.False);
	}
}
