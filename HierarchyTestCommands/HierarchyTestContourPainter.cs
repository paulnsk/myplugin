using System;
using System.Collections.Generic;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.BuildingModel;
using Nanosoft.Acad.Ifaces.Contours;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.CAE.Contours;
using Nanosoft.Extensions;
using Nanosoft.Utils;
using Nanosoft.Utils.Point3d;

namespace MyPlugin.HierarchyTestCommands;

/// <summary>
/// Контуры сцены с маркерами и подписи на плане. Тип контура этажа зависит от продукта:
/// Электрика — ScsFloorContour, ВК — TbFloorContour.
/// </summary>
public sealed class HierarchyTestContourPainter
{
    private readonly IWoObjectFactory _factory;
    private readonly IUgoTypes _ugoTypes;
    private readonly IUgoBaseComponent _ugoBase;
    private readonly ICwCadDrawManager _draw;
    private readonly Type _floorContourType;

    public HierarchyTestContourPainter(
        IWoObjectFactory factory,
        IUgoTypes ugoTypes,
        IUgoBaseComponent ugoBase,
        ICwCadDrawManager draw,
        Type floorContourType)
    {
        ThrowIf.Argument.IsNull(factory, nameof(factory));
        ThrowIf.Argument.IsNull(ugoTypes, nameof(ugoTypes));
        ThrowIf.Argument.IsNull(ugoBase, nameof(ugoBase));
        ThrowIf.Argument.IsNull(draw, nameof(draw));
        ThrowIf.Argument.IsNull(floorContourType, nameof(floorContourType));
        _factory = factory;
        _ugoTypes = ugoTypes;
        _ugoBase = ugoBase;
        _draw = draw;
        _floorContourType = floorContourType;
    }

    //based on HierarchyTestContourBuilder из внешнего тестового стенда.
    internal void DrawContour(IAcadNet net, HierarchyTestContour description)
    {
        var x = description.X;
        var y = description.Y;
        var points = description.Vertices.Length > 0
            ? description.Vertices.Select(p => new Point3d(x + p[0], y + p[1], 0)).ToList()
            : new List<Point3d>
        {
            new(x, y, 0), new(x + description.Width, y, 0),
            new(x + description.Width, y + description.Height, 0), new(x, y + description.Height, 0), new(x, y, 0)
        };
        ILocalCoordinateSystemContour contour;
        switch (description.Kind)
        {
            case "Territory":
                var territory = _factory.CreateContour<BimTerritoryAreaContour>(net, points)
                    ?? throw new InvalidOperationException("Не создана территория.");
                territory.Id = description.LogicalId;
                territory.Name = description.Name;
                contour = territory;
                break;
            case "Zone":
                var zone = _factory.CreateContour<BimZoneAreaContour>(net, points)
                    ?? throw new InvalidOperationException("Не создана зона.");
                zone.Id = description.LogicalId;
                zone.Name = description.Name;
                contour = zone;
                break;
            case "Building":
                var building = _factory.CreateContour<BimBuildingAreaContour>(net, points)
                    ?? throw new InvalidOperationException("Не создано здание.");
                building.Id = description.BuildingNumber;
                building.Name = description.Name;
                contour = building;
                break;
            case "Floor":
                var create = typeof(IWoObjectFactory).GetMethods().Single(m => m.Name == "CreateContour" &&
                    m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(List<Point3d>));
                var floor = (IBuildingModelFloorContour)create.MakeGenericMethod(_floorContourType).Invoke(_factory, new object[] { net, points })!;
                floor.BuildingIndex = description.BuildingNumber;
                floor.BuildingDescription = description.BuildingDescription ?? $"Тестовое здание {description.BuildingNumber}";
                floor.RoomFloor = description.FloorNumber;
                floor.FloorDescription = description.Name;
                floor.FloorAbsolutHeight = description.FloorHeight;
                if (description.StoreyHeight > 0)
                {
                    floor.FloorHeight = description.StoreyHeight;
                }
                contour = floor;
                break;
            default:
                throw new InvalidOperationException(description.Kind);
        }
        // Та же опорная точка, что при ручном создании в BimAreaContourCreator.CalcCirclePos.
        ((AcadContour)contour).CirclePos = AcadContour.CalcCenterPoint(contour.CurvePnts.Skip(1).ToList());
        contour.Save();
        ((IAcadNode)contour).UpdateView();
        description.Id = contour.AcId.ToGuid();

        var marker = net.GetNodes<TbFloorMarker>().Single(m => m.LocalCoordinateSystemContourId == contour.AcId);
        if (description.Markers.Count == 0)
        {
            marker.Delete();
            return;
        }
        for (var i = 0; i < description.Markers.Count; i++)
        {
            var specification = description.Markers[i];
            var current = i == 0 ? marker : CreateMarker(net, specification);
            if (specification.UsePosition) { current.MoveToPosition(new Point3d(specification.X, specification.Y, 0)); }
            current.MarkerLabel = specification.Label;
            current.IsMainFloorMarker = specification.IsMain;
            current.ZeroLevel = specification.Height;
            current.LocalCoordinateSystemContour = specification.BindToContour && !description.GeometricMarker ? contour : null;
            current.SetUgoRotation(Point3d.AxeX.GetRotated(Point3d.AxeZ, specification.Rotation.ToRadians()));
            current.SaveMe();
            current.UpdateView();
            specification.Id = current.AcId.ToGuid();
        }
    }

    private TbFloorMarker CreateMarker(IAcadNet net, HierarchyTestMarker specification)
    {
        var typeId = _ugoTypes.GetTypeId(typeof(TbFloorMarker));
        var ugo = _ugoBase.UgoList.First(x => x.UgoElemTypeId == typeId);
        return (TbFloorMarker)_factory.CreateObject(net, ugo, typeof(TbFloorMarker),
            new Point3d(specification.X, specification.Y, 0), 0, Point3d.AxeZ);
    }

    internal void DrawCaption(HierarchyTestContour contour)
    {
        var font = Math.Max(350, Math.Min(contour.Width, contour.Height) / 40) * 1.5;
        var lines = new List<string> { $"{contour.Key}: {contour.Name}" };
        lines.Add(contour.Markers.Count == 0 ? "Маркер отсутствует" :
            $"Маркер {contour.Markers[0].Rotation:0.#}°; высота {contour.Markers[0].Height:0} мм");
        if (contour.Kind == "Floor")
        {
            lines.Add($"Отметка этажа {contour.FloorHeight:0} мм");
        }
        Text(contour.X, contour.Y + contour.Height + font * (lines.Count * 1.5 + 1), font, lines);
        if (contour.Description.Length > 0)
        {
            var headingFont = Math.Max(3000, font);
            Text(contour.X, contour.Y + contour.Height + font * (lines.Count * 1.5 + 2) + headingFont * (contour.Description.Length * 1.5 + 1),
                headingFont, contour.Description);
        }
        foreach (var marker in contour.Markers.Skip(1).Where(x => x.X < contour.X || x.Y < contour.Y))
        {
            Text(marker.X, marker.Y - font * 2, font, new[] { $"Основной маркер: {marker.Rotation:0.#}°", "Начало отсчёта здесь" });
        }
    }

    /// <summary>Строки сверху вниз, первая — на высоте y.</summary>
    internal void Text(
        double x,
        double y,
        double height,
        IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            _draw.AddText("0", new Point3d(x, y, 0), Point3d.AxeX, height, 0, line);
            y -= height * 1.5;
        }
    }
}
