using InstrumentControlWorkbench.Infrastructure.Transport;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class LoopbackScpiServerTests
{
    [Fact]
    public async Task RepeatedPeerShutdownDrainsActiveHandlersAndSocketReads()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> query;
            await using (var server = new LoopbackScpiServer(async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { drained.TrySetResult(); }
            }))
            {
                await using var transport = new TcpScpiTransport(server.Configuration(TimeSpan.FromMinutes(1)));
                await transport.OpenAsync(CancellationToken.None);
                query = transport.QueryAsync("wait?", CancellationToken.None);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.True(drained.Task.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        }
    }
}
