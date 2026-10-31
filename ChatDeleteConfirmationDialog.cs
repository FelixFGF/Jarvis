using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.Services;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace Jarvis;

public sealed class ChatDeleteConfirmationDialog : Window
{
    private readonly Button _cancelButton;

    public ChatDeleteConfirmationDialog(string conversationTitle, string language)
    {
        string T(string key, string fallback) => UiLocalization.Get(key, language, fallback);
        Title = T("ChatDelete.WindowTitle", "Delete conversation");
        Width = 500;
        Height = 310;
        MinWidth = 440;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        KeyDown += OnDialogKeyDown;

        var shell = new Border
        {
            Background = Brush("#191A22"),
            BorderBrush = Brush("#514A72"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 28,
                ShadowDepth = 8,
                Opacity = 0.5
            }
        };
        Content = shell;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.Child = layout;

        var heading = new DockPanel { LastChildFill = true };
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);
        var close = new Button
        {
            Content = "×",
            FontSize = 20,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(12, -5, -5, 0),
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            ToolTip = T("ChatDelete.Cancel", "Cancel")
        };
        close.Click += (_, _) => Cancel();
        DockPanel.SetDock(close, Dock.Right);
        heading.Children.Add(close);
        var icon = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = Brush("#3B222D"),
            BorderBrush = Brush("#7D3D50"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = "🗑",
                FontFamily = new MediaFontFamily("Segoe UI Emoji"),
                FontSize = 21,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(icon, Dock.Left);
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock
        {
            Text = T("ChatDelete.Title", "Delete conversation?"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F3F1F7"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });

        var copy = new StackPanel { Margin = new Thickness(60, 20, 0, 16) };
        Grid.SetRow(copy, 1);
        layout.Children.Add(copy);
        copy.Children.Add(new TextBlock
        {
            Text = conversationTitle,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#C6BAFF"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380
        });
        copy.Children.Add(new TextBlock
        {
            Text = T("ChatDelete.Explanation", "This conversation and its locally stored messages will be permanently removed."),
            Foreground = Brush("#B3B0BE"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            LineHeight = 20
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        _cancelButton = new Button
        {
            Content = T("ChatDelete.Cancel", "Cancel"),
            MinWidth = 110,
            Padding = new Thickness(16, 9, 16, 9),
            IsDefault = true
        };
        _cancelButton.Click += (_, _) => Cancel();
        AutomationProperties.SetName(_cancelButton, T("ChatDelete.CancelAutomation", "Cancel deletion"));
        actions.Children.Add(_cancelButton);
        var delete = new Button
        {
            Content = T("ChatDelete.Delete", "Delete"),
            MinWidth = 110,
            Padding = new Thickness(16, 9, 16, 9),
            Margin = new Thickness(10, 3, 3, 3),
            Background = Brush("#A83F59"),
            BorderBrush = Brush("#D4687F"),
            FontWeight = FontWeights.SemiBold
        };
        delete.Click += (_, _) => Confirm();
        AutomationProperties.SetName(delete, T("ChatDelete.Delete", "Delete") + " " + conversationTitle);
        actions.Children.Add(delete);

        Loaded += (_, _) => _cancelButton.Focus();
    }

    public static bool IsConfirmed(bool? dialogResult) => dialogResult == true;

    private void Confirm()
    {
        DialogResult = true;
        Close();
    }

    private void Cancel()
    {
        DialogResult = false;
        Close();
    }

    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Cancel();
    }

    private static SolidColorBrush Brush(string value) => new((MediaColor)MediaColorConverter.ConvertFromString(value));
}
