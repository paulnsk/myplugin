using System;
using System.Collections.Generic;

namespace MyPlugin.HierarchyTestCommands;

// Описание рисунка и паспорт созданного проекта для будущей проверялки.
internal sealed class HierarchyTestScene
{
    public List<HierarchyTestContour> Contours { get; } = new();
    public List<HierarchyTestPattern> Patterns { get; } = new();
    public List<HierarchyTestObject> Objects { get; } = new();

    internal static HierarchyTestScene Create()
    {
        var scene = new HierarchyTestScene();
        scene.Park();
        scene.PhysicalBuilding();
        scene.Diagnostics();
        scene.ConcaveContours();
        scene.VirtualBuilding();
        scene.MarkerOutside();
        scene.FloorInsideBuilding();
        return scene;
    }

    internal static HierarchyTestScene CreateError()
    {
        var scene = new HierarchyTestScene();
        var first = scene.Zone("E1", "ERROR-1", "совпадающие зоны", 0, 0, 30000);
        scene.Zone("E2", "ERROR-2", "совпадающие зоны", 0, 0, 30000).ShowCaption = false;
        first.Description = new[]
        {
            "Два совпадающих контура: ERROR-1 и ERROR-2",
            "GENERATE3D должен показать ошибку равных площадей"
        };
        return scene;
    }

    private void Park()
    {
        var park = Territory("P1", "1-ПАРК", "парк — прямой", 0, 0, 100000);
        park.DrawOutline = true;
        park.Description = new[] { "1. Парк: вложенность и копии", "Зона −10°; подзона на Плане 2 — 15°" };
        Word(park, "P1", 5000, 85000, 1000);

        var zone = Zone("Z1", "PARK-Z", "зона парка", 20000, 20000, 60000, park.Key, -10);
        zone.DrawOutline = true;
        Word(zone, "Z1", 3000, 45000, 900);
        var subzone = Zone("S1", "PARK-S", "подзона — прямая", 25000, 25000, 30000, zone.Key);
        subzone.DrawOutline = true;
        Word(subzone, "S1", 3000, 3000, 800);

        var parkCopy = Territory("P2R", "1-ПАРК", "парк — повернутый", 130000, 0, 100000, 15);
        parkCopy.DrawOutline = true;
        Word(parkCopy, "P2R", 70000, 80000, 1000);
        var subzoneCopy = Zone("S2R", "PARK-S", "подзона — повернутая", 0, 0, 30000, rotation: 15);
        subzoneCopy.Plan = "План 2";
        subzoneCopy.DrawOutline = true;
        subzoneCopy.PlacementCopyKey = subzone.Key;
        subzoneCopy.Description = new[] { "1. Копия подзоны парка", "Свой маркер 15° + поворот зоны −10°" };
        Word(subzoneCopy, "S2R", 15000, 18000, 700);
    }

    private void PhysicalBuilding()
    {
        var zone = Zone("ZB", "2-ЗОНА-СО-ЗДАНИЕМ", "зона здания", 0, 150000, 100000, markerHeight: 200);
        zone.Description = new[] { "2. Физическое здание; этажи отдельно", "Второй этаж: две копии на разных высотах" };
        Word(zone, "ZB", 5000, 85000, 900);
        var building = Building("B21", 21, "здание 21", 20000, 170000, 60000, zone.Key, 1000);
        building.Height = 50000;
        Word(building, "B21", 5000, 35000, 900);

        var first = Floor("F1", 21, 1, "первый — добавка маркера", 120000, 150000, 30000, 0, 500);
        Word(first, "F1", 2000, 2000, 800);
        var second = Floor("F2A", 21, 2, "второй — собственная высота", 155000, 150000, 30000, 3000);
        Word(second, "F2A", 16000, 2000, 700);
        var secondCopy = Floor("F2B", 21, 2, "второй — обе высоты", 190000, 150000, 30000, 4500, 500);
        Word(secondCopy, "F2B", 2000, 18000, 700);
    }

    private void VirtualBuilding()
    {
        var first = Floor("V1", 31, 1, "первый", 0, 590000, 30000, 0);
        first.Description = new[] { "5. Виртуальное здание 31", "Обычные этажи: отметки 0 и 3500 мм" };
        first.GeometricMarker = true;
        Word(first, "V1", 2000, 2000, 800);
        var second = Floor("V2", 31, 2, "второй", 40000, 590000, 30000, 3500);
        second.GeometricMarker = true;
        Word(second, "V2", 16000, 18000, 800);
    }

    private void FloorInsideBuilding()
    {
        var building = Building("IB", 51, "здание с вложенным этажом", 0, 750000, 90000);
        building.Height = 60000;
        building.DrawOutline = true;
        building.OutlineDiameter = 200;
        building.Description = new[] { "7. Этаж прямо внутри здания 51", "Рамка B: 0; B: 3000; рамка F: 6000; F: 9000 мм" };
        Word(building, "B", 60000, 15000, 2500);
        var floor = Floor("IF", 51, 1, "геометрически вложенный", 15000, 765000, 40000, 3000, 3000);
        floor.Height = 30000;
        floor.DrawOutline = true;
        floor.OutlineDiameter = 200;
        floor.ParentKey = building.Key;
        Word(floor, "F", 6000, 6000, 2200);
    }

    private void MarkerOutside()
    {
        var floor = Floor("M", 41, 1, "основной маркер снаружи", 0, 670000, 30000, 0);
        floor.DrawOutline = true;
        floor.OutlineDiameter = 200;
        floor.Description = new[] { "6. Основной маркер снаружи: 15°", "Неосновной в углу: 0°" };
        floor.GeometricMarker = true;
        floor.Markers[0].IsMain = false;
        floor.Markers.Add(new HierarchyTestMarker
        {
            X = -10000, Y = 660000, Rotation = 15, Label = "OUTSIDE", BindToContour = false
        });
        floor.Markers[0].Label = "OUTSIDE";
        Points(floor, 4000, 4000, 8000);
    }

    private void Diagnostics()
    {
        var root = Territory("D1", "3-КОПИИ-С-ОШИБКАМИ", "территория — первая копия", 0, 300000, 140000);
        root.Height = 80000;
        root.Description = new[]
        {
            "3. Копии с ошибками",
            "NM и MM исключаются из дерева с предупреждениями",
            "Их объекты остаются в территории"
        };
        Word(root, "TE1", 10000, 60000, 1000);
        var bad = Zone("ERR", "NO_MARKER", "без маркера", 5000, 305000, 25000, root.Key);
        bad.Markers.Clear();
        Word(bad, "NM", 2000, 2000, 900);
        var many = Zone("WARN", "MANY_MARKERS", "два основных маркера", 40000, 305000, 25000, root.Key);
        many.Markers.Add(new HierarchyTestMarker { X = 43000, Y = 308000, Label = many.LogicalId });
        Word(many, "MM", 2000, 2000, 900);
        var copy1 = Zone("C1", "COPIES", "зона в первой территории", 80000, 305000, 25000, root.Key);
        Word(copy1, "C1", 2000, 2000, 900);
        var rootCopy = Territory("D2", root.LogicalId, "территория — вторая копия", 170000, 300000, 140000);
        rootCopy.Height = root.Height;
        Word(rootCopy, "TE2", 75000, 60000, 1000);
        var copy2 = Zone("C2", "COPIES", "зона во второй территории", 280000, 350000, 25000, rootCopy.Key);
        Word(copy2, "C2", 13000, 2000, 900);
        var separate = Zone("C3", "COPIES", "детализация рядом", 335000, 305000, 25000);
        separate.PlacementCopyKey = copy1.Key;
        Word(separate, "C3", 2000, 15000, 900);
    }

    private void ConcaveContours()
    {
        var territory = Territory("L1", "4-НЕПРЯМОУГОЛЬНЫЕ", "Г-образная территория", 0, 430000, 100000);
        territory.Vertices = new[]
        {
            new[] { 0d, 0d }, new[] { 100000d, 0d }, new[] { 100000d, 30000d },
            new[] { 30000d, 30000d }, new[] { 30000d, 100000d }, new[] { 0d, 100000d }, new[] { 0d, 0d }
        };
        territory.Description = new[]
        {
            "4. Г-образные контуры: зона не вложилась",
            "Переместите выноску зоны внутрь территории",
            "и повторите выгрузку"
        };
        Points(territory, 2000, 60000, 1000);
        var zone = Zone("L2", "4-ЗОНА", "Г-образная зона", 5000, 435000, 80000, territory.Key);
        zone.Vertices = new[]
        {
            new[] { 0d, 0d }, new[] { 80000d, 0d }, new[] { 80000d, 20000d },
            new[] { 20000d, 20000d }, new[] { 20000d, 80000d }, new[] { 0d, 80000d }, new[] { 0d, 0d }
        };
        Points(zone, 4000, 4000, 8000);
    }

    private HierarchyTestContour Territory(
        string key,
        string id,
        string name,
        double x,
        double y,
        double size,
        double rotation = 0)
    {
        return AddContour(key, "Territory", id, name, x, y, size, rotation, 0);
    }

    private HierarchyTestContour Zone(
        string key,
        string id,
        string name,
        double x,
        double y,
        double size,
        string? parent = null,
        double rotation = 0,
        double markerHeight = 0)
    {
        var contour = AddContour(key, "Zone", id, name, x, y, size, rotation, markerHeight);
        contour.ParentKey = parent;
        return contour;
    }

    private HierarchyTestContour Building(
        string key,
        int number,
        string name,
        double x,
        double y,
        double size,
        string? parent = null,
        double markerHeight = 0)
    {
        var contour = AddContour(key, "Building", number.ToString(), name, x, y, size, 0, markerHeight);
        contour.BuildingNumber = number;
        contour.ParentKey = parent;
        return contour;
    }

    private HierarchyTestContour Floor(
        string key,
        int building,
        int floor,
        string name,
        double x,
        double y,
        double size,
        double ownHeight,
        double markerHeight = 0)
    {
        var contour = AddContour(key, "Floor", $"{building}/{floor}", name, x, y, size, 0, markerHeight);
        contour.BuildingNumber = building;
        contour.FloorNumber = floor;
        contour.FloorHeight = ownHeight;
        return contour;
    }

    private HierarchyTestContour AddContour(
        string key,
        string kind,
        string id,
        string name,
        double x,
        double y,
        double size,
        double rotation,
        double markerHeight)
    {
        var contour = new HierarchyTestContour
        {
            Key = key, Kind = kind, LogicalId = id, Name = name, X = x, Y = y, Width = size, Height = size
        };
        contour.Markers.Add(new HierarchyTestMarker { X = x, Y = y, Rotation = rotation, Height = markerHeight, Label = id });
        Contours.Add(contour);
        return contour;
    }

    private void Word(
        HierarchyTestContour contour,
        string word,
        double x,
        double y,
        double cell)
    {
        Patterns.Add(new HierarchyTestPattern
        {
            ContourKey = contour.Key, Word = word, X = contour.X + x, Y = contour.Y + y, Cell = cell
        });
    }

    private void Points(
        HierarchyTestContour contour,
        double x,
        double y,
        double step)
    {
        Patterns.Add(new HierarchyTestPattern
        {
            ContourKey = contour.Key, X = contour.X + x, Y = contour.Y + y, Cell = step
        });
    }
}

internal sealed class HierarchyTestContour
{
    public string Key = "";
    public string Kind = "";
    public string LogicalId = "";
    public string Name = "";
    public string Plan = "План 1";
    // Физическая вложенность. Отдельно нарисованные этажи связываются со зданием по BuildingNumber.
    public string? ParentKey;
    public string? PlacementCopyKey;
    public Guid Id;
    public double X;
    public double Y;
    public double Width;
    public double Height;
    public int BuildingNumber;
    public int FloorNumber;
    public double FloorHeight;
    public bool GeometricMarker;
    public bool DrawOutline;
    public bool ShowCaption = true;
    public double OutlineDiameter = 1000;
    public double[][] OutlineVertices = Array.Empty<double[]>();
    // Локальные вершины; пустой массив означает обычный прямоугольник.
    public double[][] Vertices = Array.Empty<double[]>();
    public string[] Description = Array.Empty<string>();
    public readonly List<HierarchyTestMarker> Markers = new();
}

internal sealed class HierarchyTestMarker
{
    public Guid Id;
    public double X;
    public double Y;
    public double Rotation;
    public double Height;
    public string Label = "";
    public bool IsMain = true;
    public bool BindToContour = true;
    public bool UsePosition;
}

internal sealed class HierarchyTestPattern
{
    public string ContourKey = "";
    public string Word = "";
    public double X;
    public double Y;
    public double Cell;
    public double LightHeight = 3000;
    public bool CompactLuminaire;
}

internal sealed class HierarchyTestObject
{
    public Guid Id;
    public string ContourKey = "";
    public string Kind = "";
    public string Name = "";
    // Собственная высота хранится отдельно от плоских координат исходного рисунка.
    public double Height;
    public double[][] Points = Array.Empty<double[]>();
}
