using System.IO;

namespace Jarvis.Services;

public static class SyntaxLanguageCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "Python", [".java"] = "Java", [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".ts"] = "JavaScript", [".tsx"] = "JavaScript", [".cs"] = "C#", [".c"] = "C++", [".h"] = "C++", [".cpp"] = "C++", [".hpp"] = "C++",
        [".html"] = "HTML", [".htm"] = "HTML", [".css"] = "CSS", [".scss"] = "CSS", [".json"] = "JavaScript",
        [".xml"] = "XML", [".xaml"] = "XML", [".csproj"] = "XML", [".props"] = "XML", [".targets"] = "XML",
        [".yaml"] = "YAML", [".yml"] = "YAML", [".md"] = "Markdown", [".ps1"] = "PowerShell", [".psm1"] = "PowerShell",
        [".sql"] = "TSQL", [".properties"] = "Java", [".ini"] = "INI", [".cfg"] = "INI", [".conf"] = "INI",
        [".sh"] = "Bash", [".bash"] = "Bash", [".zsh"] = "Bash", [".bat"] = "Batch", [".cmd"] = "Batch"
    };

    public static string? DefinitionFor(string path) => Definitions.GetValueOrDefault(Path.GetExtension(path));
    public static IReadOnlyCollection<string> SupportedExtensions => Definitions.Keys.ToArray();
}
