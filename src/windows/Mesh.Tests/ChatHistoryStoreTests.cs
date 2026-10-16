// src/windows/Mesh.Tests/ChatHistoryStoreTests.cs
using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class ChatHistoryStoreTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"mesh-chat-test-{Guid.NewGuid()}.db");

    [Fact]
    public void Insert_ThenGetAllForPeer_RoundTrips()
    {
        var path = TempDbPath();
        try
        {
            var entry = ChatHistoryStore.Insert(path, "msg-1", "peer-a", "self-uuid", "You", "hello",
                DateTimeOffset.UtcNow);
            var all = ChatHistoryStore.GetAllForPeer(path, "peer-a");
            Assert.Single(all);
            Assert.Equal("hello", all[0].Text);
            Assert.Equal("You", all[0].SenderLabel);
            Assert.Equal("self-uuid", all[0].SenderUuid);
            Assert.Equal(entry!.MsgId, all[0].MsgId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_DuplicateMsgId_IsIgnored_ReturnsNull()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ChatHistoryStore.Insert(path, "msg-1", "peer-a", "self-uuid", "You", "hello", t0);
            var second = ChatHistoryStore.Insert(path, "msg-1", "peer-a", "self-uuid", "You", "hello again", t0);

            Assert.Null(second);
            var all = ChatHistoryStore.GetAllForPeer(path, "peer-a");
            Assert.Single(all);
            Assert.Equal("hello", all[0].Text); // first write wins; duplicate was ignored
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetAllForPeer_ReturnsOldestFirst()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ChatHistoryStore.Insert(path, "msg-1", "peer-a", "self-uuid", "You", "first", t0);
            ChatHistoryStore.Insert(path, "msg-2", "peer-a", "self-uuid", "You", "second", t0.AddSeconds(1));
            var all = ChatHistoryStore.GetAllForPeer(path, "peer-a");
            Assert.Equal("first", all[0].Text);
            Assert.Equal("second", all[1].Text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetAllForPeer_ScopedToPeer_OtherPeerMessagesExcluded()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ChatHistoryStore.Insert(path, "msg-1", "peer-a", "self-uuid", "You", "to a", t0);
            ChatHistoryStore.Insert(path, "msg-2", "peer-b", "self-uuid", "You", "to b", t0);

            Assert.Equal(new[] { "to a" }, ChatHistoryStore.GetAllForPeer(path, "peer-a").Select(m => m.Text));
            Assert.Equal(new[] { "to b" }, ChatHistoryStore.GetAllForPeer(path, "peer-b").Select(m => m.Text));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_Beyond50ForSamePeer_EvictsOldest_CountStaysAt50()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            for (var i = 0; i < 51; i++)
                ChatHistoryStore.Insert(path, $"msg-{i}", "peer-a", "self-uuid", "You", $"entry-{i}", t0.AddSeconds(i));

            var all = ChatHistoryStore.GetAllForPeer(path, "peer-a");
            Assert.Equal(50, all.Count);
            Assert.DoesNotContain(all, e => e.Text == "entry-0"); // oldest, evicted
            Assert.Contains(all, e => e.Text == "entry-50"); // newest, kept
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_Beyond50ForOnePeer_DoesNotEvictAnotherPeersMessages()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ChatHistoryStore.Insert(path, "msg-b", "peer-b", "self-uuid", "You", "to b", t0);
            for (var i = 0; i < 51; i++)
                ChatHistoryStore.Insert(path, $"msg-a-{i}", "peer-a", "self-uuid", "You", $"entry-{i}", t0.AddSeconds(i));

            Assert.Equal(new[] { "to b" }, ChatHistoryStore.GetAllForPeer(path, "peer-b").Select(m => m.Text));
            Assert.Equal(50, ChatHistoryStore.GetAllForPeer(path, "peer-a").Count);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetAllForPeer_ReturnsEmptyList_WhenDbFileMissing()
    {
        var path = TempDbPath(); // never created
        Assert.Empty(ChatHistoryStore.GetAllForPeer(path, "peer-a"));
    }

    [Fact]
    public void DeleteForPeer_RemovesOnlyThatPeersMessages()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ChatHistoryStore.Insert(path, "msg-a", "peer-a", "self-uuid", "You", "to a", t0);
            ChatHistoryStore.Insert(path, "msg-b", "peer-b", "self-uuid", "You", "to b", t0);

            ChatHistoryStore.DeleteForPeer(path, "peer-a");

            Assert.Empty(ChatHistoryStore.GetAllForPeer(path, "peer-a"));
            Assert.Equal(new[] { "to b" }, ChatHistoryStore.GetAllForPeer(path, "peer-b").Select(m => m.Text));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DeleteForPeer_DbFileMissing_DoesNotThrow()
    {
        var path = TempDbPath(); // never created
        ChatHistoryStore.DeleteForPeer(path, "peer-a");
    }
}
