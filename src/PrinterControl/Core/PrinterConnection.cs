using Serilog;

namespace PrinterControl.Core;

internal sealed class PrinterSnapshotChangedEventArgs(PrinterSnapshot previous, PrinterSnapshot current) : EventArgs
{
	public PrinterSnapshot Previous { get; } = previous;

	public PrinterSnapshot Current { get; } = current;
}

internal sealed class PrinterEventReceivedArgs(PrinterEvent received) : EventArgs
{
	public PrinterEvent Event { get; } = received;
}

// Holds the state everything else reads and reconnects with backoff (2 s doubling to 30 s). A backend
// runs the session and implements the feature interfaces its server supports.
internal abstract class PrinterConnection : IAsyncDisposable
{
	private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(30);
	private static readonly IReadOnlyDictionary<string, bool> NoOutputs = new Dictionary<string, bool>();

	private readonly CancellationTokenSource _stop = new();
	private Task? _loop;
	private PrinterSnapshot _snapshot = PrinterSnapshot.Offline;
	private PrinterSettings _settings = PrinterSettings.Empty;
	private IReadOnlyDictionary<string, bool> _outputStates = NoOutputs;
	private string? _lastFailure;

	protected PrinterConnection(PrinterConfig config, ILogger logger)
	{
		Config = config;
		Logger = logger.ForContext(GetType());
	}

	public event EventHandler<PrinterSnapshotChangedEventArgs>? SnapshotChanged;

	public event EventHandler<PrinterEventReceivedArgs>? EventReceived;

	public event EventHandler? SettingsChanged;

	public PrinterConfig Config { get; }

	public PrinterSnapshot Snapshot => Volatile.Read(ref _snapshot);

	public PrinterSettings Settings => Volatile.Read(ref _settings);

	// On or off per output id; an output missing here is unknown. Empty while offline.
	public IReadOnlyDictionary<string, bool> OutputStates => Volatile.Read(ref _outputStates);

	// The address set up with the printer wins over the server's own webcam settings.
	public Uri? WebcamStream => PrinterUrls.ResolveStream(
		Config.BaseUri,
		Config.WebcamUrl ?? (Settings.Webcam is { Enabled: true, StreamUrl: { Length: > 0 } stream } ? stream : null));

	protected ILogger Logger { get; }

	public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

	// Connects and returns only once the connection dropped. Throwing counts as a failed attempt.
	protected abstract Task RunSessionAsync(CancellationToken cancellationToken);

	protected PrinterException NotSupported(string what) =>
		new(PrinterFailure.NotSupported, $"{Config.DisplayName} does not support {what}.");

	protected void PublishSnapshot(PrinterSnapshot next)
	{
		var previous = Interlocked.Exchange(ref _snapshot, next);
		if (!Equals(previous, next))
		{
			SnapshotChanged?.Invoke(this, new PrinterSnapshotChangedEventArgs(previous, next));
		}
	}

	protected void PublishSettings(PrinterSettings settings)
	{
		var previous = Interlocked.Exchange(ref _settings, settings);
		if (!previous.SameAs(settings))
		{
			SettingsChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	protected void PublishEvent(PrinterEvent received) => EventReceived?.Invoke(this, new PrinterEventReceivedArgs(received));

	protected void PublishOutputStates(IReadOnlyDictionary<string, bool> states) => Volatile.Write(ref _outputStates, states);

	protected void ReportConnected()
	{
		if (_lastFailure is not null)
		{
			Logger.Information("Reconnected to {Printer}.", Config.DisplayName);
		}
		else
		{
			Logger.Information("Connected to {Printer} at {Url}.", Config.DisplayName, Config.BaseUri);
		}

		_lastFailure = null;
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		var retry = MinRetry;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await RunSessionAsync(cancellationToken);
				retry = MinRetry;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				ReportFailure(exception);
			}

			PublishOutputStates(NoOutputs);
			PublishSnapshot(PrinterSnapshot.Offline with { Temperatures = Snapshot.Temperatures });
			try
			{
				await Task.Delay(retry, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
		}
	}

	// One warning per kind of failure, so an unplugged Raspberry Pi does not flood the log every retry.
	private void ReportFailure(Exception exception)
	{
		var kind = exception is PrinterException printer ? printer.Failure.ToString() : exception.GetType().Name;
		if (kind == _lastFailure)
		{
			Logger.Debug("{Printer} still unavailable: {Message}", Config.DisplayName, exception.Message);
			return;
		}

		_lastFailure = kind;
		Logger.Warning("{Printer} is unavailable ({Kind}): {Message}", Config.DisplayName, kind, exception.Message);
	}

	public virtual async ValueTask DisposeAsync()
	{
		await _stop.CancelAsync();
		if (_loop is { } loop)
		{
			try
			{
				await loop;
			}
			catch (OperationCanceledException)
			{
			}
		}

		_stop.Dispose();
		GC.SuppressFinalize(this);
	}
}
