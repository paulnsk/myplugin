using System;
using System.Linq;

namespace MyPlugin.HierarchyTestCommands;

/// <summary>Приближённые башни Николаева 11/12 по ракурсам 2ГИС, без фасадной деталировки.</summary>
internal static class HierarchyAcademparkScene
{
    private const string Plan = "План 3";
    private const double FloorHeight = 4000;

    internal static void AddTo(HierarchyTestScene scene)
    {
        var territory = HierarchyRotationScene.Rectangle("ACADEMPARK", "Territory", Plan, 0, 0, 240000, 240000);
        territory.Name = "3 — Академпарк, Николаева 11/12 (приближённо)";
        scene.Contours.Add(territory);
        // X — восток, Y — север. Проезд проходит с запада на восток вдоль Y=120000.
        Tower(scene, territory, 201, 85000, 155000, false);
        Tower(scene, territory, 202, 130000, 85000, true);
        var bridge = HierarchyRotationScene.Rectangle("BRIDGE", "Zone", Plan, 100000, 118500, 15000, 3000);
        bridge.Name = "Переход между башнями, уровень 13-го этажа";
        bridge.ParentKey = territory.Key;
        bridge.Markers[0].Height = 12 * FloorHeight;
        scene.Contours.Add(bridge);
        scene.Patterns.Add(new HierarchyTestPattern
        {
            ContourKey = bridge.Key, Word = "P", X = bridge.X + 6000, Y = bridge.Y + 400,
            Cell = 300, LightHeight = FloorHeight
        });
    }

    private static void Tower(
        HierarchyTestScene scene,
        HierarchyTestContour territory,
        int number,
        double centerX,
        double centerY,
        bool opposite)
    {
        var key = number == 201 ? "AK11" : "AK12";
        var building = HierarchyRotationScene.Rectangle(key, "Building", Plan, centerX - 65000, centerY - 40000, 130000, 100000);
        building.BuildingNumber = number;
        building.LogicalId = number.ToString();
        building.Name = number == 201 ? "Николаева 11 — ЦИТ" : "Николаева 12 — ЦКП";
        building.ParentKey = territory.Key;
        building.Markers[0].X = centerX;
        building.Markers[0].Y = centerY;
        building.Vertices = Footprint(0, true, opposite, true);
        scene.Contours.Add(building);
        for (var level = 1; level <= 14; level++)
        {
            var index = (number - 201) * 14 + level - 1;
            var row = index / 7;
            var x = 280000 + (index % 7) * 150000;
            var y = row * 130000;
            var floor = HierarchyRotationScene.AddFloor(scene, building, level, x, y, 0);
            floor.Width = 130000;
            floor.Height = 100000;
            floor.Name = $"{building.Name}, этаж {level}";
            floor.FloorHeight = (level - 1) * FloorHeight;
            floor.Markers[0].X = x + 65000;
            floor.Markers[0].Y = y + 40000;
            floor.Vertices = Footprint((level - 1) / 13d, level <= 3, opposite, false);
            floor.OutlineVertices = Inset(floor.Vertices, 500);
            var lights = scene.Patterns.Single(p => p.ContourKey == floor.Key);
            // Вместо букв — асимметричная тройка потолочных светильников с трассами.
            lights.Word = "";
            lights.X = x + 62000;
            lights.Y = y + 37000;
            lights.Cell = 6000;
            lights.LightHeight = FloorHeight;
            if (!opposite && level == 4)
            {
                lights.Word = "NANOSOFT";
                lights.X = x + 53000;
                lights.Y = y + 57000;
                lights.Cell = 500;
                lights.CompactLuminaire = true;
            }
        }
    }

    private static double[][] Footprint(
        double fraction,
        bool withWing,
        bool opposite,
        bool envelope)
    {
        // Оба края смещаются к дороге: внешний на 22 м, внутренний на 13 м.
        // Этажи становятся короче, а верхние три дополнительно нависают углом над проездом.
        var halfWidth = envelope ? 21000 : 14000 + 2000 * fraction;
        var front = envelope ? -20500 : -20000 - 13000 * fraction;
        var rear = envelope ? 27000 : 30000 - 22000 * fraction;
        var points = withWing ? new[]
        {
            new[] { -halfWidth, front }, new[] { halfWidth, front },
            new[] { halfWidth, 50000d }, new[] { -60000d, 50000d },
            new[] { -60000d, rear }, new[] { -halfWidth, rear }
        } : fraction >= 11d / 13 ? new[]
        {
            new[] { -halfWidth, front }, new[] { halfWidth - 10000, front },
            new[] { halfWidth - 10000, front - 5000 }, new[] { halfWidth, front - 5000 },
            new[] { halfWidth, rear - 2500 }, new[] { halfWidth - 2500, rear }, new[] { -halfWidth, rear }
        } : new[]
        {
            new[] { -halfWidth, front }, new[] { halfWidth, front },
            new[] { halfWidth, rear - 2500 }, new[] { halfWidth - 2500, rear }, new[] { -halfWidth, rear }
        };
        var direction = opposite ? -1 : 1;
        foreach (var point in points)
        {
            point[0] = 65000 + direction * point[0];
            point[1] = 40000 + direction * point[1];
        }
        return points.Append(points[0]).ToArray();
    }

    // Вход замкнут: последняя точка повторяет первую. Смещаем рёбра внутрь CCW-многоугольника.
    private static double[][] Inset(double[][] points, double distance)
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
