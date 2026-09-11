using System.Collections.Concurrent;
using System.Reflection;

namespace TaskManager.Infrastructure.AI;

/// <summary>Loads the embedded prompt templates from AI/Prompts/*.txt, cached after first read.</summary>
public static class PromptLoader
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Load(string fileName)
    {
        return Cache.GetOrAdd(fileName, static name =>
        {
            var assembly = typeof(PromptLoader).Assembly;
            var resourceName = $"TaskManager.Infrastructure.AI.Prompts.{name}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Prompt template '{name}' is not embedded in the assembly.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
