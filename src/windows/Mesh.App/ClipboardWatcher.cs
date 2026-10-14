// src/windows/Mesh.App/ClipboardWatcher.cs
//
// Lives in Mesh.App rather than Mesh.Core: it needs WPF (System.Windows.Window, HwndSource,
// Clipboard), which is only available to the net8.0-windows + UseWPF project. Mesh.Core is a
// plain net8.0 library so that it (and Mesh.Tests) keep building and running cross-platform.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mesh.App;

/// <summary>
/// Fires <see cref="TextCopied"/> when the Windows clipboard's text content changes, via
/// AddClipboardFormatListener on the owning window's HWND. Must be constructed after the owning
/// window's handle exists (e.g. in a Loaded handler). Events are raised on the UI thread.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<string>? TextCopied;

    private readonly HwndSource _source;
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;
    private bool _disposed;

    public ClipboardWatcher(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("Window handle not yet created.");
        _source.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    /// <summary>Call right before the app writes to the clipboard itself (e.g. the "Copy"
    /// button on a history entry) so that resulting change notification isn't re-pushed.</summary>
    public void SuppressNextChange(TimeSpan window)
    {
        _suppressUntil = DateTimeOffset.UtcNow + window;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            if (DateTimeOffset.UtcNow < _suppressUntil)
            {
                return IntPtr.Zero;
            }
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text)) TextCopied?.Invoke(text);
                }
            }
            catch (COMException)
            {
                // Clipboard momentarily locked by another process; skip this notification.
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }
}
