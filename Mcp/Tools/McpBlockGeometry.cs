using System;
using System.Linq;
using MyPlugin.Mcp.Protocol;
using Newtonsoft.Json.Linq;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace MyPlugin.Mcp.Tools;

internal static class McpBlockGeometry
{
    internal static JObject Schema() => new()
    {
        ["type"] = "array", ["maxItems"] = 100,
        ["items"] = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("kind"),
            ["properties"] = new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("line", "circle", "polyline", "text", "hatch") },
                ["start"] = McpSchema.Point(), ["end"] = McpSchema.Point(),
                ["center"] = McpSchema.Point(), ["radius"] = McpSchema.Number("Для circle, > 0."),
                ["vertices"] = new JObject { ["type"] = "array", ["items"] = McpSchema.Point(), ["minItems"] = 2, ["maxItems"] = 100,
                    ["description"] = "Для polyline: 2–100 точек. Для hatch: 3–100 вершин простого многоугольника без повторения первой в конце; замыкается автоматически, заливка SOLID, без отверстий. Все точки с одинаковым Z; без дуг." },
                ["closed"] = McpSchema.Boolean("Замкнуть polyline, по умолчанию false."),
                ["width"] = McpSchema.Number("Ширина polyline в единицах DWG, >= 0; по умолчанию 0 (тонкая линия)."),
                ["position"] = McpSchema.Point(), ["text"] = McpSchema.String("Строка для text."),
                ["height"] = McpSchema.Number("Для text, > 0, мм."),
                ["rotation"] = McpSchema.Number("Для text, градусы против часовой, по умолчанию 0."),
                ["layer"] = McpSchema.String("Существующий слой DWG, по умолчанию 0.")
            }
        }
    };

    internal static bool IsSupported(Entity entity) =>
        entity is Line or Circle or Arc or Polyline or Hatch || entity is DBText and not AttributeDefinition and not AttributeReference;

    internal static Entity Create(JObject args)
    {
        switch (args.Value<string>("kind"))
        {
            case "hatch":
                return new Hatch();
            case "line":
                var start = ReadPoint(args["start"]);
                var end = ReadPoint(args["end"]);
                if (start == end) { throw new ArgumentException("Линия должна иметь ненулевую длину."); }
                return new Line(start, end);
            case "circle":
                return new Circle(ReadPoint(args["center"]), Vector3d.ZAxis, Positive(args, "radius"));
            case "text":
                var position = ReadPoint(args["position"]);
                var text = args.Value<string>("text") ?? throw new ArgumentException("Нужен text.");
                var height = Positive(args, "height");
                var rotation = Number(args["rotation"] ?? new JValue(0)) * Math.PI / 180;
                return new DBText { Position = position, TextString = text, Height = height, Rotation = rotation };
            case "polyline":
                if (args["vertices"] is not JArray vertices || vertices.Count is < 2 or > 100)
                {
                    throw new ArgumentException("Для polyline нужны 2–100 vertices.");
                }
                var points = vertices.Select(ReadPoint).ToArray();
                if (points.Any(x => x.Z != points[0].Z)) { throw new ArgumentException("У вершин polyline должен совпадать Z."); }
                var width = Number(args["width"] ?? new JValue(0));
                if (width < 0) { throw new ArgumentException("width должен быть >= 0."); }
                var polyline = new Polyline(points.Length) { Elevation = points[0].Z, Closed = args.Value<bool?>("closed") ?? false };
                try
                {
                    for (var i = 0; i < points.Length; i++)
                    {
                        polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, width, width);
                    }
                    return polyline;
                }
                catch
                {
                    polyline.Dispose();
                    throw;
                }
            default:
                throw new ArgumentException("kind: line, circle, polyline, text или hatch.");
        }
    }

    internal static JObject Describe(Entity entity)
    {
        var result = new JObject
        {
            ["handle"] = entity.Handle.ToString(), ["kind"] = entity.GetType().Name, ["layer"] = entity.Layer
        };
        switch (entity)
        {
            case Hatch hatch:
                McpBlockHatch.Describe(hatch, result);
                break;
            case Line line:
                result["start"] = JArray.FromObject(Point(line.StartPoint));
                result["end"] = JArray.FromObject(Point(line.EndPoint));
                break;
            case Circle circle:
                result["center"] = JArray.FromObject(Point(circle.Center));
                result["radius"] = circle.Radius;
                result["normal"] = new JArray(circle.Normal.X, circle.Normal.Y, circle.Normal.Z);
                break;
            case Arc arc:
                result["center"] = JArray.FromObject(Point(arc.Center));
                result["radius"] = arc.Radius;
                result["normal"] = new JArray(arc.Normal.X, arc.Normal.Y, arc.Normal.Z);
                result["startAngle"] = arc.StartAngle * 180 / Math.PI;
                result["endAngle"] = arc.EndAngle * 180 / Math.PI;
                break;
            case Polyline polyline:
                result["vertexCount"] = polyline.NumberOfVertices;
                result["closed"] = polyline.Closed;
                result["vertices"] = new JArray(Enumerable.Range(0, Math.Min(20, polyline.NumberOfVertices))
                    .Select(i =>
                    {
                        var vertex = new JObject { ["point"] = JArray.FromObject(Point(polyline.GetPoint3dAt(i))), ["bulge"] = polyline.GetBulgeAt(i) };
                        var startWidth = polyline.GetStartWidthAt(i);
                        var endWidth = polyline.GetEndWidthAt(i);
                        if (startWidth != 0 || endWidth != 0)
                        {
                            vertex["startWidth"] = startWidth;
                            vertex["endWidth"] = endWidth;
                        }
                        return vertex;
                    }));
                if (polyline.NumberOfVertices > 20) { result["verticesTruncated"] = true; }
                break;
            case DBText text:
                result["position"] = JArray.FromObject(Point(text.Position));
                result["text"] = text.TextString;
                result["height"] = text.Height;
                result["rotation"] = text.Rotation * 180 / Math.PI;
                break;
        }
        return result;
    }

    internal static double[] Point(Point3d point) => new[] { point.X, point.Y, point.Z };

    internal static Point3d ReadPoint(JToken? token)
    {
        if (token is not JArray values || values.Count is < 2 or > 3)
        {
            throw new ArgumentException("Точка: [x,y] или [x,y,z].");
        }
        return new Point3d(Number(values[0]), Number(values[1]), values.Count == 3 ? Number(values[2]) : 0);
    }

    private static double Positive(JObject args, string name)
    {
        var number = Number(args[name]);
        return number > 0 ? number : throw new ArgumentException($"{name} должен быть > 0.");
    }

    private static double Number(JToken? token)
    {
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float) || !double.IsFinite(token.Value<double>()))
        {
            throw new ArgumentException("Координаты, размеры и углы должны быть конечными числами.");
        }
        return token.Value<double>();
    }
}
