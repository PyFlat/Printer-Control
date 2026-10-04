using System.Text.Json;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk;
using MacroDeck.Sdk.VideoStreams;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using PrinterControl.Tests.Support;
using PrinterControl.Ui;
using PrinterControl.VideoStreams;

namespace PrinterControl.Tests.VideoStreams;

// The harness never runs IVideoStreamIntegration.InitializeAsync, so its provider cannot open a session:
// registration is checked against the fake context, sessions against the provider directly.
[TestFixture]
public sealed class WebcamStreamTests
{
	private FakeOctoPrint _server = null!;
	private PluginTestHarness _harness = null!;
	private string _printerKey = null!;

	[SetUp]
	public async Task Start()
	{
		_server = await FakeOctoPrint.StartAsync();
		_harness = EndToEndTests.CreateHarness();
		var entry = _harness.Context.Config.AddEntry("Prusa");
		_harness.Context.Config.SeedString(entry, ConfigKeys.Url, _server.BaseUri.ToString());
		_harness.Context.Config.SeedSecret(entry, ConfigKeys.ApiKey, FakeOctoPrint.ApiKey);
		_printerKey = entry.ToString("N");

		await _harness.InitializeIntegrationsAsync();
		await _server.AuthReceived.WaitAsync(TimeSpan.FromSeconds(10));

		var integration = (IVideoStreamIntegration)_harness.Services.GetServices<IPluginIntegration>().Single();
		await integration.InitializeAsync(_harness.Context.VideoStreams);
	}

	[TearDown]
	public async Task Stop()
	{
		await _harness.DisposeAsync();
		await _server.DisposeAsync();
	}

	private WebcamStreamProvider Provider => new(_harness.Services.GetRequiredService<PrinterRegistry>());

	private static VideoStreamOpenRequest Request(string streamId, params string[] transports) =>
		new(Guid.NewGuid().ToString("N"), streamId, transports.Length == 0 ? ["hls", "mjpeg"] : transports);

	[Test]
	public void The_webcam_provider_is_registered() =>
		Assert.That(_harness.Context.VideoStreams.Providers.ContainsKey(WebcamStreamProvider.ProviderId), Is.True);

	[Test]
	public async Task Each_printer_is_listed_as_a_connected_stream()
	{
		var stream = (await Provider.GetStreamsAsync(CancellationToken.None)).Single();

		using (Assert.EnterMultipleScope())
		{
			Assert.That(stream.Id, Is.EqualTo(_printerKey));
			Assert.That(stream.State, Is.EqualTo(VideoStreamState.Connected));
			Assert.That((stream.Width, stream.Height), Is.EqualTo((1280, 720)));
		}
	}

	[Test]
	public async Task Macro_deck_is_pointed_at_the_mjpeg_stream()
	{
		var description = await Provider.OpenAsync(Request(_printerKey), CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(description.Transport, Is.EqualTo("mjpeg"));
			Assert.That(description.Url, Is.EqualTo(new Uri(_server.BaseUri, "/webcam/?action=stream").ToString()));
		}
	}

	[Test]
	public void A_consumer_without_mjpeg_is_refused() =>
		AssertRefused(Request(_printerKey, "hls"), VideoStreamErrorCode.TransportNotAccepted);

	[Test]
	public void An_unknown_printer_is_refused() =>
		AssertRefused(Request("nope"), VideoStreamErrorCode.UnknownStream);

	private void AssertRefused(VideoStreamOpenRequest request, VideoStreamErrorCode expected)
	{
		var refused = Assert.ThrowsAsync<VideoStreamException>(() => Provider.OpenAsync(request, CancellationToken.None));
		Assert.That(refused!.ErrorCode, Is.EqualTo(expected));
	}

	[Test]
	public void A_progress_update_does_not_touch_the_video_node()
	{
		var surface = new UiSurface
		{
			Kind = UiSurfaceKinds.Widget,
			SessionMode = UiSessionModes.Shared,
			Attributes = new Dictionary<string, JsonElement>(),
		};
		var state = new UiState<WidgetModel>(WidgetSamples.Printing with { PrinterKey = _printerKey });

		var view = new UiView(surface, WebcamWidgetView.Build(state, 16, "com.pyflat.printer-control::webcams"));
		view.DrainPatches();
		state.Set(state.Value with { Snapshot = state.Value.Snapshot with { Completion = 70 } });

		var patches = view.DrainPatches();
		using (Assert.EnterMultipleScope())
		{
			Assert.That(patches, Is.Not.Empty, "the overlay should follow the progress");
			Assert.That(patches.Select(p => JsonSerializer.Serialize(p)), Has.None.Contains("video-stream"));
		}
	}
}
