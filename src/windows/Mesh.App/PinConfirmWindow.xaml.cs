using System;
using System.Windows;

namespace Mesh.App;

/// <summary>
/// Modeless two-button confirmation window, used both for the PIN comparison and for the
/// "Certificate changed — re-pair?" warning. <see cref="Result"/> is false unless the
/// positive button was clicked (closing the window or a programmatic Close() = decline).
///
/// A positive click does not close the window by itself — it raises <see cref="Decided"/> and
/// leaves closing to the caller, so a PIN confirm can switch to a "Connecting..." placeholder
/// (via <see cref="ShowConnecting"/>) instead of vanishing while the peer is still being waited
/// on. The negative button stays live throughout: before the positive click it still means
/// "decline" (<see cref="Decided"/> with false); after <see cref="ShowConnecting"/> it relabels
/// to "Cancel" and instead raises <see cref="CancelRequested"/>, letting the user abort a
/// pairing attempt they already approved but no longer want to wait on.
/// </summary>
public partial class PinConfirmWindow : Window
{
    public bool Result { get; private set; }

    /// <summary>Raised once, with the clicked answer, before the window necessarily closes.</summary>
    public event Action<bool>? Decided;

    /// <summary>Raised instead of <see cref="Decided"/> when the negative button is clicked
    /// after <see cref="ShowConnecting"/> has already fired Decided(true).</summary>
    public event Action? CancelRequested;

    private bool _connecting;

    public PinConfirmWindow(string title, string message, string? pin, string yesLabel, string noLabel)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (pin == null) PinText.Visibility = Visibility.Collapsed;
        else PinText.Text = pin;
        MatchButton.Content = yesLabel;
        NoMatchButton.Content = noLabel;
    }

    /// <summary>Relabels the positive button to a greyed-out "Connecting" status (disabled, no
    /// longer clickable) while leaving the original message and PIN visible — the user still
    /// needs to see the PIN they just approved. The negative button stays enabled, relabelled to
    /// let the user cancel while waiting. Caller closes the window once the pairing attempt
    /// actually finishes.</summary>
    public void ShowConnecting(string connectingLabel = "Connecting", string cancelLabel = "Cancel")
    {
        _connecting = true;
        MatchButton.IsEnabled = false;
        MatchButton.Content = connectingLabel;
        NoMatchButton.Content = cancelLabel;
    }

    private void MatchButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        Decided?.Invoke(true);
    }

    private void NoMatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting)
        {
            CancelRequested?.Invoke();
            Close();
            return;
        }
        Result = false;
        Decided?.Invoke(false);
        Close();
    }
}
