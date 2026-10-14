using Mesh.Core;
using Xunit;

namespace Mesh.Tests;

/// <summary>
/// Covers the self-copy dedup timing decision that <c>ClipboardWatcher</c> (Mesh.App) delegates
/// to <see cref="ClipboardSuppressionWindow"/>: a change notification that arrives within the
/// suppression window set by a "Copy" action must be suppressed, but one that arrives after the
/// window has expired (a genuinely new, different local copy made shortly afterwards) must not
/// be eaten by the dedup window.
/// </summary>
public class ClipboardSuppressionWindowTests
{
    [Fact]
    public void ShouldSuppress_ReturnsFalse_BeforeAnySuppressionRequested()
    {
        var window = new ClipboardSuppressionWindow();

        Assert.False(window.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_ReturnsTrue_WithinSuppressionWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ClipboardSuppressionWindow(() => now);

        window.SuppressNextChange(TimeSpan.FromMilliseconds(500));
        // Simulate the WM_CLIPBOARDUPDATE notification arriving 100ms later, still inside the window.
        now = now.AddMilliseconds(100);

        Assert.True(window.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_ReturnsFalse_AfterSuppressionWindowExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var window = new ClipboardSuppressionWindow(() => now);

        window.SuppressNextChange(TimeSpan.FromMilliseconds(500));
        // A genuinely new, different local copy made after the window has elapsed must not be
        // mistaken for the app's own write and swallowed.
        now = now.AddMilliseconds(600);

        Assert.False(window.ShouldSuppress());
    }

    [Fact]
    public void ShouldSuppress_UsingRealClock_SuppressesThenAllowsAfterRealDelay()
    {
        // End-to-end check with the real wall clock (as ClipboardWatcher uses it in production),
        // to prove the injectable-clock tests above aren't hiding a wiring bug.
        var window = new ClipboardSuppressionWindow();

        window.SuppressNextChange(TimeSpan.FromMilliseconds(300));
        Assert.True(window.ShouldSuppress());

        Thread.Sleep(400);

        Assert.False(window.ShouldSuppress());
    }
}
