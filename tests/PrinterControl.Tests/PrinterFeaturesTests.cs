using NUnit.Framework;
using PrinterControl.Actions;
using PrinterControl.Core;

namespace PrinterControl.Tests;

[TestFixture]
public sealed class PrinterFeaturesTests
{
	// A backend that only sends G-code, so motion runs on the defaults.
	private sealed class GcodeOnlyConnection() : PrinterConnection(
		new PrinterConfig(Guid.NewGuid(), "test", "Test", new Uri("http://printer.local/"), null, null),
		Serilog.Core.Logger.None), IMotionControl
	{
		public List<string> Sent { get; } = [];

		protected override Task RunSessionAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

		public Task SendGcodeAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken)
		{
			Sent.AddRange(commands);
			return Task.CompletedTask;
		}
	}

	private static async Task<IReadOnlyList<string>> SentBy(Func<IMotionControl, Task> command)
	{
		await using var printer = new GcodeOnlyConnection();
		await command(printer);
		return printer.Sent;
	}

	[Test]
	public async Task Homing_sends_G28_with_the_axes() =>
		Assert.That(await SentBy(m => m.HomeAsync(["x", "z"], CancellationToken.None)), Is.EqualTo((string[])["G28 X Z"]));

	[Test]
	public async Task Jogging_moves_relative_and_switches_back_to_absolute() =>
		Assert.That(
			await SentBy(m => m.JogAsync(0, -2.5, 0, 3000, CancellationToken.None)),
			Is.EqualTo((string[])["G91", "G1 Y-2.5 F3000", "G90"]));

	[Test]
	public async Task Extruding_uses_relative_extrusion() =>
		Assert.That(
			await SentBy(m => m.ExtrudeAsync(5, CancellationToken.None)),
			Is.EqualTo((string[])["M83", "G1 E5 F300", "M82"]));

	[Test]
	public async Task Rates_are_M220_and_M221()
	{
		using (Assert.EnterMultipleScope())
		{
			Assert.That(await SentBy(m => m.SetFeedrateAsync(120, CancellationToken.None)), Is.EqualTo((string[])["M220 S120"]));
			Assert.That(await SentBy(m => m.SetFlowrateAsync(95, CancellationToken.None)), Is.EqualTo((string[])["M221 S95"]));
		}
	}

	[Test]
	public async Task A_missing_feature_is_reported_as_not_supported()
	{
		await using var printer = new GcodeOnlyConnection();

		var failure = Assert.Throws<PrinterException>(() => PrinterAction.Require<IOutputControl>(printer));

		Assert.That(failure.Failure, Is.EqualTo(PrinterFailure.NotSupported));
	}
}
