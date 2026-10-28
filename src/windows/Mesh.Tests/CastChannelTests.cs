using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class CastChannelTests : IClassFixture<TwoIdentitiesFixture>
{
    private readonly TwoIdentitiesFixture _identities;

    public CastChannelTests(TwoIdentitiesFixture identities) => _identities = identities;

    private static CastFrame SampleFrame() =>
        new(CastFrameType.VideoKey, 42, new byte[] { 9, 8, 7 });

    [Fact]
    public async Task Frame_FromTheExpectedPeerWithTheRightToken_Arrives()
    {
        var senderFingerprint = IdentityStore.Fingerprint(_identities.CertB);
        using var listener = CastChannelListener.Start(_identities.CertA, "token-1", senderFingerprint);

        var acceptTask = listener.AcceptAsync(CancellationToken.None);
        using var sender = await CastChannel.ConnectAsync(_identities.CertB, "127.0.0.1", listener.Port, "token-1");
        using var receiver = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));

        await sender.WriteFrameAsync(SampleFrame());
        var received = await receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CastFrameType.VideoKey, received.Type);
        Assert.Equal(42, received.TimestampUs);
        Assert.Equal(new byte[] { 9, 8, 7 }, received.Payload.ToArray());
    }

    [Fact]
    public async Task WrongToken_IsRefusedAndTheListenerKeepsListening()
    {
        var senderFingerprint = IdentityStore.Fingerprint(_identities.CertB);
        using var listener = CastChannelListener.Start(_identities.CertA, "token-1", senderFingerprint);

        var refusedAccept = listener.AcceptAsync(CancellationToken.None);
        using (var impostor = await CastChannel.ConnectAsync(_identities.CertB, "127.0.0.1", listener.Port, "token-2"))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => refusedAccept.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        // The listener is still usable: the real sender gets through on the same port.
        var acceptTask = listener.AcceptAsync(CancellationToken.None);
        using var sender = await CastChannel.ConnectAsync(_identities.CertB, "127.0.0.1", listener.Port, "token-1");
        using var receiver = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        await sender.WriteFrameAsync(SampleFrame());

        var received = await receiver.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CastFrameType.VideoKey, received.Type);
    }

    [Fact]
    public async Task AnotherDevicesCertificate_IsRefused()
    {
        // The listener expects itself (CertA) as the sender; CertB turns up instead.
        var wrongFingerprint = IdentityStore.Fingerprint(_identities.CertA);
        using var listener = CastChannelListener.Start(_identities.CertA, "token-1", wrongFingerprint);

        var acceptTask = listener.AcceptAsync(CancellationToken.None);
        using var impostor = await CastChannel.ConnectAsync(_identities.CertB, "127.0.0.1", listener.Port, "token-1");

        await Assert.ThrowsAnyAsync<Exception>(() => acceptTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
