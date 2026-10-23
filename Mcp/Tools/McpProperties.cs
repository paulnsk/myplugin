using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Guard;
using Nanosoft.Utils.Point3d;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

public static class McpProperties
{
    private static readonly HashSet<string> InfrastructureProperties = new(StringComparer.Ordinal)
    {
        "AcId", "DbId", "DbIdPrp", "DbIdProp", "DbKitIdProp", "SerializationInProgress", "IsDbSerialization",
        "IsObjectAlive"
    };

    internal static bool IsValueType(Type type)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(Point3d);
    }

    public static PropertyInfo[] Editable(Type type)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        return Readable(type).Where(x => x.SetMethod?.IsPublic == true).ToArray();
    }

    public static PropertyInfo[] Readable(Type type)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(x => x.GetMethod?.IsPublic == true && x.GetIndexParameters().Length == 0 &&
                        !InfrastructureProperties.Contains(x.Name) && IsValueType(x.PropertyType) &&
                        x.GetCustomAttributes(true).Any(a =>
                            a.GetType().Name is "PropertyAttribute" or "UgoPropertyAttribute"
                                or "DataFieldPrimitiveAttribute"))
            .OrderBy(x => x.Name).ToArray();
    }

    public static IReadOnlyList<Action<object>> Prepare(Type type, JObject? values)
    {
        ThrowIf.Argument.IsNull(type, nameof(type));
        var setters = new List<Action<object>>();
        if (values == null)
        {
            return setters;
        }

        var allowed = Editable(type).ToDictionary(x => x.Name);
        // Изменение высоты этажа пересчитывает высоту помещений. Явно заданная высота помещений применяется последней, независимо от порядка JSON.
        var isFloor = typeof(Nanosoft.Acad.BuildingModel.IBuildingModelFloorContour).IsAssignableFrom(type);
        var orderedValues = values.Properties().OrderBy(x => isFloor && x.Name == nameof(Nanosoft.Acad.BuildingModel.IBuildingModelFloorContour.RoomFloorHeight));
        foreach (var entry in orderedValues)
        {
            if (!allowed.TryGetValue(entry.Name, out var property))
            {
                throw new ArgumentException(
                    $"'{entry.Name}' is not an editable property of {type.Name}. Call types_describe.");
            }

            var valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value;
            if (entry.Value.Type == JTokenType.Null)
            {
                if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null)
                {
                    throw new ArgumentException(entry.Name + " cannot be null.");
                }

                value = null;
            }
            else if (valueType == typeof(Point3d))
            {
                value = Point(entry.Value);
            }
            else if (valueType.IsEnum)
            {
                if (entry.Value.Type != JTokenType.String ||
                    !Enum.GetNames(valueType).Contains(entry.Value.Value<string>()))
                {
                    throw new ArgumentException(
                        $"{entry.Name}: use one of {string.Join(", ", Enum.GetNames(valueType))}.");
                }

                value = Enum.Parse(valueType, entry.Value.Value<string>()!);
            }
            else
            {
                value = entry.Value.ToObject(valueType);
                if ((value is double d && !double.IsFinite(d)) || (value is float f && !float.IsFinite(f)))
                {
                    throw new ArgumentException(entry.Name + " must be finite.");
                }
            }

            setters.Add(instance => property.SetValue(instance, value));
        }

        return setters;
    }

    public static void Apply(object instance, IReadOnlyList<Action<object>> setters)
    {
        ThrowIf.Argument.IsNull(setters, nameof(setters));
        ThrowIf.Argument.IsNull(instance, nameof(instance));
        foreach (var set in setters)
        {
            set(instance);
        }
    }

    public static JObject Read(object instance, IEnumerable<string>? names = null)
    {
        ThrowIf.Argument.IsNull(instance, nameof(instance));
        var selected = names?.ToHashSet(StringComparer.Ordinal);
        var result = new JObject();
        foreach (var property in Readable(instance.GetType()).Where(x => selected == null || selected.Contains(x.Name)))
        {
            var value = property.GetValue(instance);
            result[property.Name] = value switch
            {
                null => JValue.CreateNull(),
                Point3d point => new JArray(point.X, point.Y, point.Z),
                Enum item => new JValue(item.ToString()),
                _ => JToken.FromObject(value)
            };
        }

        return result;
    }

    public static Point3d Point(JToken token)
    {
        ThrowIf.Argument.IsNull(token, nameof(token));
        if (token is not JArray array || array.Count is < 2 or > 3 ||
            array.Any(x => x.Type is not (JTokenType.Float or JTokenType.Integer)))
        {
            throw new ArgumentException("Point must be [x,y] or [x,y,z] in millimetres.");
        }

        var values = array.Select(x => x.Value<double>()).ToArray();
        if (values.Any(x => !double.IsFinite(x)))
        {
            throw new ArgumentException("Point coordinates must be finite.");
        }

        return new Point3d(values[0], values[1], values.Length == 3 ? values[2] : 0);
    }
}
