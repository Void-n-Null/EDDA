using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// Descriptor for a discovered tool.
/// </summary>
public record LlmToolDescriptor
{
    /// <summary>Tool name for LLM function calling.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description for LLM.</summary>
    public required string Description { get; init; }

    /// <summary>JSON schema for parameters.</summary>
    public required JsonElement ParameterSchema { get; init; }

    /// <summary>The tool instance.</summary>
    public required ILlmTool Instance { get; init; }

    /// <summary>The concrete tool type.</summary>
    public required Type ToolType { get; init; }

    /// <summary>
    /// Convert to OpenAI-compatible tool definition.
    /// </summary>
    public object ToOpenAiToolDefinition() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = ParameterSchema
        }
    };
}

/// <summary>
/// Discovers LLM tools in assemblies by scanning for ILlmTool implementations.
/// </summary>
public class ToolDiscovery
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<ToolDiscovery>? _logger;
    private readonly Dictionary<string, LlmToolDescriptor> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolDiscovery(IServiceProvider? serviceProvider = null, ILogger<ToolDiscovery>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// All discovered tools.
    /// </summary>
    public IReadOnlyDictionary<string, LlmToolDescriptor> Tools => _tools;

    /// <summary>
    /// Get tool definitions in OpenAI-compatible format.
    /// </summary>
    public IEnumerable<object> GetOpenAiToolDefinitions()
    {
        return _tools.Values.Select(t => t.ToOpenAiToolDefinition());
    }

    /// <summary>
    /// Discover all ILlmTool implementations in an assembly.
    /// </summary>
    public ToolDiscovery FromAssembly(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Where(t => typeof(ILlmTool).IsAssignableFrom(t));

        foreach (var toolType in toolTypes)
        {
            try
            {
                RegisterToolType(toolType);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to register tool type {Type}", toolType.FullName);
            }
        }

        return this;
    }

    /// <summary>
    /// Discover all ILlmTool implementations in multiple assemblies.
    /// </summary>
    public ToolDiscovery FromAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            FromAssembly(assembly);
        }
        return this;
    }

    /// <summary>
    /// Register a specific tool type.
    /// </summary>
    public ToolDiscovery Register<TTool>() where TTool : ILlmTool
    {
        RegisterToolType(typeof(TTool));
        return this;
    }

    /// <summary>
    /// Register a tool instance directly.
    /// </summary>
    public ToolDiscovery Register(ILlmTool tool)
    {
        var descriptor = new LlmToolDescriptor
        {
            Name = tool.Name,
            Description = tool.Description,
            ParameterSchema = tool.GetParameterSchema(),
            Instance = tool,
            ToolType = tool.GetType()
        };

        _tools[tool.Name] = descriptor;
        _logger?.LogDebug("Registered tool: {Name}", tool.Name);

        return this;
    }

    /// <summary>
    /// Try to get a tool by name.
    /// </summary>
    public bool TryGetTool(string name, out LlmToolDescriptor? descriptor)
    {
        return _tools.TryGetValue(name, out descriptor);
    }

    private void RegisterToolType(Type toolType)
    {
        ILlmTool? instance = null;

        // Try to resolve from DI first
        if (_serviceProvider is not null)
        {
            instance = _serviceProvider.GetService(toolType) as ILlmTool;
        }

        // Fall back to Activator
        instance ??= CreateInstance(toolType);

        if (instance is null)
        {
            _logger?.LogWarning("Could not create instance of tool type {Type}", toolType.FullName);
            return;
        }

        Register(instance);
    }

    private ILlmTool? CreateInstance(Type toolType)
    {
        // Try parameterless constructor first
        var parameterlessCtor = toolType.GetConstructor(Type.EmptyTypes);
        if (parameterlessCtor is not null)
        {
            return Activator.CreateInstance(toolType) as ILlmTool;
        }

        // Try to resolve constructor dependencies from DI
        if (_serviceProvider is not null)
        {
            var ctors = toolType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length);

            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var args = new object?[parameters.Length];
                var canResolve = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    var service = _serviceProvider.GetService(parameters[i].ParameterType);
                    if (service is null && !parameters[i].HasDefaultValue)
                    {
                        canResolve = false;
                        break;
                    }
                    args[i] = service ?? parameters[i].DefaultValue;
                }

                if (canResolve)
                {
                    return ctor.Invoke(args) as ILlmTool;
                }
            }
        }

        _logger?.LogWarning(
            "Tool type {Type} has no parameterless constructor and dependencies could not be resolved",
            toolType.FullName);

        return null;
    }
}
