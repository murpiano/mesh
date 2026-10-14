// src/windows/Mesh.Tests/ClipboardHistoryStoreTests.cs
using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

public class ClipboardHistoryStoreTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"mesh-clip-test-{Guid.NewGuid()}.db");

    [Fact]
    public void Insert_ThenGetAll_RoundTrips()
    {
        var path = TempDbPath();
        try
        {
            var entry = ClipboardHistoryStore.Insert(path, "hello", "local", DateTimeOffset.UtcNow);
            var all = ClipboardHistoryStore.GetAll(path);
            Assert.Single(all);
            Assert.Equal("hello", all[0].Text);
            Assert.Equal("local", all[0].Source);
            Assert.Equal(entry.Id, all[0].Id);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            ClipboardHistoryStore.Insert(path, "first", "local", t0);
            ClipboardHistoryStore.Insert(path, "second", "local", t0.AddSeconds(1));
            var all = ClipboardHistoryStore.GetAll(path);
            Assert.Equal("second", all[0].Text);
            Assert.Equal("first", all[1].Text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Insert_Beyond50_EvictsOldest_CountStaysAt50()
    {
        var path = TempDbPath();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            for (var i = 0; i < 51; i++)
                ClipboardHistoryStore.Insert(path, $"entry-{i}", "local", t0.AddSeconds(i));

            var all = ClipboardHistoryStore.GetAll(path);
            Assert.Equal(50, all.Count);
            Assert.DoesNotContain(all, e => e.Text == "entry-0"); // oldest, evicted
            Assert.Contains(all, e => e.Text == "entry-50"); // newest, kept
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void GetAll_ReturnsEmptyList_WhenDbFileMissing()
    {
        var path = TempDbPath(); // never created
        Assert.Empty(ClipboardHistoryStore.GetAll(path));
    }
}
