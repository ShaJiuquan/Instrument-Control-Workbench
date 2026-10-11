using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using InstrumentControlWorkbench.Core.Devices;

namespace InstrumentControlWorkbench.Application.Tests;

// Owned local protocol peer; all accept/client tasks and sockets are drained.
internal sealed class LoopbackScpiServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task accepting;
    private readonly List<Task> clients = [];
    private readonly Channel<string> received = Channel.CreateUnbounded<string>();
    private int connections;
    public LoopbackScpiServer(Func<string, CancellationToken, Task<byte[]?>> handler)
    {
        Handler = handler; listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; accepting = AcceptAsync();
    }
    public int Port { get; }
    public int Connections => Volatile.Read(ref connections);
    public ConcurrentQueue<string> Commands { get; } = new();
    public Func<string, CancellationToken, Task<byte[]?>> Handler { get; set; }
    public int ReplyChunkBytes { get; set; } = int.MaxValue;
    public TcpDeviceConnection Configuration(TimeSpan? timeout = null, string terminator = "\n") => new("127.0.0.1", Port, terminator, timeout ?? TimeSpan.FromSeconds(2));
    public static byte[] Reply(string text) => Encoding.ASCII.GetBytes(text + "\n");
    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token); Interlocked.Increment(ref connections); client.NoDelay = true;
                clients.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(lifetime.Token) is { } command)
                {
                    Commands.Enqueue(command); received.Writer.TryWrite(command);
                    if (await Handler(command, lifetime.Token) is { } response)
                    {
                        if (response.Length == 0) return; // Deliberate peer EOF.
                        for (var offset = 0; offset < response.Length;)
                        {
                            var count = Math.Min(ReplyChunkBytes, response.Length - offset);
                            await stream.WriteAsync(response.AsMemory(offset, count), lifetime.Token);
                            offset += count;
                            if (offset < response.Length) await Task.Delay(1, lifetime.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) { } // Peer closes poisoned/canceled transactions.
            catch (SocketException) { }
        }
    }
    public async Task WaitForAsync(string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await received.Reader.ReadAsync(timeout.Token)) != command) { }
    }
    public async ValueTask DisposeAsync()
    {
        // Drain token-cancelled accept before Stop: an accepted client can
        // otherwise re-enter AcceptTcpClientAsync on a stopped listener.
        lifetime.Cancel(); await accepting; listener.Stop(); await Task.WhenAll(clients); lifetime.Dispose(); received.Writer.TryComplete();
    }
}
