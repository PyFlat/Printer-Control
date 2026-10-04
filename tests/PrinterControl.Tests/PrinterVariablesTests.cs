using MacroDeck.Sdk.Variables;
using NUnit.Framework;
using PrinterControl.Core;
using PrinterControl.Variables;

namespace PrinterControl.Tests;

[TestFixture]
public sealed class PrinterVariablesTests
{
	private static PrinterConfig Printer(string title, string guid = "0f8fad5b-d9cb-469f-a165-70867728950e") =>
		new(Guid.Parse(guid), "octoprint", title, new Uri("http://octopi.local/"), "key", null);

	[Test]
	public void Every_local_id_resolves_back_to_its_printer_and_field()
	{
		var printer = Printer("Prusa");
		foreach (var (localId, field) in PrinterVariables.FieldsFor(printer))
		{
			Assert.That(PrinterVariables.TryResolve(localId, out var key, out var resolved), Is.True, localId);
			Assert.That((key, resolved), Is.EqualTo((printer.Key, field)));
		}
	}

	// Pinned: these ids and names are persisted in users' profiles and templates.
	[Test]
	public void Ids_and_names_are_stable()
	{
		var definitions = PrinterVariables.Build([Printer("Prusa MK3S+")]);
		var progress = definitions.Single(d => d.Name == "printer_prusa_mk3s_progress");

		using (Assert.EnterMultipleScope())
		{
			Assert.That(progress.Id, Is.EqualTo("p0f8fad5bd9cb469fa16570867728950e-progress"));
			Assert.That(definitions.Select(d => d.Name), Does.Contain("printer_prusa_mk3s_time_left"));
			Assert.That(definitions.Select(d => d.Id), Does.Contain("p0f8fad5bd9cb469fa16570867728950e-time-left"));
			Assert.That(definitions, Has.All.Matches<VariableDefinition>(d => d.Materialization == VariableMaterialization.Eager));
			Assert.That(definitions, Has.All.Matches<VariableDefinition>(d => d.Configuration!.Key == Printer("x").Key));
		}
	}

	[Test]
	public void Templates_name_the_printer_placeholder()
	{
		var templates = PrinterVariables.Templates();

		using (Assert.EnterMultipleScope())
		{
			Assert.That(templates, Has.Count.EqualTo(PrinterVariables.FieldsPerPrinter));
			Assert.That(templates.Select(t => t.Name), Has.All.Matches<string>(VariableNameTemplate.IsTemplate));
		}
	}

	[Test]
	public void Temperatures_are_whole_degrees()
	{
		var definition = PrinterVariables.Build([Printer("Prusa")]).Single(d => d.Name == "printer_prusa_tool_temp");

		Assert.That(definition.DecimalPlaces, Is.Zero);
	}

	[Test]
	public void Printers_with_the_same_name_get_distinct_variable_names()
	{
		var names = PrinterVariables.Build(
		[
			Printer("Ender", "11111111-1111-1111-1111-111111111111"),
			Printer("Ender", "22222222-2222-2222-2222-222222222222"),
		]).Select(d => d.Name).ToHashSet();

		Assert.That(names, Does.Contain("printer_ender_status").And.Contain("printer_ender_2_status"));
	}

	// Pinned like the fields: the id follows the output's id, the name the output's name.
	[Test]
	public void Outputs_get_one_boolean_each()
	{
		IReadOnlyList<PrinterOutput> outputs = [new("gpio27", "Light"), new("gpio17", "Light"), new("gpio27", "Same id")];
		var gpio = PrinterVariables.Build([Printer("Prusa")], _ => outputs).Where(d => d.Name!.Contains("_output_", StringComparison.Ordinal)).ToList();

		using (Assert.EnterMultipleScope())
		{
			Assert.That(gpio.Select(d => (d.Id, d.Name)), Is.EqualTo(new[]
			{
				("p0f8fad5bd9cb469fa16570867728950e-output-gpio27", "printer_prusa_output_light"),
				("p0f8fad5bd9cb469fa16570867728950e-output-gpio17", "printer_prusa_output_light_2"),
			}));
			Assert.That(gpio, Has.All.Matches<VariableDefinition>(d => d.Type == VariableType.Boolean));
			Assert.That(PrinterVariables.TryResolveOutput(gpio[0].Id!, out var key, out var output), Is.True);
			Assert.That((key, output), Is.EqualTo((Printer("x").Key, "gpio27")));
			Assert.That(PrinterVariables.TryResolve(gpio[0].Id!, out _, out _), Is.False);
		}
	}

	[TestCase("p0f8fad5bd9cb469fa16570867728950e-output-")]
	[TestCase("p0f8fad5bd9cb469fa16570867728950e-progress")]
	public void Other_ids_do_not_resolve_as_outputs(string localId) =>
		Assert.That(PrinterVariables.TryResolveOutput(localId, out _, out _), Is.False);

	[TestCase("garbage")]
	[TestCase("p0f8fad5bd9cb469fa16570867728950e-nope")]
	[TestCase("x0f8fad5bd9cb469fa16570867728950e-progress")]
	public void Unknown_ids_do_not_resolve(string localId) =>
		Assert.That(PrinterVariables.TryResolve(localId, out _, out _), Is.False);

	[Test]
	public void Values_come_from_the_snapshot()
	{
		var snapshot = new PrinterSnapshot
		{
			Online = true,
			Status = PrinterStatus.Printing,
			Completion = 12.345,
			PrintTimeLeft = 600,
			Temperatures = new Dictionary<string, Temperature> { [HeaterIds.Extruder] = new(214.56, 215) },
		};

		using (Assert.EnterMultipleScope())
		{
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.Status, snapshot).Value, Is.EqualTo("printing"));
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.Progress, snapshot).Value, Is.EqualTo(12.3));
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.TimeLeft, snapshot).Value, Is.EqualTo(600.0));
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.ToolTemp, snapshot).Value, Is.EqualTo(215));
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.BedTemp, snapshot).Value, Is.Null);
			Assert.That(PrinterVariables.Eta(snapshot, new DateTimeOffset(2026, 1, 1, 13, 55, 0, TimeSpan.Zero)), Is.EqualTo("14:05"));
		}
	}

	[Test]
	public void An_unknown_printer_reads_as_offline()
	{
		using (Assert.EnterMultipleScope())
		{
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.Online, null).Value, Is.EqualTo(false));
			Assert.That(PrinterVariables.Read(PrinterVariables.Field.Progress, null).Value, Is.Null);
		}
	}
}
