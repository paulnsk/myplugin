namespace MyPlugin.HierarchyTestCommands;

/// <summary>
/// Кейсы PSA-12707: смена Id контура в окне «Модель объекта» пересобирает дерево.
/// Что поменять и что должно произойти, написано прямо в Id — его видно в дереве.
/// </summary>
internal static class HierarchyPsa12707Scene
{
    private const string Plan = "План 1";
    private const double Width = 60000;
    private const double Depth = 30000;
    private const double Step = 80000;
    private const double RowStep = 60000;

    internal static HierarchyTestScene Create()
    {
        var scene = new HierarchyTestScene();

        var row = 0d;
        Zone(scene, "папка_1", "папка из двух контуров", "Первый контур", 0, row);
        Zone(scene, "папка_2", "папка из двух контуров", "Смени мне Id на «одна» → папка «Физические контуры» исчезнет", Step, row);

        row += RowStep;
        Zone(scene, "пустой", "Сотри Id → предупреждение о пустом идентификаторе", "", 0, row);

        row += RowStep;
        Territory(scene, "территория_1", 0, row);
        Zone(scene, "зона_1", "зона_1", "", 15000, row + 7500, Width / 2, Depth / 2);
        Territory(scene, "территория_2", Step, row);
        Zone(scene, "два_родителя", "Смени Id на «зона_1» → предупреждение о двух родителях, зона пропадёт", "", Step + 15000, row + 7500, Width / 2, Depth / 2);

        row += RowStep;
        Zone(scene, "цикл_А", "цикл_А", "", 0, row);
        Zone(scene, "цикл_Б_1", "цикл_Б", "", 15000, row + 7500, Width / 2, Depth / 2);
        Zone(scene, "цикл_Б_2", "цикл_Б", "", Step, row);
        Zone(scene, "цикл", "Смени Id на «цикл_А» → ошибка: цикл, окно закроется", "", Step + 15000, row + 7500, Width / 2, Depth / 2);
        return scene;
    }

    private static void Territory(
        HierarchyTestScene scene,
        string id,
        double x,
        double y)
    {
        var territory = HierarchyRotationScene.Rectangle(id, "Territory", Plan, x, y, Width, Depth);
        territory.LogicalId = id;
        territory.Name = "";
        scene.Contours.Add(territory);
    }

    private static void Zone(
        HierarchyTestScene scene,
        string key,
        string id,
        string name,
        double x,
        double y,
        double width = Width,
        double depth = Depth)
    {
        var zone = HierarchyRotationScene.Rectangle(key, "Zone", Plan, x, y, width, depth);
        zone.LogicalId = id;
        zone.Name = name;
        scene.Contours.Add(zone);
    }
}
