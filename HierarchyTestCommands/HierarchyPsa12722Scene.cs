namespace MyPlugin.HierarchyTestCommands;

/// <summary>
/// Кейсы PSA-12722 на одном плане, рядами снизу вверх: подписи контуров и нод, предупреждения о маркерах.
/// Трим Id здесь не проверить: генератор задаёт Id через тот же сеттер, пробел отрезается сразу.
/// Id нод по возможности называют сам кейс; над рядом — заголовок с тем, что проверять.
/// </summary>
internal static class HierarchyPsa12722Scene
{
    private const string Plan = "План 1";
    private const double Width = 50000;
    private const double Depth = 25000;
    private const double Step = 70000;
    private const double RowStep = 80000;

    internal static HierarchyTestScene Create()
    {
        var scene = new HierarchyTestScene();

        var row = 0d;
        Zone(scene, "зона_два_контура_разные_имена", 1, "Первый контур", 0, row, Width).Description = new[]
        {
            "1. Подписи контуров в окне выбора",
            "Разные имена — как есть; одинаковые — с планом и габаритами; пустое — «Без имени»"
        };
        Zone(scene, "зона_два_контура_разные_имена", 2, "Второй контур", Step, row, Width);
        Zone(scene, "зона_два_контура_одинаковые_имена", 1, "Одинаковое имя", 2 * Step, row, Width);
        // Другой размер: подписи одноимённых контуров различаются габаритами.
        Zone(scene, "зона_два_контура_одинаковые_имена", 2, "Одинаковое имя", 3 * Step, row, Width - 10000);
        Zone(scene, "зона_без_имени", 1, "", 4 * Step, row, Width);

        row += RowStep;
        Zone(scene, "зона_с_единственным_контуром", 1, "Наименование контура", 0, row, Width).Description = new[]
        {
            "2. Нода с единственным контуром — в «Модели объекта»",
            "«Id - наименование»; без наименования — только Id или «Здание №N»"
        };
        Zone(scene, "зона_с_единственным_контуром_без_имени", 1, "", Step, row, Width);
        Building(scene, 3, 1, "", 2 * Step, row);
        Building(scene, 5, 1, "Наименование здания", 3 * Step, row);

        row += RowStep;
        Building(scene, 1, 1, "Корпус А", 0, row).Description = new[]
        {
            "3. Здание 1 из двух контуров",
            "В свойствах ноды здания — «Корпус А / Корпус Б»"
        };
        Building(scene, 1, 2, "Корпус Б", Step, row);

        row += RowStep;
        var withoutMarker = Zone(scene, "зона_без_маркера", 1, "Нет маркера", 0, row, Width);
        withoutMarker.Markers.Clear();
        withoutMarker.Description = new[]
        {
            "4. Маркеры: предупреждения до окна выбора, контуры исключены",
            "Зона — «Не найден маркер»; этаж — «Найдено основных маркеров: 3», маркеры подсвечиваются"
        };
        var floor = Contour(scene, "этаж_2_здания_1_три_основных_маркера", "Floor", Step, row, Width, "Три основных маркера");
        floor.BuildingNumber = 1;
        floor.FloorNumber = 2;
        floor.Markers.Add(new HierarchyTestMarker { X = Step + 5000, Y = row + 5000, Label = "лишний_маркер_1", UsePosition = true });
        floor.Markers.Add(new HierarchyTestMarker { X = Step + Width - 5000, Y = row + 5000, Label = "лишний_маркер_2", UsePosition = true });
        return scene;
    }

    private static HierarchyTestContour Zone(
        HierarchyTestScene scene,
        string id,
        int copy,
        string name,
        double x,
        double y,
        double width)
    {
        var zone = Contour(scene, $"{id.Trim()} ({copy})", "Zone", x, y, width, name);
        zone.LogicalId = id;
        return zone;
    }

    private static HierarchyTestContour Building(
        HierarchyTestScene scene,
        int number,
        int copy,
        string name,
        double x,
        double y)
    {
        var building = Contour(scene, $"здание_{number} ({copy})", "Building", x, y, Width, name);
        building.BuildingNumber = number;
        building.LogicalId = number.ToString();
        return building;
    }

    private static HierarchyTestContour Contour(
        HierarchyTestScene scene,
        string key,
        string kind,
        double x,
        double y,
        double width,
        string name)
    {
        var contour = HierarchyRotationScene.Rectangle(key, kind, Plan, x, y, width, Depth);
        contour.Name = name;
        scene.Contours.Add(contour);
        return contour;
    }
}
