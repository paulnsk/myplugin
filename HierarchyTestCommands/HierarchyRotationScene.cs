using System;

namespace MyPlugin.HierarchyTestCommands;

/// <summary>Два демонстрационных корня: ромашка зданий и закрученная башня.</summary>
internal static class HierarchyRotationScene
{
    private const double FloorWidth = 28000;
    private const double FloorDepth = 14000;

    internal static HierarchyTestScene Create()
    {
        var scene = new HierarchyTestScene();
        Flower(scene);
        Tower(scene);
        return scene;
    }

    private static void Flower(HierarchyTestScene scene)
    {
        var territory = Rectangle("FLOWER", "Territory", "План 1", 0, 0, 200000, 200000);
        territory.Name = "1 — Ромашка: 12 зданий, шаг 30°";
        territory.Markers[0].X = 100000;
        territory.Markers[0].Y = 100000;
        scene.Contours.Add(territory);
        for (var i = 0; i < 12; i++)
        {
            var angle = i * 30;
            var radians = angle * Math.PI / 180;
            var centerX = 100000 + 65000 * Math.Cos(radians);
            var centerY = 100000 + 65000 * Math.Sin(radians);
            var building = Rectangle($"B{i + 1}", "Building", "План 1", centerX - 18000, centerY - 10000, 36000, 20000, angle);
            building.BuildingNumber = i + 1;
            building.LogicalId = (i + 1).ToString();
            building.Name = $"B{i + 1}, {angle}°";
            building.ParentKey = territory.Key;
            // На плане повёрнут сам контур; исходные этажи справа остаются горизонтальными.
            building.Vertices = RotatedRectangle(36000, 20000, radians);
            scene.Contours.Add(building);
            scene.Patterns.Add(new HierarchyTestPattern
            {
                ContourKey = building.Key, Word = building.Key,
                X = centerX - 5500, Y = centerY + 3500, Cell = 600, LightHeight = 0
            });
            for (var floor = 1; floor <= 2; floor++)
            {
                var row = i / 3;
                AddFloor(scene, building, floor, 240000 + (i % 3) * 80000 + (floor - 1) * 40000, row * 35000, 0);
            }
        }
    }

    private static void Tower(HierarchyTestScene scene)
    {
        var building = Rectangle("TOWER", "Building", "План 2", 0, 0, 36000, 20000);
        building.BuildingNumber = 101;
        building.LogicalId = "101";
        building.Name = "2 — Башня: 10 этажей, 0–90°";
        scene.Contours.Add(building);
        for (var floor = 1; floor <= 10; floor++)
        {
            var row = (floor - 1) / 5;
            AddFloor(scene, building, floor, 70000 + ((floor - 1) % 5) * 40000, row * 30000, (floor - 1) * 10);
        }
    }

    private static void AddFloor(
        HierarchyTestScene scene,
        HierarchyTestContour building,
        int number,
        double x,
        double y,
        double angle)
    {
        var floor = Rectangle($"{building.Key}-F{number}", "Floor", building.Plan, x, y, FloorWidth, FloorDepth, angle);
        floor.BuildingNumber = building.BuildingNumber;
        floor.FloorNumber = number;
        floor.FloorHeight = (number - 1) * 3000;
        floor.Name = $"Этаж {number}, {angle}°";
        const double inset = 500;
        floor.OutlineVertices = new[]
        {
            new[] { inset, inset }, new[] { FloorWidth - inset, inset },
            new[] { FloorWidth - inset, FloorDepth - inset }, new[] { inset, FloorDepth - inset }
        };
        scene.Contours.Add(floor);
        // Этажи ромашки разнесены по полосам: F1 внизу, F2 выше, B — ещё выше и на земле.
        var textY = building.Key == "TOWER" ? 1500 + (number - 1) * 800 : 500 + (number - 1) * 4500;
        scene.Patterns.Add(new HierarchyTestPattern
        {
            ContourKey = floor.Key, Word = $"F{number}", X = x + 9000, Y = y + textY, Cell = 600, LightHeight = 3000
        });
    }

    private static HierarchyTestContour Rectangle(
        string key,
        string kind,
        string plan,
        double x,
        double y,
        double width,
        double depth,
        double angle = 0)
    {
        var contour = new HierarchyTestContour
        {
            Key = key, Kind = kind, LogicalId = key, Name = key, Plan = plan,
            X = x, Y = y, Width = width, Height = depth,
            DrawOutline = kind != "Building", OutlineDiameter = 150
        };
        contour.Markers.Add(new HierarchyTestMarker
        {
            X = x + width / 2, Y = y + depth / 2, Rotation = angle, Label = key, UsePosition = true
        });
        return contour;
    }

    private static double[][] RotatedRectangle(
        double width,
        double depth,
        double angle)
    {
        var vertices = new[] { new[] { 0d, 0d }, new[] { width, 0d }, new[] { width, depth }, new[] { 0d, depth }, new[] { 0d, 0d } };
        foreach (var point in vertices)
        {
            var x = point[0] - width / 2;
            var y = point[1] - depth / 2;
            point[0] = width / 2 + x * Math.Cos(angle) - y * Math.Sin(angle);
            point[1] = depth / 2 + x * Math.Sin(angle) + y * Math.Cos(angle);
        }
        return vertices;
    }
}
