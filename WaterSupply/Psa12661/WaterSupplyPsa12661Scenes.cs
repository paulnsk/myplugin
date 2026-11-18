using System.Collections.Generic;
using System.Linq;
using MyPlugin.HierarchyTestCommands;
using Nanosoft.Acad;
using Nanosoft.Piping.Common.Pipes;
using Nanosoft.Utils.Point3d;
using Nanosoft.Watersupply.Nets;
using Nanosoft.WS.Consumers;

namespace MyPlugin.WaterSupply.Psa12661;

/// <summary>
/// Кейсы PSA-12661 на «План 1», размеры в мм. Этажи — TbFloorContour 18×12 м, высота этажа 3 м.
/// Этаж везде устроен одинаково: стояк у западной стены, магистраль на восток к смесителю,
/// врезками — кран смывного бачка унитаза и душевая сетка. Между этажами сеть идёт по одноимённым стоякам,
/// между территорией и зданием — по паре одноимённых разрывов (TubeTraceBreak).
/// </summary>
internal sealed class WaterSupplyPsa12661Scenes
{
    private const string Plan = "План 1";
    private const double FloorWidth = 18000;
    private const double FloorDepth = 12000;
    private const double FloorStep = 22000;
    private const double StoreyHeight = 3000;
    private const double NoteHeight = 700;
    // Ось стояка и магистрали этажа от левого нижнего угла этажа.
    private const double StandX = 3000;
    private const double MainY = 6000;
    // Разрыв ввода в здание — у западной стены этажа 1 на оси магистрали.
    private const double EntryX = 500;

    private readonly IAcadNet _net;
    private readonly HierarchyTestContourPainter _contours;
    private readonly WaterSupplyDemoObjects _objects;

    internal WaterSupplyPsa12661Scenes(IAcadNet net, HierarchyTestContourPainter contours, WaterSupplyDemoObjects objects)
    {
        _net = net;
        _contours = contours;
        _objects = objects;
    }

    /// <summary>Кейс 1: здание по старой модели — только этажи с маркерами, без территории и контура здания.</summary>
    internal void Old()
    {
        var floors = Enumerable.Range(1, 3).Select(n =>
        {
            var floor = Floor($"Э{n}", 1, n, $"Этаж {n}", (n - 1) * FloorStep, 0, "Здание 1 (старая модель)");
            // Как «Виртуальное здание» стенда: маркер привязан к этажу геометрически, а не ссылкой.
            floor.GeometricMarker = true;
            return Draw(floor);
        }).ToArray();
        var stands = floors.Select(x => FloorNetwork(x, "Ст. В1.1")).ToArray();
        var input = _objects.Input(_net, P(floors[0], 1000, 2000), "Ввод В1");
        _objects.Pipe(input, stands[0], P(floors[0], StandX, 2000));
        Notes(0, FloorDepth, new[]
        {
            "PSA-12661 · OLD: здание по старой модели, 3 этажа, ввод на 1-м этаже, стояк — 9 приборов"
        });
    }

    /// <summary>
    /// Кейс 4: воспроизведение автора задачи — ввод прямо на территории, в проекте нет ни одного контура этажа.
    /// </summary>
    internal void NoFloors()
    {
        var territory = Draw(Area("Т", "Territory", "территория", "Территория", 0, 0, 60000, 36000));
        var input = _objects.Input(_net, new Point3d(4000, 18000, 0), "Ввод В1 (территория)");
        _objects.Pipe(input, _objects.Break(_net, new Point3d(10000, 18000, 0), "В1 к группе приборов", 5000));

        var entry = _objects.Break(_net, new Point3d(31000, 18000, 0), "В1 к группе приборов", 5000);
        var mixer = _objects.Consumer<Mixer>(_net, new Point3d(47000, 18000, 0), "Смеситель умывальника", 1000);
        var tap = _objects.Consumer<WateringTap>(_net, new Point3d(36000, 21000, 0), "Поливочный кран", 600);
        var shower = _objects.Consumer<Shower>(_net, new Point3d(41000, 15000, 0), "Душевая сетка", 2100);
        var flush = _objects.Consumer<Hydrant>(_net, new Point3d(44000, 21000, 0), "Кран унитаза", 800);
        _objects.Main(entry, mixer, tap, shower, flush);
        Notes(territory.X, territory.Y + territory.Height + 6000, new[]
        {
            "PSA-12661 · NO_FLOORS: как у автора задачи — этажей нет, ввод и приборы на территории, связь разрывами — 4 прибора"
        });
    }

    /// <summary>
    /// Кейс 3: три независимые сети В1 в одном проекте.
    /// Сеть 1: ввод в зоне «Котельная» на территории (без контура этажа) питает приборы в другой зоне и через разрыв здание 1.
    /// Сеть 2: у здания 2 свой ввод на 1-м этаже — при снятом расчёте одного ввода новая «Показать сеть» затеняет объекты другой сети.
    /// Сеть 3: ввод и кран в зоне без маркера вне территории (иерархия её отбрасывает с предупреждением) → разрыв
    /// в «Зону душевых» на территории → разрыв в здание 3 → стояк на 2-й этаж. В 3D по иерархии объекты зоны без маркера пропадают.
    /// Все сети — В1: горячая вода потребовала бы второй конфигурации, параметров ГВС и проверки
    /// IsValidWaterUser для каждого прибора, а для проверки затенения важна только независимость сетей.
    /// </summary>
    internal void Boiler()
    {
        var territory = Draw(Area("Т", "Territory", "территория", "Территория", 0, 0, 70000, 56000));
        Draw(Area("К", "Zone", "котельная", "Котельная (служебный контур)", 4000, 20000, 14000, 12000));
        Draw(Area("ЗП", "Zone", "зона_полива", "Зона полива", 4000, 4000, 14000, 10000));
        Draw(Building(1, "Здание 1", 40000, 6000));
        Draw(Building(2, "Здание 2", 40000, 38000));
        Draw(Area("ЗД", "Zone", "зона_душевых", "Зона душевых", 4000, 38000, 14000, 12000));
        Draw(Building(3, "Здание 3", 21000, 38000));
        // Вне территории, без маркера: билдер иерархии отбросит зону с предупреждением, а расчёт ввод в ней пропустит.
        var noMarker = Area("ЗБМ", "Zone", "зона_без_маркера", "Зона без маркера", -22000, 38000, 18000, 12000);
        noMarker.Markers.Clear();
        Draw(noMarker);

        // Сеть 1. Магистраль котельной по оси Y = 24 м; длина разрыва делится поровну между парой.
        var boilerInput = _objects.Input(_net, new Point3d(6500, 24000, 0), "Ввод В1 котельной (сеть 1)");
        var toFirst = _objects.Break(_net, new Point3d(17000, 24000, 0), "В1 котельная → здание 1", 35500 / 2.0);
        var tap = _objects.Consumer<WateringTap>(_net, new Point3d(9000, 9000, 0), "Поливочный кран (зона полива)", 600);
        var hydrant = _objects.Consumer<Hydrant>(_net, new Point3d(15000, 9000, 0), "Водоразборный кран (зона полива)", 800);
        var first = BuildingFloors(1, 2, 90000, 6000, "Ст. В1.1");
        // Сеть 2: ввод здания 2 на его 1-м этаже, как в старом кейсе.
        var second = BuildingFloors(2, 2, 90000, 38000, "Ст. В1.2");
        var buildingInput = _objects.Input(_net, P(second.floor1, 1000, 2000), "Ввод В1 здания 2 (сеть 2)");
        _objects.Main(boilerInput, toFirst, tap, hydrant);
        _objects.Pipe(_objects.Break(_net, P(first.floor1, EntryX, MainY), "В1 котельная → здание 1", 35500 / 2.0), first.stand1);
        _objects.Pipe(buildingInput, second.stand1, P(second.floor1, StandX, 2000));

        // Сеть 3. Зона без маркера: магистраль по оси Y = 44 м; дальше «Зона душевых» и здание 3.
        var noMarkerInput = _objects.Input(_net, new Point3d(-18000, 44000, 0), "Ввод В1 (зона без маркера, сеть 3)");
        var noMarkerTap = _objects.Consumer<WateringTap>(_net, new Point3d(-12000, 47000, 0), "Поливочный кран (зона без маркера)", 600);
        var toShowers = _objects.Break(_net, new Point3d(-6000, 44000, 0), "В1 зона без маркера → зона душевых", 3000);
        var fromNoMarker = _objects.Break(_net, new Point3d(6000, 44000, 0), "В1 зона без маркера → зона душевых", 3000);
        var shower = _objects.Consumer<Shower>(_net, new Point3d(10000, 41000, 0), "Душевая сетка (зона душевых)", 2100);
        var showerMixer = _objects.Consumer<Mixer>(_net, new Point3d(14000, 47000, 0), "Смеситель душевой (зона душевых)", 1000);
        var toThird = _objects.Break(_net, new Point3d(17000, 44000, 0), "В1 зона душевых → здание 3", 15000);
        var third = BuildingFloors(3, 2, 90000, 22000, "Ст. В1.3");
        _objects.Main(noMarkerInput, toShowers, noMarkerTap);
        _objects.Main(fromNoMarker, toThird, shower, showerMixer);
        _objects.Pipe(_objects.Break(_net, P(third.floor1, EntryX, MainY), "В1 зона душевых → здание 3", 15000), third.stand1);
        Notes(territory.X, territory.Y + territory.Height + 7000, new[]
        {
            "PSA-12661 · BOILER: три сети. 1 — ввод в зоне «Котельная» → здание 1 (8 приборов); 2 — ввод в здании 2 (6);",
            "3 — ввод в зоне без маркера (иерархия её отбросит) → «Зона душевых» → здание 3 (9). Этажи зданий справа от территории"
        });
    }

    // Этажи здания рядом друг с другом; возвращает этаж 1 и его стояк — к нему подводится ввод.
    private (HierarchyTestContour floor1, TbStandPipe stand1) BuildingFloors(int building, int count, double x, double y, string standName)
    {
        var floors = Enumerable.Range(1, count)
            .Select(n => Draw(Floor($"З{building}-Э{n}", building, n, $"Здание {building}, этаж {n}", x + (n - 1) * FloorStep, y, $"Здание {building}")))
            .ToArray();
        var stands = floors.Select(f => FloorNetwork(f, standName)).ToArray();
        return (floors[0], stands[0]);
    }

    // Стояк, магистраль к смесителю и две врезки. Приборы выше трубы: подводки строит «Обновление модели».
    private TbStandPipe FloorNetwork(HierarchyTestContour floor, string standName)
    {
        var suffix = $"{floor.BuildingNumber}.{floor.FloorNumber}";
        var stand = _objects.Stand(_net, P(floor, StandX, MainY), standName, StoreyHeight);
        var mixer = _objects.Consumer<Mixer>(_net, P(floor, 15000, MainY), $"Смеситель умывальника {suffix}", 1000);
        var flush = _objects.Consumer<Hydrant>(_net, P(floor, 7000, MainY + 2000), $"Кран унитаза {suffix}", 800);
        var shower = _objects.Consumer<Shower>(_net, P(floor, 11000, MainY - 2000), $"Душевая сетка {suffix}", 2100);
        // Как у пользователя: сначала все приборы, потом трубы — новый прибор не ляжет на уже проложенную трубу.
        _objects.Main(stand, mixer, flush, shower);
        return stand;
    }

    private HierarchyTestContour Floor(string key, int building, int number, string name, double x, double y, string buildingName,
        double width = FloorWidth, double depth = FloorDepth)
    {
        var floor = Area(key, "Floor", key, name, x, y, width, depth);
        floor.BuildingNumber = building;
        floor.FloorNumber = number;
        floor.FloorHeight = (number - 1) * StoreyHeight;
        floor.StoreyHeight = StoreyHeight;
        floor.BuildingDescription = buildingName;
        return floor;
    }

    // Контур здания того же размера, что его этажи: маркеры в центрах совмещают этажи с контуром здания.
    private static HierarchyTestContour Building(int number, string name, double x, double y)
    {
        var building = Area($"З{number}", "Building", number.ToString(), name, x, y, FloorWidth, FloorDepth);
        building.BuildingNumber = number;
        return building;
    }

    private static HierarchyTestContour Area(string key, string kind, string id, string name, double x, double y, double width, double depth)
    {
        var contour = HierarchyRotationScene.Rectangle(key, kind, Plan, x, y, width, depth);
        contour.LogicalId = id;
        contour.Name = name;
        contour.DrawOutline = false;
        return contour;
    }

    private HierarchyTestContour Draw(HierarchyTestContour contour)
    {
        _contours.DrawContour(_net, contour);
        if (contour.ShowCaption)
        {
            _contours.DrawCaption(contour);
        }
        return contour;
    }

    // Пояснение над кейсом: строки сверху вниз, нижняя — на bottom + 4 м.
    private void Notes(double x, double bottom, IReadOnlyList<string> lines) =>
        _contours.Text(x, bottom + 4000 + NoteHeight * 1.5 * lines.Count, NoteHeight, lines);

    private static Point3d P(HierarchyTestContour contour, double x, double y) => new(contour.X + x, contour.Y + y, 0);
}
