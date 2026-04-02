using System.Reflection;
using System.Text.Json;

namespace CalendarParse.Cli.Services;

/// <summary>
/// Loads and resolves LLM prompt templates from the embedded Prompts/prompts.json resource.
/// Templates use {variableName} placeholders for runtime substitution.
/// Array values in the JSON are joined with newlines to form multi-line prompts.
/// </summary>
internal static class PromptService
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> _prompts =
        new(LoadPrompts, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns the named prompt template with all {key} placeholders replaced by their values.
    /// </summary>
    public static string Get(string key, IReadOnlyDictionary<string, string>? vars = null)
    {
        if (!_prompts.Value.TryGetValue(key, out var template))
            throw new KeyNotFoundException($"Prompt template '{key}' not found in prompts.json");

        if (vars == null || vars.Count == 0)
            return template;

        foreach (var (varKey, value) in vars)
            template = template.Replace("{" + varKey + "}", value);

        return template;
    }

    private static IReadOnlyDictionary<string, string> LoadPrompts()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CalendarParse.Cli.Prompts.prompts.json")
            ?? throw new InvalidOperationException(
                "Embedded resource 'CalendarParse.Cli.Prompts.prompts.json' not found. " +
                "Ensure the file is included as EmbeddedResource in the .csproj.");

        using var doc = JsonDocument.Parse(stream);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string value;
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                // Array of lines — join with newlines (no trailing newline)
                var lines = prop.Value.EnumerateArray()
                    .Select(el => el.GetString() ?? "")
                    .ToList();
                value = string.Join("\n", lines);
            }
            else
            {
                value = prop.Value.GetString() ?? "";
            }
            dict[prop.Name] = value;
        }

        return dict;
    }
}
