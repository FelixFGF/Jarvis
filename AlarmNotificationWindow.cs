using Jarvis.Services;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Jarvis;

public sealed class AlarmNotificationWindow : Window
{
    private readonly TextBlock _state;
    private bool _resolved;
    public string ReminderId { get; }
    public bool IsResolved => _resolved;
    public event EventHandler? StopRequested;
    public event EventHandler? SnoozeRequested;

    public AlarmNotificationWindow(ReminderRecord reminder, DateTimeOffset firedAt, bool german)
    {
        ReminderId = reminder.Id;
        Title = german ? "Jarvis Erinnerung" : "Jarvis reminder";
        Width = 470; MinHeight = 330; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true; ShowActivated = true; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(16, 17, 21));
        AutomationProperties.SetName(this, german ? $"Aktiver Alarm: {reminder.Title}" : $"Active alarm: {reminder.Title}");

        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "⏰  " + reminder.Title, FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        var purpose = string.IsNullOrWhiteSpace(reminder.Description) ? reminder.Title : reminder.Description;
        body.Children.Add(Line(german ? "Erinnerung" : "Reminder", purpose, 16));
        body.Children.Add(Line(german ? "Geplant" : "Scheduled", reminder.DueAt.LocalDateTime.ToString("f"), 8));
        body.Children.Add(Line(german ? "Ausgelöst" : "Fired", firedAt.LocalDateTime.ToString("T"), 4));
        _state = Line(german ? "Status" : "Status", german ? "Alarm klingelt" : "Alarm ringing", 4);
        body.Children.Add(_state);

        var actions = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        var stop = new Button { Content = german ? "Alarm stoppen" : "Stop alarm", Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(0, 0, 5, 0), IsDefault = true };
        var snooze = new Button { Content = german ? "5 Minuten später" : "Snooze 5 minutes", Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(5, 0, 0, 0) };
        stop.Click += (_, _) => { if (_resolved) return; _resolved = true; _state.Text = german ? "Status: Gestoppt" : "Status: Stopped"; StopRequested?.Invoke(this, EventArgs.Empty); };
        snooze.Click += (_, _) => { if (_resolved) return; _resolved = true; _state.Text = german ? "Status: 5 Minuten pausiert" : "Status: Snoozed for 5 minutes"; SnoozeRequested?.Invoke(this, EventArgs.Empty); };
        Grid.SetColumn(snooze, 1); actions.Children.Add(stop); actions.Children.Add(snooze); body.Children.Add(actions);
        Content = new Border { Background = new SolidColorBrush(Color.FromRgb(25, 26, 34)), BorderBrush = new SolidColorBrush(Color.FromRgb(103, 85, 190)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(2), Child = body };
    }

    public void MarkResolved() => _resolved = true;

    private static TextBlock Line(string label, string value, double top) => new()
    {
        Text = $"{label}: {value}", Foreground = new SolidColorBrush(Color.FromRgb(207, 205, 220)),
        TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, top, 0, 0)
    };
}
