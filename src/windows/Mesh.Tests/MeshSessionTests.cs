using Mesh.Core;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

namespace Mesh.Tests;

public class MeshSessionTests
{
    private static async Task<(MeshSession ServerSession, MeshSession ClientSession)> CreatePairAsync(int port)
    {
        var (server, client) = await CreateTransportsAsync(port);
        return (
            MeshSession.FromTransport(server.Client, server.Stream, "peer-uuid-client", "127.0.0.1", port),
            MeshSession.FromTransport(client.Client, client.Stream, "peer-uuid-server", "127.0.0.1", port));
    }

    private static async Task<((System.Net.Sockets.TcpClient Client, System.Net.Security.SslStream Stream) Server,
        (System.Net.Sockets.TcpClient Client, System.Net.Security.SslStream Stream) Client)> CreateTransportsAsync(int port)
    {
        var certAPath = Path.Combine(Path.GetTempPath(), $"mesh-test-a-{Guid.NewGuid()}.pfx");
        var certBPath = Path.Combine(Path.GetTempPath(), $"mesh-test-b-{Guid.NewGuid()}.pfx");
        var certA = IdentityStore.LoadOrCreate(certAPath);
        var certB = IdentityStore.LoadOrCreate(certBPath);

        var listener = new PairingListener(port);
        var acceptTask = listener.AcceptAsync(certA);
        var clientSession = await PairingSession.ConnectAsync(certB, "127.0.0.1", port);
        var serverSession = await acceptTask;
        listener.Dispose();

        return (serverSession.DetachTransport(), clientSession.DetachTransport());
    }

    [Fact]
    public async Task FromTransport_HandlersPassedIn_ReceiveMessagesAlreadyBufferedBeforeConstruction()
    {
        // The peer writes immediately after reconnecting, before our side has even built its
        // MeshSession: the frames are already sitting in the stream, so the very first read
        // completes straight away. With the handler passed into FromTransport there is no
        // "subscribe after construction" window in which those frames could be dropped.
        var (serverTransport, clientTransport) = await CreateTransportsAsync(51895);
        var server = MeshSession.FromTransport(serverTransport.Client, serverTransport.Stream,
            "peer-uuid-client", "127.0.0.1", 51895);
        MeshSession? client = null;
        try
        {
            const int count = 20;
            for (var i = 0; i < count; i++)
                await server.SendAsync(new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, $"clip-{i}"));
            await Task.Delay(300); // let the frames land in the client's receive buffer

            var received = new ConcurrentQueue<string>();
            var gotAll = new TaskCompletionSource();
            client = MeshSession.FromTransport(clientTransport.Client, clientTransport.Stream,
                "peer-uuid-server", "127.0.0.1", 51895,
                onMessageReceived: msg =>
                {
                    if (msg is ChatSendMessage push) received.Enqueue(push.Text);
                    if (received.Count == count) gotAll.TrySetResult();
                });

            await gotAll.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Enumerable.Range(0, count).Select(i => $"clip-{i}"), received.ToArray());
        }
        finally
        {
            server.Dispose();
            client?.Dispose();
        }
    }

    [Fact]
    public async Task FromTransport_StateHandlerPassedIn_ReceivesStateChanges()
    {
        var (serverTransport, clientTransport) = await CreateTransportsAsync(51896);
        var states = new ConcurrentQueue<MeshSessionState>();
        var server = MeshSession.FromTransport(serverTransport.Client, serverTransport.Stream,
            "peer-uuid-client", "127.0.0.1", 51896);
        var client = MeshSession.FromTransport(clientTransport.Client, clientTransport.Stream,
            "peer-uuid-server", "127.0.0.1", 51896, onStateChanged: states.Enqueue);
        try
        {
            Assert.Equal(MeshSessionState.Connected, client.State);
            server.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Contains(MeshSessionState.Disconnected, states);
            Assert.NotEqual(MeshSessionState.Connected, client.State);
        }
        finally { client.Dispose(); }
    }

    [Fact]
    public async Task SendAsync_DeliversMessageToPeer_MessageReceivedFires()
    {
        var (server, client) = await CreatePairAsync(51891);
        try
        {
            var received = new TaskCompletionSource<MeshMessage>();
            client.MessageReceived += msg => received.TrySetResult(msg);

            await server.SendAsync(new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "hi from server"));

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var push = Assert.IsType<ChatSendMessage>(result);
            Assert.Equal("hi from server", push.Text);
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task StateChanged_FiresDisconnected_WhenPeerCloses()
    {
        var (server, client) = await CreatePairAsync(51892);
        try
        {
            var states = new ConcurrentQueue<MeshSessionState>();
            client.StateChanged += states.Enqueue;

            server.Dispose(); // simulate peer going away

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Contains(MeshSessionState.Disconnected, states);
        }
        finally { client.Dispose(); }
    }

    [Fact]
    public async Task DrainPendingSendsAsync_MessagesQueuedWhileDisconnected_ArriveOverReplacementSession()
    {
        var (oldServer, oldClient) = await CreatePairAsync(51893);
        MeshSession? newServer = null, newClient = null;
        try
        {
            var disconnected = new TaskCompletionSource();
            oldClient.StateChanged += s =>
            {
                if (s != MeshSessionState.Connected) disconnected.TrySetResult();
            };
            oldServer.Dispose(); // peer goes away
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Sent during the disconnect window: must be queued, not written or dropped.
            await oldClient.SendAsync(new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "first"));
            await oldClient.SendAsync(new ChatSendMessage(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow, "second"));

            // Replace the dead session the way the UI layer does: dispose, drain, re-send.
            oldClient.Dispose();
            var pending = await oldClient.DrainPendingSendsAsync();
            Assert.Equal(new[] { "first", "second" },
                pending.Cast<ChatSendMessage>().Select(m => m.Text));
            Assert.Empty(await oldClient.DrainPendingSendsAsync()); // queue really was emptied

            (newServer, newClient) = await CreatePairAsync(51894);
            var received = new ConcurrentQueue<string>();
            var gotBoth = new TaskCompletionSource();
            newServer.MessageReceived += msg =>
            {
                if (msg is ChatSendMessage push) received.Enqueue(push.Text);
                if (received.Count == 2) gotBoth.TrySetResult();
            };

            foreach (var message in pending) await newClient.SendAsync(message);

            await gotBoth.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "first", "second" }, received.ToArray());
        }
        finally
        {
            oldClient.Dispose();
            newServer?.Dispose();
            newClient?.Dispose();
        }
    }

    /// <summary>
    /// Finding I1 regression test: a stuck send (simulated here by holding the private
    /// <c>_sendLock</c> for the whole test, as a large clip write stuck on a dead/slow peer
    /// would) must not prevent the heartbeat loop's idle-timeout check from firing. Before the
    /// fix, the heartbeat loop only ran its idle check right before sending a heartbeat through
    /// the SAME lock (via <c>SendAsync</c>'s blocking <c>WaitAsync()</c>), so a stuck send blocked
    /// the idle check indefinitely too. Uses <c>FromTransport</c>'s test-only
    /// heartbeatInterval/idleTimeout overrides instead of waiting out the real 20s/45s production
    /// values.
    /// </summary>
    [Fact]
    public async Task RunHeartbeatLoop_IdleTimeoutFires_EvenWhileSendLockHeldByStuckSend()
    {
        var (serverTransport, clientTransport) = await CreateTransportsAsync(51898);
        var server = MeshSession.FromTransport(serverTransport.Client, serverTransport.Stream,
            "peer-uuid-client", "127.0.0.1", 51898);
        var states = new ConcurrentQueue<MeshSessionState>();
        var client = MeshSession.FromTransport(clientTransport.Client, clientTransport.Stream,
            "peer-uuid-server", "127.0.0.1", 51898, onStateChanged: states.Enqueue,
            heartbeatInterval: TimeSpan.FromMilliseconds(50), idleTimeout: TimeSpan.FromMilliseconds(200));
        try
        {
            var sendLockField = typeof(MeshSession).GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sendLock = (SemaphoreSlim)sendLockField.GetValue(client)!;
            // Simulate a send stuck mid-write (peer asleep, TCP buffer full): hold the lock
            // for the rest of the test and never release it. If the heartbeat loop still
            // blocked on this lock to run its idle check, Disconnected would never fire below.
            await sendLock.WaitAsync();

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Contains(MeshSessionState.Disconnected, states);
        }
        finally
        {
            server.Dispose();
            client.Dispose();
        }
    }
}
