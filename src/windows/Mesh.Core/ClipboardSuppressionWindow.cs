namespace Mesh.Core;

/// <summary>
/// Tracks the short "ignore the next clipboard-change notification" window that
/// <c>ClipboardWatcher</c> (Mesh.App) uses when the app writes to the clipboard itself (e.g. the
/// "Copy" button on a history entry), so that write doesn't get re-detected and re-pushed to the
/// peer as if it were a brand-new local copy.
///
/// Lives here, in the plain net8.0 Mesh.Core library, rather than inside the WPF-dependent
/// ClipboardWatcher itself, purely so this timing decision can be unit-tested from Mesh.Tests
/// without a real Win32 message loop or window handle.
/// </summary>
public sealed class ClipboardSuppressionWindow(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    /// <summary>Call right before the app writes to the clipboard itself so the resulting change
    /// notification is suppressed for the given window.</summary>
    public void SuppressNextChange(TimeSpan window)
    {
        _suppressUntil = _now() + window;
    }

    /// <summary>True if a clipboard-change notification arriving right now falls within an
    /// active suppression window set by <see cref="SuppressNextChange"/> and should be ignored.</summary>
    public bool ShouldSuppress() => _now() < _suppressUntil;
}
