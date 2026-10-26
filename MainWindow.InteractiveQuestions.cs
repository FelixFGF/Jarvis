using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Jarvis.Services;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Jarvis;

public partial class MainWindow
{
    private sealed class QuestionInputState(InteractiveQuestion question, FrameworkElement control, TextBox? other)
    {
        public InteractiveQuestion Question { get; } = question;
        public FrameworkElement Control { get; } = control;
        public TextBox? Other { get; } = other;
    }

    private async Task<string> AskInteractiveQuestionsAsync(JsonElement arguments, CancellationToken token)
    {
        var form = InteractiveQuestionSchema.Parse(arguments);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<QuestionInputState>();
        var german = _config.UiLanguage == "de";
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = form.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(form.Description)) body.Children.Add(new TextBlock { Text = form.Description, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        var validation = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 160)), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

        foreach (var question in form.Questions)
        {
            var section = new StackPanel();
            section.Children.Add(new TextBlock { Text = question.Label + (question.Required ? " *" : ""), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            FrameworkElement input;
            TextBox? other = null;
            if (question.Type is InteractiveQuestionType.SingleChoice or InteractiveQuestionType.YesNo)
            {
                var choices = question.Type == InteractiveQuestionType.YesNo
                    ? new[] { new InteractiveQuestionOption("yes", german ? "Ja" : "Yes"), new InteractiveQuestionOption("no", german ? "Nein" : "No") }
                    : question.Options;
                var choicePanel = new StackPanel();
                foreach (var option in choices)
                    choicePanel.Children.Add(new RadioButton { Content = option.Label, Tag = option.Value, GroupName = form.FormId + "_" + question.Id, Margin = new Thickness(2, 4, 2, 4), Padding = new Thickness(5, 3, 5, 3) });
                if (question.AllowOther)
                {
                    var otherRadio = new RadioButton { Content = german ? "Andere" : "Other", Tag = "__other", GroupName = form.FormId + "_" + question.Id, Margin = new Thickness(0, 2, 0, 2) };
                    other = new TextBox { MaxLength = 500, Margin = new Thickness(28, 6, 0, 2), Visibility = Visibility.Collapsed, Opacity = 0, MinHeight = 36, ToolTip = german ? "Eigene Antwort eingeben …" : "Enter your own answer…" };
                    otherRadio.Checked += (_, _) => RevealOther(other, true);
                    otherRadio.Unchecked += (_, _) => RevealOther(other, false);
                    choicePanel.Children.Add(otherRadio); choicePanel.Children.Add(other);
                }
                input = choicePanel;
            }
            else if (question.Type == InteractiveQuestionType.MultipleChoice)
            {
                var choicePanel = new StackPanel();
                foreach (var option in question.Options) choicePanel.Children.Add(new CheckBox { Content = option.Label, Tag = option.Value, Margin = new Thickness(2, 4, 2, 4), Padding = new Thickness(5, 3, 5, 3) });
                if (question.AllowOther)
                {
                    var otherCheck = new CheckBox { Content = german ? "Andere" : "Other", Tag = "__other", Margin = new Thickness(0, 2, 0, 2) };
                    other = new TextBox { MaxLength = 500, Margin = new Thickness(28, 6, 0, 2), Visibility = Visibility.Collapsed, Opacity = 0, MinHeight = 36, ToolTip = german ? "Eigene Antwort eingeben …" : "Enter your own answer…" };
                    otherCheck.Checked += (_, _) => RevealOther(other, true);
                    otherCheck.Unchecked += (_, _) => RevealOther(other, false);
                    choicePanel.Children.Add(otherCheck); choicePanel.Children.Add(other);
                }
                input = choicePanel;
            }
            else
            {
                input = new TextBox { MaxLength = question.Type == InteractiveQuestionType.Multiline ? 4000 : 500, AcceptsReturn = question.Type == InteractiveQuestionType.Multiline, TextWrapping = TextWrapping.Wrap, MinHeight = question.Type == InteractiveQuestionType.Multiline ? 90 : 34, VerticalScrollBarVisibility = question.Type == InteractiveQuestionType.Multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            }
            section.Children.Add(input);
            body.Children.Add(new Border { Child = section, Background = new SolidColorBrush(Color.FromRgb(31, 32, 43)), BorderBrush = new SolidColorBrush(Color.FromRgb(62, 58, 86)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Margin = new Thickness(0, 5, 0, 7) });
            states.Add(new(question, input, other));
        }
        body.Children.Add(validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var submit = new Button { Content = german ? "Antworten senden" : "Submit answers", Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 7, 0), IsDefault = true };
        var skip = new Button { Content = german ? "Optionale überspringen" : "Skip optional", Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 7, 0) };
        var cancel = new Button { Content = german ? "Abbrechen" : "Cancel", Padding = new Thickness(11, 6, 11, 6), IsCancel = false };
        buttons.Children.Add(submit); buttons.Children.Add(skip); buttons.Children.Add(cancel); body.Children.Add(buttons);
        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(27, 28, 37)), BorderBrush = new SolidColorBrush(Color.FromRgb(82, 67, 128)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 7, 0, 7), MaxWidth = 720, Child = body };
        Messages.Children.Add(card); ChatStartPanel.Visibility = Visibility.Collapsed; ChatScroll.ScrollToEnd();

        string? BuildResult(bool skipOptional)
        {
            var answers = new Dictionary<string, object?>();
            foreach (var state in states)
            {
                object? answer = state.Question.Type switch
                {
                    InteractiveQuestionType.Text or InteractiveQuestionType.Multiline => ((TextBox)state.Control).Text.Trim(),
                    InteractiveQuestionType.SingleChoice or InteractiveQuestionType.YesNo => ReadRadioAnswer((StackPanel)state.Control, state.Other),
                    InteractiveQuestionType.MultipleChoice => ReadCheckboxAnswers((StackPanel)state.Control, state.Other),
                    _ => null
                };
                var otherSelected = state.Other?.Visibility == Visibility.Visible;
                var empty = answer is null || answer is string text && text.Length == 0 || otherSelected && string.IsNullOrWhiteSpace(state.Other?.Text);
                if (state.Question.Required && empty)
                {
                    validation.Text = (german ? "Bitte beantworte das Pflichtfeld: " : "Please answer the required question: ") + state.Question.Label;
                    validation.Visibility = Visibility.Visible;
                    return null;
                }
                if (!empty && (!skipOptional || state.Question.Required)) answers[state.Question.Id] = answer;
            }
            return JsonSerializer.Serialize(new { ok = true, form_id = form.FormId, answers });
        }

        void Finish(string result)
        {
            if (!completion.TrySetResult(result)) return;
            submit.IsEnabled = skip.IsEnabled = cancel.IsEnabled = false;
            validation.Text = german ? "Antworten übernommen." : "Answers submitted.";
            validation.Foreground = (Brush)FindResource("Muted"); validation.Visibility = Visibility.Visible;
        }
        submit.Click += (_, _) => { var result = BuildResult(false); if (result is not null) Finish(result); };
        skip.Click += (_, _) => { var result = BuildResult(true); if (result is not null) Finish(result); };
        cancel.Click += (_, _) => Finish(JsonSerializer.Serialize(new { ok = false, cancelled = true, form_id = form.FormId }));
        using var registration = token.Register(() => Dispatcher.BeginInvoke(() => completion.TrySetCanceled(token)));
        return await completion.Task;
    }

    private static object? ReadRadioAnswer(StackPanel panel, TextBox? other)
    {
        var selected = panel.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true)?.Tag?.ToString();
        if (selected is null) return null;
        string? customText = selected == "__other" ? other?.Text.Trim() ?? "" : null;
        return new { selected = selected == "__other" ? "other" : selected, custom_text = customText };
    }

    private static object? ReadCheckboxAnswers(StackPanel panel, TextBox? other)
    {
        var selected = panel.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => box.Tag?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        if (selected.Length == 0) return null;
        string? customText = selected.Contains("__other", StringComparer.Ordinal) ? other?.Text.Trim() ?? "" : null;
        return new { selected = selected.Select(value => value == "__other" ? "other" : value).ToArray(), custom_text = customText };
    }

    private static void RevealOther(TextBox textBox, bool show)
    {
        if (show) textBox.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        if (!show) animation.Completed += (_, _) => textBox.Visibility = Visibility.Collapsed;
        textBox.BeginAnimation(UIElement.OpacityProperty, animation);
        if (show) textBox.Focus();
    }
}
