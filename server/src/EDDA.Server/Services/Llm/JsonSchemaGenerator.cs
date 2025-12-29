using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// Generates JSON Schema from .NET types for OpenAI-compatible tool definitions.
/// </summary>
public static class JsonSchemaGenerator
{
    /// <summary>
    /// Generate a JSON schema for a type.
    /// </summary>
    public static JsonElement Generate<T>() => Generate(typeof(T));

    /// <summary>
    /// Generate a JSON schema for a type.
    /// </summary>
    public static JsonElement Generate(Type type)
    {
        var schema = GenerateSchema(type);
        var json = JsonSerializer.Serialize(schema);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Dictionary<string, object> GenerateSchema(Type type)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;

            var propSchema = GeneratePropertySchema(prop);
            var jsonName = ToSnakeCase(prop.Name);
            properties[jsonName] = propSchema;

            // Non-nullable reference types and non-nullable value types are required
            if (IsRequired(prop))
            {
                required.Add(jsonName);
            }
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object> GeneratePropertySchema(PropertyInfo prop)
    {
        var schema = new Dictionary<string, object>();
        var propType = prop.PropertyType;

        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(propType);
        if (underlyingType is not null)
        {
            propType = underlyingType;
        }

        // Get description from attribute
        var description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (description is not null)
        {
            schema["description"] = description;
        }

        // Map .NET types to JSON Schema types
        if (propType == typeof(string))
        {
            schema["type"] = "string";
        }
        else if (propType == typeof(int) || propType == typeof(long) || propType == typeof(short) || propType == typeof(byte))
        {
            schema["type"] = "integer";
        }
        else if (propType == typeof(float) || propType == typeof(double) || propType == typeof(decimal))
        {
            schema["type"] = "number";
        }
        else if (propType == typeof(bool))
        {
            schema["type"] = "boolean";
        }
        else if (propType.IsEnum)
        {
            schema["type"] = "string";
            schema["enum"] = Enum.GetNames(propType).Select(ToSnakeCase).ToArray();
        }
        else if (propType.IsArray || (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(List<>)))
        {
            schema["type"] = "array";
            var elementType = propType.IsArray
                ? propType.GetElementType()!
                : propType.GetGenericArguments()[0];
            schema["items"] = GeneratePropertySchemaForType(elementType);
        }
        else if (propType.IsClass && propType != typeof(object))
        {
            // Nested object
            return GenerateSchema(propType);
        }
        else
        {
            // Fallback
            schema["type"] = "string";
        }

        return schema;
    }

    private static Dictionary<string, object> GeneratePropertySchemaForType(Type type)
    {
        var schema = new Dictionary<string, object>();

        if (type == typeof(string))
            schema["type"] = "string";
        else if (type == typeof(int) || type == typeof(long))
            schema["type"] = "integer";
        else if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            schema["type"] = "number";
        else if (type == typeof(bool))
            schema["type"] = "boolean";
        else if (type.IsClass && type != typeof(object))
            return GenerateSchema(type);
        else
            schema["type"] = "string";

        return schema;
    }

    private static bool IsRequired(PropertyInfo prop)
    {
        var propType = prop.PropertyType;

        // Nullable<T> is optional
        if (Nullable.GetUnderlyingType(propType) is not null)
            return false;

        // Check for nullable reference type annotation
        var nullabilityContext = new NullabilityInfoContext();
        var nullabilityInfo = nullabilityContext.Create(prop);

        if (nullabilityInfo.WriteState == NullabilityState.Nullable)
            return false;

        // Non-nullable reference types and value types are required
        return true;
    }

    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(input[0]));

        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(input[i]));
            }
            else
            {
                result.Append(input[i]);
            }
        }

        return result.ToString();
    }
}
