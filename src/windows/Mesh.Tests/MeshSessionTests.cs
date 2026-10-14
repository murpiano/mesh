using Mesh.Core;
using System.Collections.Concurrent;
using Xunit;

namespace Mesh.Tests;

public class MeshSessionTests
{
    private static async Task<(MeshSession ServerSession, MeshSession ClientSession)> CreatePairAsync(int port)
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

        var (serverClient, serverStream) = serverSession.DetachTransport();
        var (clientTcp, clientStream) = clientSession.DetachTransport();

        var server = MeshSession.FromTransport(serverClient, serverStream, "peer-uuid-client", "127.0.0.1", port);
        var client = MeshSession.FromTransport(clientTcp, clientStream, "peer-uuid-server", "127.0.0.1", port);
        return (server, client);
    }

    [Fact]
    public async Task SendAsync_DeliversMessageToPeer_MessageReceivedFires()
    {
        var (server, client) = await CreatePairAsync(51891);
        try
        {
            var received = new TaskCompletionSource<MeshMessage>();
            client.MessageReceived += msg => received.TrySetResult(msg);

            await server.SendAsync(new ClipboardPushMessage(DateTimeOffset.UtcNow, "hi from server"));

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var push = Assert.IsType<ClipboardPushMessage>(result);
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
}
