using System.Net.Sockets;
using System.Text;
using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.Infrastructure.Transport;

// Raw socket SCPI boundary. One request/reply owns the wire; interrupted or
// malformed replies invalidate the connection and require an explicit open.
public sealed class TcpScpiTransport : IAsyncDisposable
{
    public const int MaximumResponseBytes = 65536;
    public const int MaximumCommandBytes = 4096;
    private readonly TcpDeviceConnection configuration;
    private readonly byte[] terminator;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private TcpClient? client;
    private int disposed;
    public TcpScpiTransport(TcpDeviceConnection configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.configuration = configuration;
        terminator = Encoding.ASCII.GetBytes(configuration.Terminator switch
        {
            "\n" or "\\n" => "\n",
            "\r\n" or "\\r\\n" => "\r\n",
            _ => throw new NotSupportedException("TCP SCPI requires an LF or CRLF command terminator."),
        });
    }
    public bool IsConnected => Volatile.Read(ref client) is not null;
    public async Task OpenAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireAlive(); if (client is not null) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token); deadline.CancelAfter(configuration.Timeout);
            var opening = new TcpClient { NoDelay = true };
            try { await opening.ConnectAsync(configuration.Host, configuration.Port, deadline.Token); client = opening; }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
            { opening.Dispose(); throw new TimeoutException("TCP SCPI connection timed out.", ex); }
            catch { opening.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }
    public Task WriteAsync(string command, CancellationToken token) => ExchangeAsync(command, false, token);
    public Task<string> QueryAsync(string command, CancellationToken token) => ExchangeAsync(command, true, token);
    private async Task<string> ExchangeAsync(string command, bool readReply, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > MaximumCommandBytes || command.Any(c => c < 32 || c > 126))
            throw new ArgumentException("SCPI commands must be bounded printable ASCII without line breaks.", nameof(command));
        await gate.WaitAsync(token);
        try
        {
            RequireAlive(); var active = client ?? throw new InvalidOperationException("TCP SCPI is disconnected; open it explicitly.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token); deadline.CancelAfter(configuration.Timeout);
            try
            {
                var stream = active.GetStream();
                if (stream.DataAvailable) throw new IOException("Unsolicited TCP SCPI bytes cannot answer a new command.");
                var text = Encoding.ASCII.GetBytes(command); var payload = new byte[text.Length + terminator.Length]; text.CopyTo(payload, 0); terminator.CopyTo(payload, text.Length);
                await stream.WriteAsync(payload, deadline.Token);
                if (!readReply) return "";
                var response = new List<byte>(); var buffer = new byte[1024];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, deadline.Token);
                    if (count == 0) throw new EndOfStreamException("TCP SCPI peer closed before a complete reply.");
                    for (var i = 0; i < count; i++)
                    {
                        var b = buffer[i];
                        if (b == (byte)'\n')
                        {
                            if (i != count - 1 || stream.DataAvailable) throw new IOException("Surplus TCP SCPI reply bytes are ambiguous.");
                            if (response.Count > 0 && response[^1] == (byte)'\r') response.RemoveAt(response.Count - 1);
                            if (response.Count == 0 || response.Any(c => c < 32 || c > 126)) throw new IOException("SCPI reply must be nonempty printable ASCII.");
                            return Encoding.ASCII.GetString(response.ToArray());
                        }
                        if (response.Count == MaximumResponseBytes) throw new IOException("TCP SCPI reply exceeds its byte budget.");
                        response.Add(b);
                    }
                }
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
            { Invalidate(); throw new TimeoutException("TCP SCPI request/reply timed out; reconnect explicitly.", ex); }
            catch { Invalidate(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task CloseAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { RequireAlive(); Invalidate(); }
        finally { gate.Release(); }
    }
    private void Invalidate() { var previous = Interlocked.Exchange(ref client, null); previous?.Dispose(); }
    private void RequireAlive() { ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel(); await gate.WaitAsync();
        try { Invalidate(); shutdown.Dispose(); }
        finally { gate.Release(); }
    }
}
