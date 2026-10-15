using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Mesh.App;

/// <summary>
/// Dialer-only modeless dialog for first-time pairing. Starts in a "Connecting..." placeholder
/// state — the dialer never sees the PIN, only the listener does — then, only if the listener
/// approves, morphs into a text-entry prompt via <see cref="PromptForCodeAsync"/>. Created and
/// shown immediately on the Pair/Connect click, independently of any <c>IPairingPrompt</c> call,
/// so "Connecting..." appears without waiting on a protocol step.
/// </summary>
public partial class PinEntryWindow : Window
{
    private TaskCompletionSource<string?>? _entryCompletion;

    /// <summary>Raised when Cancel is clicked while still on the "Connecting..." placeholder
    /// (before <see cref="PromptForCodeAsync"/> swaps to the code-entry state). The caller aborts
    /// the in-flight attempt (e.g. closes the socket) and this window closes itself.</summary>
    public event Action? ConnectingCancelRequested;

    public PinEntryWindow(string peerName)
    {
        InitializeComponent();
        ConnectingText.Text = $"Connecting to {peerName}...";
        EntryMessageText.Text = $"Enter the code shown on {peerName}:";
    }

    private void ConnectingCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectingCancelRequested?.Invoke();
        Close();
    }

    /// <summary>Switches to the code-entry state and waits for Submit/Cancel or
    /// <paramref name="ct"/> (cancelling closes the dialog, same as a user Cancel). Call at most
    /// once per window.</summary>
    public async Task<string?> PromptForCodeAsync(CancellationToken ct)
    {
        _entryCompletion = new TaskCompletionSource<string?>();
        ConnectingPanel.Visibility = Visibility.Collapsed;
        EntryPanel.Visibility = Visibility.Visible;
        CodeTextBox.Focus();
        using var registration = ct.Register(() => Dispatcher.InvokeAsync(() =>
        {
            _entryCompletion?.TrySetResult(null);
            if (IsVisible) Close();
        }));
        return await _entryCompletion.Task;
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        _entryCompletion?.TrySetResult(CodeTextBox.Text.Trim());
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _entryCompletion?.TrySetResult(null);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _entryCompletion?.TrySetResult(null); // no-op if Submit/Cancel already set it
        base.OnClosed(e);
    }
}
