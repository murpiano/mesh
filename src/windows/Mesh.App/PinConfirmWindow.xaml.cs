using System.Windows;

namespace Mesh.App;

public partial class PinConfirmWindow : Window
{
    public PinConfirmWindow(string pin)
    {
        InitializeComponent();
        PinText.Text = pin;
    }

    private void MatchButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void NoMatchButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
