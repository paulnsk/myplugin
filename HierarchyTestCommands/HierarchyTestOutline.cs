using System;

namespace MyPlugin.HierarchyTestCommands;

internal static class HierarchyTestOutline
{
    // Замкнутый многоугольник против часовой стрелки; отступ внутрь от каждого ребра.
    internal static double[][] Inset(double[][] points, double distance)
    {
        var count = points.Length - 1;
        var result = new double[count][];
        for (var i = 0; i < count; i++)
        {
            var previous = points[(i + count - 1) % count];
            var current = points[i];
            var next = points[(i + 1) % count];
            var ax = current[0] - previous[0];
            var ay = current[1] - previous[1];
            var bx = next[0] - current[0];
            var by = next[1] - current[1];
            var lengthA = Math.Sqrt(ax * ax + ay * ay);
            var lengthB = Math.Sqrt(bx * bx + by * by);
            var x = current[0] - ay * distance / lengthA;
            var y = current[1] + ax * distance / lengthA;
            var otherX = current[0] - by * distance / lengthB;
            var otherY = current[1] + bx * distance / lengthB;
            var along = ((otherX - x) * by - (otherY - y) * bx) / (ax * by - ay * bx);
            result[i] = new[] { x + along * ax, y + along * ay };
        }
        return result;
    }
}
