using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;

namespace Jarvis.Services;

public enum InteractiveQuestionType { SingleChoice, MultipleChoice, YesNo, Text, Multiline }
public sealed record InteractiveQuestionOption(string Value, string Label);
public sealed record InteractiveQuestion(string Id, string Label, InteractiveQuestionType Type, bool Required, IReadOnlyList<InteractiveQuestionOption> Options, bool AllowOther);
public sealed record InteractiveQuestionForm(string FormId, string Title, string? Description, IReadOnlyList<InteractiveQuestion> Questions);

public static class InteractiveQuestionSchema
{
    private static readonly Regex IdPattern = new(@"^[a-zA-Z][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    public static InteractiveQuestionForm Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The question form must be an object.");
        var formId = ReadString(root, "form_id", 64);
        var title = ReadString(root, "title", 160);
        var description = root.TryGetProperty("description", out var descriptionNode) && descriptionNode.ValueKind == JsonValueKind.String ? descriptionNode.GetString()?.Trim() : null;
        if (!IdPattern.IsMatch(formId)) throw new InvalidDataException("The question form ID is invalid.");
        if (description?.Length > 500) throw new InvalidDataException("The question form description is too long.");
        if (!root.TryGetProperty("questions", out var questionsNode) || questionsNode.ValueKind != JsonValueKind.Array || questionsNode.GetArrayLength() is < 1 or > 8)
            throw new InvalidDataException("A question form must contain 1 to 8 questions.");

        var questions = new List<InteractiveQuestion>();
        foreach (var node in questionsNode.EnumerateArray())
        {
            var id = ReadString(node, "id", 64);
            var label = ReadString(node, "label", 240);
            if (!IdPattern.IsMatch(id) || questions.Any(question => question.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Question IDs must be unique safe identifiers.");
            var typeText = node.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()?.Trim() ?? "single_choice"
                : "single_choice";
            if (typeText.Length > 32) throw new InvalidDataException("The question type is too long.");
            var type = typeText switch
            {
                "single_choice" => InteractiveQuestionType.SingleChoice,
                "multiple_choice" => InteractiveQuestionType.MultipleChoice,
                "yes_no" => InteractiveQuestionType.YesNo,
                "text" => InteractiveQuestionType.Text,
                "multiline" => InteractiveQuestionType.Multiline,
                _ => throw new InvalidDataException($"Unsupported question type '{typeText}'.")
            };
            var required = node.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.True;
            var allowOther = node.TryGetProperty("allow_other", out var otherNode) && otherNode.ValueKind == JsonValueKind.True;
            var options = new List<InteractiveQuestionOption>();
            if (node.TryGetProperty("options", out var optionsNode))
            {
                if (optionsNode.ValueKind != JsonValueKind.Array || optionsNode.GetArrayLength() > 12) throw new InvalidDataException("Question options must be an array with at most 12 items.");
                foreach (var option in optionsNode.EnumerateArray())
                {
                    var value = ReadString(option, "value", 100);
                    var optionLabel = ReadString(option, "label", 160);
                    if (options.Any(existing => existing.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Question option values must be unique.");
                    options.Add(new(value, optionLabel));
                }
            }
            if (type is InteractiveQuestionType.SingleChoice or InteractiveQuestionType.MultipleChoice && options.Count < 2)
                throw new InvalidDataException("Choice questions require at least two options.");
            if (type is not (InteractiveQuestionType.SingleChoice or InteractiveQuestionType.MultipleChoice) && (options.Count > 0 || allowOther))
                throw new InvalidDataException("Only choice questions may define options or an Other field.");
            questions.Add(new(id, label, type, required, options, allowOther));
        }
        return new(formId, title, description, questions);
    }

    private static string ReadString(JsonElement node, string property, int maxLength)
    {
        if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Question form property '{property}' is required.");
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length is 0 || text.Length > maxLength || text.Any(char.IsControl)) throw new InvalidDataException($"Question form property '{property}' is invalid.");
        return text;
    }
}
