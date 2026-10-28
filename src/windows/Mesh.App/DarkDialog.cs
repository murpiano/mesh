using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Mesh.App;

/// <summary>
/// A modal dark dialog drawn inside the main window over a dimmed screen — the Windows twin of
/// the Android app's dark AlertDialog: title, message, optional text field, and text buttons on
/// the right (negative, then positive) colored by meaning (<see cref="DarkPopup.ColorFor"/>).
/// Used for every confirm, PIN, connecting and edit prompt.
///
/// Buttons don't close the dialog on their own: callers react to <see cref="PositiveClicked"/> /
/// <see cref="NegativeClicked"/> and call <see cref="Close"/> when done, so a dialog can morph in
/// place (e.g. Allow → "Connecting", Deny → "Cancel") while it waits on the peer.
/// </summary>
public sealed class DarkDialog
{
    private readonly Panel _host;
    private readonly Grid _layer;
    private readonly TextBlock _message;
    private readonly TextBlock _positiveText;
    private readonly TextBlock _negativeText;
    private readonly Border _positiveButton;
    private readonly Border _negativeButton;
    private bool _positiveEnabled = true;

    public event Action? PositiveClicked;
    public event Action? NegativeClicked;
    /// <summary>Raised once, after the dialog is removed by <see cref="Close"/>.</summary>
    public event Action? Closed;

    /// <summary>The text field, if the dialog was created with one (PIN entry, message edit).</summary>
    public TextBox? Input { get; }

    public bool IsOpen { get; private set; }

    public DarkDialog(Panel host, string title, string message, string? positive, string? negative,
        string? inputText = null)
    {
        _host = host;
        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
        });
        _message = new TextBlock
        {
            Text = message, FontSize = 15, Foreground = Brush("TextMuted"), TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(_message);
        if (inputText != null)
        {
            Input = new TextBox
            {
                Text = inputText, Style = (Style)Application.Current.FindResource("DarkTextBox"),
                Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    if (_positiveEnabled) PositiveClicked?.Invoke();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    NegativeClicked?.Invoke();
                }
            };
            content.Children.Add(Input);
        }

        (_negativeButton, _negativeText) = MakeButton(negative ?? "", () => NegativeClicked?.Invoke());
        (_positiveButton, _positiveText) = MakeButton(positive ?? "", () => { if (_positiveEnabled) PositiveClicked?.Invoke(); });
        _negativeButton.Visibility = negative == null ? Visibility.Collapsed : Visibility.Visible;
        _positiveButton.Visibility = positive == null ? Visibility.Collapsed : Visibility.Visible;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 4, 8, 8)
        };
        buttons.Children.Add(_negativeButton);
        buttons.Children.Add(_positiveButton);

        var body = new StackPanel();
        body.Children.Add(content);
        body.Children.Add(buttons);

        var card = new Border
        {
            Background = Brush("BgPopup"), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(32), MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = body
        };
        // The scrim takes every click outside the card: the dialog is modal, like the Android one.
        _layer = new Grid { Background = Brush("DialogScrim") };
        _layer.Children.Add(card);
    }

    public string Message
    {
        get => _message.Text;
        set => _message.Text = value;
    }

    /// <summary>Relabels and/or enables the positive button, recoloring it by its new label.</summary>
    public void SetPositive(string label, bool enabled = true)
    {
        _positiveText.Text = label;
        _positiveText.Foreground = DarkPopup.ColorFor(label);
        _positiveEnabled = enabled;
        _positiveButton.Visibility = Visibility.Visible;
        _positiveButton.Opacity = enabled ? 1 : 0.4;
        _positiveButton.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
    }

    public void SetNegative(string label)
    {
        _negativeText.Text = label;
        _negativeText.Foreground = DarkPopup.ColorFor(label);
        _negativeButton.Visibility = Visibility.Visible;
    }

    public void Show()
    {
        if (IsOpen) return;
        IsOpen = true;
        _host.Children.Add(_layer);
        if (Input != null)
        {
            Input.Focus();
            Input.SelectAll();
        }
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _host.Children.Remove(_layer);
        Closed?.Invoke();
    }

    private (Border Button, TextBlock Text) MakeButton(string label, Action onClick)
    {
        var text = new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = DarkPopup.ColorFor(label) };
        var button = new Border
        {
            Padding = new Thickness(16, 10, 16, 10), Background = Brushes.Transparent, CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand, Child = text
        };
        // Two steps, as on the phone: a tint under the pointer, a stronger one while held.
        var hover = Brush("BgPopupItemPressed");
        var pressed = Brush("BgPressed");
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, _) => button.Background = pressed;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            button.Background = hover;
            onClick();
        };
        return (button, text);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
