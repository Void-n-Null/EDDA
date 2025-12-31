using System.Reflection;

namespace EDDA.Server.Agent.Prompts;

/// <summary>
/// Loads prompt templates from embedded resources.
/// Templates are compiled into the DLL, versioned with code, and always available.
/// </summary>
public static class PromptLoader
{
    private const string ResourcePrefix = "EDDA.Server.Agent.Prompts.Templates.";
    private static readonly Assembly Assembly = typeof(PromptLoader).Assembly;

    /// <summary>
    /// Load a prompt template by name.
    /// </summary>
    /// <param name="name">Template name (e.g., "system.md")</param>
    /// <returns>Template content.</returns>
    /// <exception cref="InvalidOperationException">If template not found.</exception>
    public static string Load(string name)
    {
        var resourceName = ResourcePrefix + name;

        using var stream = Assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            var available = Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix))
                .Select(n => n[ResourcePrefix.Length..]);

            throw new InvalidOperationException(
                $"Prompt template '{name}' not found. " +
                $"Available templates: {string.Join(", ", available)}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Try to load a prompt template by name.
    /// </summary>
    /// <param name="name">Template name (e.g., "system.md")</param>
    /// <param name="content">Template content if found.</param>
    /// <returns>True if template was found.</returns>
    public static bool TryLoad(string name, out string? content)
    {
        var resourceName = ResourcePrefix + name;

        using var stream = Assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            content = null;
            return false;
        }

        using var reader = new StreamReader(stream);
        content = reader.ReadToEnd();
        return true;
    }

    /// <summary>
    /// List all available prompt template names.
    /// </summary>
    public static IEnumerable<string> ListTemplates()
    {
        return Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix))
            .Select(n => n[ResourcePrefix.Length..]);
    }
}
