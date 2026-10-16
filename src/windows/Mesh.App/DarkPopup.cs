using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Mesh.App;

/// <summary>
/// The one popup menu shared by every screen (a device row's Connect/Disconnect/Unpair, a chat
/// message's Copy/Edit/Delete, the "+" attach menu) — the Windows twin of the Android app's
/// DarkPopup: same width for every popup, centered in the window, items colored by meaning.
///
/// It lives in an overlay panel with no background of its own, so a click outside the card
/// falls through to whatever is underneath: <see cref="MainWindow"/> dismisses the popup on that
/// click (see <see cref="IsInside"/>) and the click still reaches, say, another chat message,
/// which opens its own popup in the same click.
/// </summary>
public sealed class DarkPopup
{
    private static readonly HashSet<string> Positive = new() { "Connect", "Yes", "OK", "Save", "Allow", "Re-pair", "Submit" };
    private static readonly HashSet<string> Negative = new() { "Disconnect", "Unpair", "Delete", "Cancel", "No", "Deny" };

    // Every label any popup can show. The shared item width is measured from this whole set, not
    // from whichever items one popup has, so a 1-item and a 3-item popup are equally wide.
    private static readonly string[] AllLabels = Positive.Concat(Negative).Concat(new[] { "Copy", "Edit", "Image", "File" }).ToArray();

    private const double ItemFontSize = 15;

    private readonly Panel _host;
    private Border? _card;
    private Action? _onDismiss;

    public DarkPopup(Panel host) => _host = host;

    public bool IsOpen => _card != null;

    /// <summary>The brush a button/menu item labelled <paramref name="label"/> reads in, by
    /// meaning rather than position: affirmative green, destructive or negative red, else muted.
    /// Also used by <see cref="DarkDialog"/> so dialog buttons match the menus.</summary>
    public static Brush ColorFor(string label) => (Brush)Application.Current.FindResource(
        Positive.Contains(label) ? "ActionPositive" : Negative.Contains(label) ? "ActionNegative" : "TextMuted");

    /// <summary>True if <paramref name="element"/> is part of the open popup's card.</summary>
    public bool IsInside(DependencyObject? element)
    {
        for (var current = element; current != null; current = GetParent(current))
        {
            if (ReferenceEquals(current, _card)) return true;
        }
        return false;
    }

    /// <summary>Opens a popup with one row per item, replacing any popup already open.
    /// <paramref name="onSelected"/> gets the clicked item's label; <paramref name="onDismiss"/>
    /// runs once the popup closes for any reason (used to clear a highlighted row).</summary>
    public void Show(IReadOnlyList<string> items, Action<string> onSelected, Action? onDismiss = null)
    {
        Dismiss();
        var itemWidth = FixedItemWidth();
        var stack = new StackPanel();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                stack.Children.Add(new Border
                {
                    Height = 1, Width = itemWidth, Background = (Brush)Application.Current.FindResource("DividerPopup")
                });
            }
            var label = items[i];
            var item = new Border
            {
                Width = itemWidth,
                Padding = new Thickness(0, 13, 0, 13),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label, FontSize = ItemFontSize, Foreground = ColorFor(label),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            var pressed = (Brush)Application.Current.FindResource("BgPopupItemPressed");
            item.MouseEnter += (_, _) => item.Background = pressed;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Dismiss();
                onSelected(label);
            };
            stack.Children.Add(item);
        }

        _card = new Border
        {
            Background = (Brush)Application.Current.FindResource("BgPopup"),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
            Child = stack
        };
        _onDismiss = onDismiss;
        _host.Children.Add(_card);
    }

    /// <summary>Closes the open popup, if any. Safe to call when none is open.</summary>
    public void Dismiss()
    {
        if (_card == null) return;
        _host.Children.Remove(_card);
        _card = null;
        var onDismiss = _onDismiss;
        _onDismiss = null;
        onDismiss?.Invoke();
    }

    /// <summary>Widest label of <see cref="AllLabels"/>, plus 3 px padding each side, times 1.5 —
    /// the same formula the Android popup uses.</summary>
    private double FixedItemWidth()
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(_host).PixelsPerDip;
        var typeface = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var widest = AllLabels.Max(label => new FormattedText(label, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, ItemFontSize, Brushes.White, pixelsPerDip).WidthIncludingTrailingWhitespace);
        return Math.Ceiling((widest + 6) * 1.5);
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
