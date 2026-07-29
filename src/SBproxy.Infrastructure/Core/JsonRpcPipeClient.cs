using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SBproxy.Application.Diagnostics;

namespace SBproxy.Infrastructure.Core;

// JSON Lines 的 id 用于关联请求；无 id 帧是事件。
public sealed class JsonRpcPipeClient : IDisposable, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _idSeed;
    private bool _isDisposed;

    public JsonRpcPipeClient(string pipeName) => _pipeName = pipeName;

    public event EventHandler<EventNotification>? EventReceived;

    public event EventHandler? ConnectionClosed;

    public bool IsConnected => _stream is not null;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                return;
            }

            var stream = await ConnectStreamAsync(_pipeName, cancellationToken).ConfigureAwait(false);
            var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            var readerCts = new CancellationTokenSource();
            _stream = stream;
            _reader = reader;
            _writer = writer;
            _readerCts = readerCts;
            _ = Task.Run(() => ReadLoopAsync(stream, reader, writer, readerCts));
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static async Task<Stream> ConnectStreamAsync(string pipeName, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (!Path.IsPathRooted(pipeName))
            {
                throw new InvalidOperationException("IPC Unix socket path must be absolute.");
            }

            await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var writer = _writer ?? throw new InvalidOperationException("Pipe is not connected");

        var id = Interlocked.Increment(ref _idSeed).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        try
        {
            // 写入前先登记 pending，避免早到响应丢失匹配。
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            await using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(
        Stream stream,
        StreamReader reader,
        StreamWriter writer,
        CancellationTokenSource readerCts)
    {
        var ct = readerCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.GetString();
                    if (id is not null && _pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("result", out var result))
                        {
                            tcs.TrySetResult(result.Clone());
                        }
                        else if (root.TryGetProperty("error", out var error))
                        {
                            var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "unknown" : "unknown";
                            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                            tcs.TrySetException(new IpcRemoteException(code, message));
                        }
                    }
                }
                else if (root.TryGetProperty("event", out var ev) && root.TryGetProperty("data", out var data))
                {
                    try
                    {
                        EventReceived?.Invoke(this, new EventNotification(ev.GetString() ?? string.Empty, data.Clone()));
                    }
                    catch (Exception exception)
                    {
                        // 订阅者异常不能中断读取循环。
                        AppLogger.Error(exception, "IPC event dispatch failed");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (_isDisposed)
        {
        }
        catch (Exception ex)
        {
            // 读取循环失败会中断所有待处理调用的响应源；无 pending 时仍需留下第一现场。
            AppLogger.Error(ex, "IPC read loop failed");
            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(ex);
            }
            _pending.Clear();
        }
        finally
        {
            if (!_isDisposed)
            {
                FailPending(new IOException("IPC connection was closed."));
            }
            await CloseConnectionAsync(stream, reader, writer, readerCts).ConfigureAwait(false);
        }
    }

    private async Task CloseConnectionAsync(
        Stream stream,
        StreamReader reader,
        StreamWriter writer,
        CancellationTokenSource readerCts)
    {
        var ownsCurrentConnection = false;
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_stream, stream))
            {
                _stream = null;
                _reader = null;
                _writer = null;
                _readerCts = null;
                ownsCurrentConnection = true;
            }
        }
        finally
        {
            _connectionGate.Release();
        }

        readerCts.Cancel();
        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        reader.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
        readerCts.Dispose();
        if (ownsCurrentConnection && !_isDisposed)
        {
            ConnectionClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _readerCts?.Cancel();
        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        CancelPending();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _readerCts?.Cancel();

        if (_writer is not null)
        {
            try
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        _reader?.Dispose();
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        CancelPending();
    }

    private void CancelPending()
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        _pending.Clear();
    }

    private void FailPending(Exception exception)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }

        _pending.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

public sealed record EventNotification(string Event, JsonElement Data);

public sealed class IpcRemoteException : Exception
{
    public string Code { get; }

    public IpcRemoteException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
