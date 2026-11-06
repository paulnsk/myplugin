namespace MyPlugin.HierarchyTestCommands;

internal static class HierarchyBasicScene
{
    internal static HierarchyTestScene Create()
    {
        var scene = new HierarchyTestScene();
        var territory = HierarchyRotationScene.Rectangle("TERRITORY", "Territory", "План 1", 0, 0, 120000, 60000);
        territory.Name = "Территория";
        territory.DrawOutline = false;
        territory.ShowCaption = false;
        territory.Markers[0].X = 2000;
        territory.Markers[0].Y = 2000;
        scene.Contours.Add(territory);
        scene.Patterns.Add(new HierarchyTestPattern
        {
            ContourKey = territory.Key, Word = "TERRITORY", X = 8000, Y = 47000,
            Cell = 600, LightHeight = 0, CompactLuminaire = true
        });

        for (var copy = 0; copy < 3; copy++)
        {
            var x = copy == 0 ? 5000 : copy == 1 ? 140000 : 0;
            var y = copy == 0 ? 5000 : 0;
            var zone = HierarchyRotationScene.Rectangle($"ZONE-1-COPY-{copy}", "Zone", copy == 2 ? "План 2" : "План 1",
                x, y, 64000, 38000);
            zone.LogicalId = "zone1";
            zone.Name = copy switch
            {
                0 => "Зона — исходный контур",
                1 => "Зона — первая копия",
                _ => "Зона — вторая копия"
            };
            zone.ParentKey = copy == 0 ? territory.Key : null;
            zone.PlacementCopyKey = copy == 0 ? null : "ZONE-1-COPY-0";
            zone.DrawOutline = false;
            zone.ShowCaption = false;
            zone.Markers[0].X = x + 2000;
            zone.Markers[0].Y = y + 2000;
            zone.Markers[0].Height = copy == 2 ? 5000 : 0;
            scene.Contours.Add(zone);
            // Разные полосы остаются раздельными и после совмещения всех копий.
            scene.Patterns.Add(new HierarchyTestPattern
            {
                ContourKey = zone.Key, Word = $"ZONE 1 COPY {copy}", X = x + 3000, Y = y + 3000 + copy * 11000,
                Cell = 600, LightHeight = 0, CompactLuminaire = true
            });
        }
        return scene;
    }
}
