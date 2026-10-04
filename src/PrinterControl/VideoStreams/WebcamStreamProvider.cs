using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.VideoStreams;
using PrinterControl.Core;

namespace PrinterControl.VideoStreams;

// Macro Deck is pointed straight at the MJPEG stream (mjpg-streamer, camera-streamer, crowsnest) and
// relays it, so no printer credential is handed out.
internal sealed class WebcamStreamProvider(PrinterRegistry registry) : IVideoStreamProvider
{
	public const string ProviderId = "webcams";
	public const string Transport = "mjpeg";

	public string Id => ProviderId;

	public LocalizedText Name => Strings.Video.ProviderName();

	public LocalizedText? Description => Strings.Video.ProviderDescription();

	public Task<IReadOnlyList<VideoStreamDescriptor>> GetStreamsAsync(CancellationToken cancellationToken) =>
		Task.FromResult<IReadOnlyList<VideoStreamDescriptor>>([.. registry.Printers.Select(Describe)]);

	internal static VideoStreamDescriptor Describe(PrinterConnection printer)
	{
		var webcam = printer.Settings.Webcam;
		var (width, height) = Size(webcam.Ratio, webcam.Rotation % 180 != 0);
		var state = printer.WebcamStream is null
			? (printer.Snapshot.Online ? VideoStreamState.Unavailable : VideoStreamState.Disconnected)
			: VideoStreamState.Connected;

		return new VideoStreamDescriptor(
			printer.Config.Key,
			LocalizedText.FromLiteral(printer.Config.DisplayName),
			Width: width,
			Height: height,
			State: state);
	}

	public Task<VideoStreamSessionDescription> OpenAsync(VideoStreamOpenRequest request, CancellationToken cancellationToken)
	{
		if (!request.AcceptedTransports.Contains(Transport, StringComparer.OrdinalIgnoreCase))
		{
			throw new VideoStreamException(VideoStreamErrorCode.TransportNotAccepted, "Only MJPEG webcams are served.");
		}

		var printer = registry.Find(request.StreamId)
			?? throw new VideoStreamException(VideoStreamErrorCode.UnknownStream, $"No printer with key {request.StreamId}.");

		var url = printer.WebcamStream
			?? throw new VideoStreamException(VideoStreamErrorCode.StreamUnavailable, $"The webcam of {printer.Config.DisplayName} has no usable address.");

		return Task.FromResult(VideoStreamSessionDescription.Mjpeg(url.ToString()));
	}

	// The consumer talks to the webcam directly, so a session holds nothing on this side.
	public Task CloseAsync(string sessionId, VideoStreamSessionReason reason, CancellationToken cancellationToken) =>
		Task.CompletedTask;

	// Reserves the aspect ratio before the first frame; servers report "16:9" or "4:3".
	private static (int Width, int Height) Size(string? ratio, bool rotated)
	{
		var (width, height) = ratio?.Split(':') is [var w, var h]
			&& int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rw)
			&& int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rh)
			&& rw > 0 && rh > 0
				? (rw * 80, rh * 80)
				: (1280, 720);

		return rotated ? (height, width) : (width, height);
	}
}
