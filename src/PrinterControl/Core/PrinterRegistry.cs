using PrinterControl.Backends;
using Serilog;

namespace PrinterControl.Core;

internal sealed class PrinterEventArgs<T>(PrinterConnection printer, T data) : EventArgs
{
	public PrinterConnection Printer { get; } = printer;

	public T Data { get; } = data;
}

// The configured printers. Replaced as a whole from the config entries on every initialization; a
// printer whose configuration did not change keeps its connection.
internal sealed class PrinterRegistry(PrinterBackends backends, IHttpClientFactory httpClients, ILogger logger) : IAsyncDisposable
{
	public const string HttpClientName = "printers";

	private readonly ILogger _logger = logger.ForContext<PrinterRegistry>();
	private readonly SemaphoreSlim _gate = new(1, 1);
	private volatile List<PrinterConnection> _printers = [];

	public event EventHandler<PrinterEventArgs<PrinterSnapshotChangedEventArgs>>? SnapshotChanged;

	public event EventHandler<PrinterEventArgs<PrinterEvent>>? EventReceived;

	public event EventHandler<PrinterEventArgs<EventArgs>>? SettingsChanged;

	public event EventHandler? PrintersChanged;

	public IReadOnlyList<PrinterConnection> Printers => _printers;

	public PrinterConnection? Find(string? key) =>
		string.IsNullOrEmpty(key) ? null : _printers.FirstOrDefault(p => p.Config.Key == key);

	// Actions and widgets name a printer; with exactly one configured the choice may be left empty.
	public PrinterConnection? Resolve(string? key) =>
		string.IsNullOrEmpty(key) && _printers.Count == 1 ? _printers[0] : Find(key);

	public async Task ApplyAsync(IReadOnlyList<PrinterConfig> configs, bool connect = true)
	{
		List<PrinterConnection> removed;
		await _gate.WaitAsync();
		try
		{
			var current = _printers.ToDictionary(p => p.Config.EntryId);
			var next = new List<PrinterConnection>(configs.Count);
			foreach (var config in configs)
			{
				if (current.Remove(config.EntryId, out var existing) && existing.Config == config)
				{
					next.Add(existing);
					continue;
				}

				if (existing is not null)
				{
					current[config.EntryId] = existing;
				}

				if (backends.Find(config.Backend) is not { } backend)
				{
					_logger.Warning("Skipping printer {Printer}: no backend called {Backend}.", config.DisplayName, config.Backend);
					continue;
				}

				var connection = backend.Connect(config, httpClients.CreateClient(HttpClientName), _logger);
				Subscribe(connection);
				next.Add(connection);
			}

			removed = [.. current.Values];
			foreach (var connection in removed)
			{
				Unsubscribe(connection);
			}

			var changed = removed.Count > 0 || next.Count != _printers.Count || next.Where((p, i) => !ReferenceEquals(p, _printers[i])).Any();
			_printers = next;
			if (connect)
			{
				foreach (var connection in next)
				{
					connection.Start();
				}
			}

			if (changed)
			{
				_logger.Information("Printers: {Printers}.", string.Join(", ", next.Select(p => p.Config.DisplayName)));
				PrintersChanged?.Invoke(this, EventArgs.Empty);
			}
		}
		finally
		{
			_gate.Release();
		}

		foreach (var connection in removed)
		{
			await connection.DisposeAsync();
		}
	}

	private void Subscribe(PrinterConnection connection)
	{
		connection.SnapshotChanged += OnSnapshotChanged;
		connection.EventReceived += OnEventReceived;
		connection.SettingsChanged += OnSettingsChanged;
	}

	private void Unsubscribe(PrinterConnection connection)
	{
		connection.SnapshotChanged -= OnSnapshotChanged;
		connection.EventReceived -= OnEventReceived;
		connection.SettingsChanged -= OnSettingsChanged;
	}

	private void OnSnapshotChanged(object? sender, PrinterSnapshotChangedEventArgs e) =>
		SnapshotChanged?.Invoke(this, new PrinterEventArgs<PrinterSnapshotChangedEventArgs>((PrinterConnection)sender!, e));

	private void OnEventReceived(object? sender, PrinterEventReceivedArgs e) =>
		EventReceived?.Invoke(this, new PrinterEventArgs<PrinterEvent>((PrinterConnection)sender!, e.Event));

	private void OnSettingsChanged(object? sender, EventArgs e) =>
		SettingsChanged?.Invoke(this, new PrinterEventArgs<EventArgs>((PrinterConnection)sender!, e));

	public async ValueTask DisposeAsync()
	{
		var printers = _printers;
		_printers = [];
		foreach (var connection in printers)
		{
			Unsubscribe(connection);
			await connection.DisposeAsync();
		}

		_gate.Dispose();
	}
}
