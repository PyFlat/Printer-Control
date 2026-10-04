using MacroDeck.Sdk.VideoStreams;
using PrinterControl.Core;
using PrinterControl.VideoStreams;

namespace PrinterControl;

internal sealed partial class PrinterControlIntegration : IVideoStreamIntegration
{
	private IVideoStreamProviderContext? _videoContext;

	// Runs after InitializeAsync, and again after every re-initialization, which withdraws the provider first.
	async Task IVideoStreamIntegration.InitializeAsync(IVideoStreamProviderContext context, CancellationToken cancellationToken)
	{
		_videoContext = context;
		_registry.PrintersChanged -= OnWebcamsChanged;
		_registry.PrintersChanged += OnWebcamsChanged;
		_registry.SettingsChanged -= OnWebcamSettingsChanged;
		_registry.SettingsChanged += OnWebcamSettingsChanged;
		_registry.SnapshotChanged -= OnWebcamPrinterOnline;
		_registry.SnapshotChanged += OnWebcamPrinterOnline;

		var registration = await context.RegisterProviderAsync(new WebcamStreamProvider(_registry), cancellationToken);

		// An older Macro Deck without video streams returns an empty registration.
		WebcamProviderId = registration.QualifiedId;
		if (string.IsNullOrEmpty(registration.QualifiedId))
		{
			_logger.Information("This Macro Deck does not support video streams; the webcam widget stays empty.");
		}
		else
		{
			_logger.Information("Registered the webcam provider as {ProviderId}.", registration.QualifiedId);
		}
	}

	partial void ShutdownVideoStreams()
	{
		_registry.PrintersChanged -= OnWebcamsChanged;
		_registry.SettingsChanged -= OnWebcamSettingsChanged;
		_registry.SnapshotChanged -= OnWebcamPrinterOnline;
		_videoContext = null;
	}

	private void OnWebcamsChanged(object? sender, EventArgs e) => NotifyWebcams();

	private void OnWebcamSettingsChanged(object? sender, PrinterEventArgs<EventArgs> e) => NotifyWebcams();

	// Stream state follows the server coming and going, not every progress frame.
	private void OnWebcamPrinterOnline(object? sender, PrinterEventArgs<PrinterSnapshotChangedEventArgs> e)
	{
		if (e.Data.Previous.Online != e.Data.Current.Online)
		{
			NotifyWebcams();
		}
	}

	private void NotifyWebcams()
	{
		if (_videoContext is not { } context)
		{
			return;
		}

		_ = NotifyWebcamsAsync(context);
	}

	private async Task NotifyWebcamsAsync(IVideoStreamProviderContext context)
	{
		try
		{
			await context.NotifyStreamsChangedAsync(WebcamStreamProvider.ProviderId);
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "Telling Macro Deck the webcams changed failed.");
		}
	}
}
