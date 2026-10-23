using System;
using System.Collections.Generic;
using System.Linq;
using Guard;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Protocol;

/// <summary>The public catalog and the handlers share one definition.</summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);

    public void Add(
        string name,
        string description,
        JObject properties,
        string[] required,
        Func<JObject, object> execute,
        bool changesModel = false,
        bool usesCad = true,
        bool supportsItems = false)
    {
        ThrowIf.Argument.IsNull(execute, nameof(execute));
        ThrowIf.Argument.IsNull(required, nameof(required));
        ThrowIf.Argument.IsNull(properties, nameof(properties));
        ThrowIf.Argument.IsNull(description, nameof(description));
        ThrowIf.Argument.IsNull(name, nameof(name));
        _tools.Add(name, new McpTool(name, description, properties, required, execute, changesModel, usesCad, supportsItems));
    }

    public object List()
    {
        return new { tools = _tools.Values.Select(x => x.Describe()).ToArray() };
    }

    public McpTool Get(string name)
    {
        ThrowIf.Argument.IsNull(name, nameof(name));
        return _tools.TryGetValue(name, out var tool)
            ? tool
            : throw new ArgumentException($"Unknown tool '{name}'. Call tools/list.");
    }
}

public sealed class McpTool
{
    private readonly string _name;
    private readonly string _description;
    private readonly JObject _properties;
    private readonly string[] _required;
    private readonly Func<JObject, object> _execute;
    private readonly bool _changesModel;
    private readonly bool _supportsItems;

    public McpTool(
        string name,
        string description,
        JObject properties,
        string[] required,
        Func<JObject, object> execute,
        bool changesModel,
        bool usesCad,
        bool supportsItems)
    {
        ThrowIf.Argument.IsNull(execute, nameof(execute));
        ThrowIf.Argument.IsNull(required, nameof(required));
        ThrowIf.Argument.IsNull(properties, nameof(properties));
        ThrowIf.Argument.IsNull(description, nameof(description));
        ThrowIf.Argument.IsNull(name, nameof(name));
        _name = name;
        _description = description;
        _properties = properties;
        _required = required;
        _execute = execute;
        _changesModel = changesModel;
        UsesCad = usesCad;
        _supportsItems = supportsItems;
    }

    public bool UsesCad { get; }

    public object Describe()
    {
        var schema = new JObject
        {
            ["type"] = "object", ["properties"] = _properties.DeepClone(),
            ["required"] = JArray.FromObject(_required), ["additionalProperties"] = false
        };
        if (_supportsItems)
        {
            schema = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("items"),
                ["properties"] = new JObject
                {
                    ["items"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 100, ["items"] = schema
                    }
                }
            };
        }
        return new
        {
            name = _name,
            description = _description,
            inputSchema = schema,
            annotations = new { readOnlyHint = !_changesModel, openWorldHint = false }
        };
    }

    public object Execute(JObject arguments)
    {
        ThrowIf.Argument.IsNull(arguments, nameof(arguments));
        if (_supportsItems)
        {
            if (arguments.Count != 1 || arguments["items"] is not JArray items ||
                items.Count is < 1 or > 100 || items.Any(x => x is not JObject))
            {
                throw new ArgumentException("Передайте только items: массив из 1–100 объектов с аргументами.");
            }
            foreach (var item in items.Cast<JObject>())
            {
                ValidateArguments(item);
            }
            var results = new JArray();
            try
            {
                foreach (var item in items.Cast<JObject>())
                {
                    results.Add(JToken.FromObject(_execute(item), Newtonsoft.Json.JsonSerializer.Create(new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore })));
                }
            }
            catch (Exception exception)
            {
                exception.Data["completedResults"] = results;
                exception.Data["failedItemIndex"] = results.Count;
                throw;
            }
            return new { results };
        }
        ValidateArguments(arguments);
        return _execute(arguments);
    }

    private void ValidateArguments(JObject arguments)
    {
        var unknown = arguments.Properties().Select(x => x.Name).Except(_properties.Properties().Select(x => x.Name))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException("Unknown arguments: " + string.Join(", ", unknown));
        }

        foreach (var name in _required)
        {
            if (arguments[name] == null || arguments[name]!.Type == JTokenType.Null)
            {
                throw new ArgumentException($"Required argument '{name}' is missing.");
            }
        }

    }
}

public static class McpSchema
{
    public static JObject String(string description)
    {
        ThrowIf.Argument.IsNull(description, nameof(description));
        return new JObject { ["type"] = "string", ["description"] = description };
    }

    public static JObject Number(string description)
    {
        ThrowIf.Argument.IsNull(description, nameof(description));
        return new JObject { ["type"] = "number", ["description"] = description };
    }

    public static JObject Integer(string description)
    {
        ThrowIf.Argument.IsNull(description, nameof(description));
        return new JObject { ["type"] = "integer", ["description"] = description };
    }

    public static JObject Boolean(string description)
    {
        ThrowIf.Argument.IsNull(description, nameof(description));
        return new JObject { ["type"] = "boolean", ["description"] = description };
    }

    public static JObject Properties(string description)
    {
        ThrowIf.Argument.IsNull(description, nameof(description));
        return new JObject { ["type"] = "object", ["description"] = description };
    }

    public static JObject Point()
    {
        return new JObject
        {
            ["type"] = "array", ["items"] = new JObject { ["type"] = "number" },
            ["minItems"] = 2, ["maxItems"] = 3, ["description"] = "[x,y] or [x,y,z], millimetres in the source DWG."
        };
    }

    public static JObject Array(JObject item)
    {
        ThrowIf.Argument.IsNull(item, nameof(item));
        return new JObject { ["type"] = "array", ["items"] = item };
    }
}
