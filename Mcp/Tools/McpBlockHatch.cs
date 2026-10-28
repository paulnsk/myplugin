using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Teigha.DatabaseServices;
using Teigha.Geometry;

namespace MyPlugin.Mcp.Tools;

internal static class McpBlockHatch
{
    // Hatch требует добавления в базу до настройки границы и вычисления заливки.
    internal static void Fill(
        Hatch hatch,
        JObject args,
        Transaction transaction)
    {
        if (args["vertices"] is not JArray vertices || vertices.Count is < 3 or > 100)
        {
            throw new ArgumentException("Для hatch нужны 3–100 вершин простого многоугольника без повторения первой в конце.");
        }
        var points = vertices.Select(McpBlockGeometry.ReadPoint).ToArray();
        if (points.Any(x => !x.Z.Equals(points[0].Z))) { throw new ArgumentException("У вершин hatch должен совпадать Z."); }
        if (points.Distinct().Count() != points.Length) { throw new ArgumentException("Вершины hatch не должны повторяться; граница замыкается автоматически."); }
        var previousDatabase = HostApplicationServices.WorkingDatabase;
        try
        {
            // Настройки/вычисление штриховки должны относиться к редактируемой базе, не активному DWG.
            HostApplicationServices.WorkingDatabase = hatch.Database;
            using var boundary = new Polyline(points.Length);
            boundary.SetDatabaseDefaults(hatch.Database);
            boundary.Closed = true;
            boundary.Elevation = points[0].Z;
            for (var i = 0; i < points.Length; i++)
            {
                boundary.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            }
            var block = (BlockTableRecord)transaction.GetObject(hatch.OwnerId, OpenMode.ForWrite);
            block.AppendEntity(boundary);
            transaction.AddNewlyCreatedDBObject(boundary, true);
            hatch.Normal = Vector3d.ZAxis;
            hatch.Elevation = points[0].Z;
            hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hatch.Associative = false;
            hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection(new[] { boundary.ObjectId }));
            hatch.EvaluateHatch(true);
            // Граница уже скопирована в неассоциативный Hatch; вспомогательный объект не сохраняем.
            boundary.Erase();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previousDatabase;
        }
    }

    internal static void Describe(Hatch hatch, JObject result)
    {
        result["pattern"] = hatch.PatternName;
        result["solid"] = hatch.IsSolidFill;
        result["associative"] = hatch.Associative;
        result["normal"] = new JArray(hatch.Normal.X, hatch.Normal.Y, hatch.Normal.Z);
        result["elevation"] = hatch.Elevation;
        result["loopCount"] = hatch.NumberOfLoops;
        result["loops"] = new JArray(Enumerable.Range(0, Math.Min(5, hatch.NumberOfLoops)).Select(i =>
        {
            var loop = hatch.GetLoopAt(i);
            var description = new JObject { ["type"] = loop.LoopType.ToString() };
            if (loop.IsPolyline)
            {
                description["vertexCount"] = loop.Polyline.Count;
                description["vertices"] = new JArray(Enumerable.Range(0, Math.Min(20, loop.Polyline.Count)).Select(j =>
                    new JObject { ["point"] = new JArray(loop.Polyline[j].Vertex.X, loop.Polyline[j].Vertex.Y), ["bulge"] = loop.Polyline[j].Bulge }));
                if (loop.Polyline.Count > 20) { description["verticesTruncated"] = true; }
            }
            else
            {
                description["curveCount"] = loop.Curves.Count;
                description["geometryOmitted"] = true;
            }
            return description;
        }));
        if (hatch.NumberOfLoops > 5) { result["loopsTruncated"] = true; }
    }
}
