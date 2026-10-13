using System.Windows;

namespace Mesh.App;

/// <summary>
/// Modeless two-button confirmation window, used both for the PIN comparison and for the
/// "Certificate changed — re-pair?" warning. <see cref="Result"/> is false unless the
/// positive button was clicked (closing the window or a programmatic Close() = decline).
/// </summary>
public partial class PinConfirmWindow : Window
{
    public bool Result { get; private set; }

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

    private void MatchButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void NoMatchButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
