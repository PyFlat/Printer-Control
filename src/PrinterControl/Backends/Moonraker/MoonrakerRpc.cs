using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

// JSON-RPC 2.0 over /websocket. The API key goes into the handshake, which authenticates the whole socket.
internal sealed class MoonrakerRpc : IAsyncDisposable
{
	private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

	// Bounds what a misbehaving server can make the plugin buffer; a large file list still fits.
	private const int MaxMessageBytes = 16 * 1024 * 1024;

	private readonly ClientWebSocket _socket;
	private readonly Action<string, JsonElement> _onNotification;
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly SemaphoreSlim _sending = new(1, 1);
	private readonly CancellationTokenSource _stop = new();
	private int _nextId;

	private MoonrakerRpc(ClientWebSocket socket, Action<string, JsonElement> onNotification)
	{
		_socket = socket;
		_onNotification = onNotification;
		Completion = Task.Run(() => ReceiveAsync(_stop.Token));
	}

	// Ends when the socket closes; every call still waiting then fails as unreachable.
	public Task Completion { get; }

	public static async Task<MoonrakerRpc> ConnectAsync(
		Uri baseUri,
		string? apiKey,
		Action<string, JsonElement> onNotification,
		CancellationToken cancellationToken)
	{
		var socket = new ClientWebSocket();
		socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
		socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
		if (!string.IsNullOrEmpty(apiKey))
		{
			socket.Options.SetRequestHeader("X-Api-Key", apiKey);
		}

		try
		{
			await socket.ConnectAsync(MoonrakerUrls.Websocket(baseUri), cancellationToken);
		}
		catch (WebSocketException exception)
		{
			socket.Dispose();
			throw new PrinterException(PrinterFailure.Unreachable, $"Could not open Moonraker's websocket at {baseUri}: {exception.Message}", exception);
		}

		return new MoonrakerRpc(socket, onNotification);
	}

	public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
	{
		var (id, answer) = await SendAsync(method, parameters, cancellationToken);
		try
		{
			return await answer.WaitAsync(CallTimeout, cancellationToken);
		}
		catch (TimeoutException exception)
		{
			throw new PrinterException(PrinterFailure.Unreachable, $"Moonraker did not answer {method} in time.", exception);
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	// For calls Moonraker only answers once the work is done (G-code): waits briefly for a refusal, then
	// leaves the call running. A late answer is dropped.
	public async Task CallWithoutWaitingAsync(string method, object? parameters, TimeSpan refusalWindow, CancellationToken cancellationToken)
	{
		var (id, answer) = await SendAsync(method, parameters, cancellationToken);
		try
		{
			await answer.WaitAsync(refusalWindow, cancellationToken);
		}
		catch (TimeoutException)
		{
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	private async Task<(int Id, Task<JsonElement> Answer)> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
	{
		var id = Interlocked.Increment(ref _nextId);
		var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = answer;

		var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["id"] = id };
		if (parameters is not null)
		{
			request["params"] = JsonSerializer.SerializeToNode(parameters);
		}

		await _sending.WaitAsync(cancellationToken);
		try
		{
			await _socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, cancellationToken);
		}
		catch (WebSocketException exception)
		{
			_pending.TryRemove(id, out _);
			throw new PrinterException(PrinterFailure.Unreachable, $"Moonraker's websocket closed while sending {method}.", exception);
		}
		finally
		{
			_sending.Release();
		}

		return (id, answer.Task);
	}

	private async Task ReceiveAsync(CancellationToken cancellationToken)
	{
		var buffer = new byte[16 * 1024];
		using var message = new MemoryStream();
		try
		{
			while (_socket.State == WebSocketState.Open)
			{
				var received = await _socket.ReceiveAsync(buffer, cancellationToken);
				if (received.MessageType == WebSocketMessageType.Close)
				{
					break;
				}

				message.Write(buffer, 0, received.Count);
				if (message.Length > MaxMessageBytes)
				{
					break;
				}

				if (!received.EndOfMessage)
				{
					continue;
				}

				Dispatch(message.ToArray());
				message.SetLength(0);
			}
		}
		catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
		{
		}
		finally
		{
			foreach (var pending in _pending.Values)
			{
				pending.TrySetException(new PrinterException(PrinterFailure.Unreachable, "Moonraker's websocket closed."));
			}
		}
	}

	private void Dispatch(byte[] frame)
	{
		JsonElement root;
		try
		{
			using var document = JsonDocument.Parse(frame);
			root = document.RootElement.Clone();
		}
		catch (JsonException)
		{
			return;
		}

		if (root.Str("method") is { } method)
		{
			_onNotification(method, root.TryGetProperty("params", out var parameters) ? parameters : default);
			return;
		}

		if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || !_pending.TryGetValue(id, out var pending))
		{
			return;
		}

		if (root.Obj("error") is { } error)
		{
			pending.TrySetException(Failure(error));
		}
		else
		{
			pending.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
		}
	}

	// Moonraker's error codes follow HTTP's; 503 is Klipper not being ready.
	private static PrinterException Failure(JsonElement error)
	{
		var code = error.Num("code") is { } number ? (int)number : 0;
		var failure = code switch
		{
			401 => PrinterFailure.Unauthorized,
			403 => PrinterFailure.Forbidden,
			404 => PrinterFailure.NotFound,
			400 => PrinterFailure.BadRequest,
			503 => PrinterFailure.Conflict,
			_ => PrinterFailure.ServerError,
		};
		return new PrinterException(failure, $"Moonraker refused the request ({code}): {error.Str("message")}");
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			if (_socket.State == WebSocketState.Open)
			{
				using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
			}
		}
		catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
		{
		}

		await _stop.CancelAsync();
		await Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		_socket.Dispose();
		_stop.Dispose();
		_sending.Dispose();
	}
}
