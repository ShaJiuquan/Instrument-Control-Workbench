using System.Text;
using InstrumentControlWorkbench.Infrastructure.Transport;

namespace InstrumentControlWorkbench.Application.Tests;

public sealed class TcpScpiTransportTests
{
    [Fact]
    public async Task FragmentedCrLfReplyIsOneResponseAndDoesNotContaminateTheNextQuery()
    {
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(Encoding.ASCII.GetBytes("1.234\r\n"))) { ReplyChunkBytes = 1 };
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        Assert.Equal("1.234", await transport.QueryAsync("first?", CancellationToken.None));
        Assert.Equal("1.234", await transport.QueryAsync("second?", CancellationToken.None)); Assert.True(transport.IsConnected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofAndNonAsciiRepliesInvalidateTheConnection(bool nonAscii)
    {
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(nonAscii ? new byte[] { 255, 10 } : []));
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<IOException>(() => transport.QueryAsync("value?", CancellationToken.None)); Assert.False(transport.IsConnected);
    }
    [Fact]
    public async Task ReplyAtTheByteBudgetRemainsValid()
    {
        var expected = new string('x', TcpScpiTransport.MaximumResponseBytes);
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(expected)));
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        Assert.Equal(expected, await transport.QueryAsync("large?", CancellationToken.None)); Assert.True(transport.IsConnected);
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\\n")]
    [InlineData("\\r\\n")]
    public async Task ExplicitOpenAndConcurrentQueriesPreserveRequestResponseIdentity(string terminator)
    {
        await using var server = new LoopbackScpiServer((command, _) => Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(command + "-reply")));
        await using var transport = new TcpScpiTransport(server.Configuration(terminator: terminator));
        Assert.False(transport.IsConnected); Assert.Equal(0, server.Connections);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync("one?", CancellationToken.None));
        await transport.OpenAsync(CancellationToken.None);
        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => transport.QueryAsync($"Q{i}?", CancellationToken.None)));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"Q{i}?-reply"), replies); Assert.Equal(20, server.Commands.Count);
        await transport.CloseAsync(CancellationToken.None); Assert.False(transport.IsConnected);
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("1\n2\n")]
    [InlineData("\u0001\n")]
    public async Task MalformedOrSurplusFramesInvalidateTheSession(string raw)
    {
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(Encoding.ASCII.GetBytes(raw)));
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => transport.QueryAsync("bad?", CancellationToken.None)); Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.QueryAsync("next?", CancellationToken.None)); Assert.Single(server.Commands);
        server.Handler = (_, _) => Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("fresh"));
        await transport.OpenAsync(CancellationToken.None); Assert.Equal("fresh", await transport.QueryAsync("next?", CancellationToken.None)); Assert.Equal(2, server.Connections);
    }
    [Fact]
    public async Task ByteBudgetRejectsOversizedReplyWithoutReusingItsTail()
    {
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(LoopbackScpiServer.Reply(new string('x', TcpScpiTransport.MaximumResponseBytes + 1))));
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => transport.QueryAsync("large?", CancellationToken.None)); Assert.False(transport.IsConnected);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationPoisonOnlyTheDispatchedTransaction(bool callerCancellation)
    {
        await using var server = new LoopbackScpiServer(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; });
        await using var transport = new TcpScpiTransport(server.Configuration(TimeSpan.FromMilliseconds(150))); await transport.OpenAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(); var pending = transport.QueryAsync("wait?", cancellation.Token); await server.WaitForAsync("wait?");
        if (callerCancellation) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); }
        else await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.False(transport.IsConnected); Assert.Single(server.Commands);
    }
    [Fact]
    public async Task CanceledWaiterDoesNotDisconnectAnotherOwnersActiveQuery()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackScpiServer(async (_, token) => { await release.Task.WaitAsync(token); return LoopbackScpiServer.Reply("active"); });
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        var active = transport.QueryAsync("active?", CancellationToken.None); await server.WaitForAsync("active?");
        using var cancellation = new CancellationTokenSource(); var waiter = transport.QueryAsync("waiter?", cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter); Assert.True(transport.IsConnected); Assert.Single(server.Commands);
        release.TrySetResult(); Assert.Equal("active", await active);
    }
    [Fact]
    public async Task DisposalCancelsActiveSocketReadAndDrainsIt()
    {
        await using var server = new LoopbackScpiServer(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; });
        var transport = new TcpScpiTransport(server.Configuration(TimeSpan.FromMinutes(1))); await transport.OpenAsync(CancellationToken.None);
        var query = transport.QueryAsync("wait?", CancellationToken.None); await server.WaitForAsync("wait?");
        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.False(transport.IsConnected); await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.OpenAsync(CancellationToken.None));
    }
    [Theory]
    [InlineData("bad\ncommand")]
    [InlineData("unicode\u03b1")]
    public async Task InvalidCommandsDoNotSendOrPoisonAnOpenConnection(string command)
    {
        await using var server = new LoopbackScpiServer((_, _) => Task.FromResult<byte[]?>(LoopbackScpiServer.Reply("ok")));
        await using var transport = new TcpScpiTransport(server.Configuration()); await transport.OpenAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.QueryAsync(command, CancellationToken.None)); Assert.True(transport.IsConnected); Assert.Empty(server.Commands);
        Assert.Equal("ok", await transport.QueryAsync("valid?", CancellationToken.None));
    }
}
