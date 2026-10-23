using System;
using System.Collections.Generic;
using System.Linq;
using Guard;
using Nanosoft.Extensions;
using Nanosoft.Utils.Point3d;

namespace MyPlugin.HierarchyTestCommands;

// Ожидания считаем по описанию рисунка, без рабочего билдера, резолверов и калькулятора матриц.
internal sealed class HierarchyTestExpectedCoordinates
{
    private readonly HierarchyTestScene _scene;
    private readonly HierarchyTestExport _export;
    private readonly HashSet<Guid> _selected;
    private readonly Dictionary<string, HierarchyTestContour> _contoursByKey;

    internal HierarchyTestExpectedCoordinates(HierarchyTestScene scene, HierarchyTestExport export)
    {
        ThrowIf.Argument.IsNull(scene, nameof(scene));
        ThrowIf.Argument.IsNull(export, nameof(export));
        _scene = scene;
        _export = export;
        _selected = export.SelectedContours.ToHashSet();
        _contoursByKey = scene.Contours.ToDictionary(x => x.Key);
    }

    internal IEnumerable<HierarchyTestObject> Objects => _scene.Objects.Where(x =>
        GetOwnerContour(_contoursByKey[x.ContourKey]) is { } contour && _selected.Contains(contour.Id));

    private HierarchyTestContour? GetOwnerContour(HierarchyTestContour contour)
    {
        while (!HasSingleMarker(contour))
        {
            if (contour.ParentKey == null)
            {
                return null;
            }
            contour = _contoursByKey[contour.ParentKey];
        }
        return contour;
    }

    private static bool HasSingleMarker(HierarchyTestContour contour)
    {
        var mainCount = contour.Markers.Count(x => x.IsMain);
        return mainCount > 0 ? mainCount == 1 : contour.Markers.Count == 1;
    }

    internal Point3d[] GetPoints(HierarchyTestObject obj)
    {
        ThrowIf.Argument.IsNull(obj, nameof(obj));
        return obj.Points.Select(x =>
        {
            var point = PlaceInRoot(GetOwnerContour(_contoursByKey[obj.ContourKey])!, new Point3d(x[0], x[1], x[2] + obj.Height));
            return Rotate(point, _export.Rotation) + new Point3d(_export.X, _export.Y, _export.Z);
        }).ToArray();
    }

    private Point3d PlaceInRoot(HierarchyTestContour contour, Point3d point)
    {
        var marker = GetMarker(contour);
        // Сначала свой маркер: перенос начала отсчёта, обратный поворот и добавление высоты.
        var local = Rotate(point - new Point3d(marker.X, marker.Y, 0), -marker.Rotation);
        local += new Point3d(0, 0, marker.Height + (_export.UseFloorAbsoluteHeight && contour.Kind == "Floor" ? contour.FloorHeight : 0));
        if (IsRoot(contour))
        {
            return local;
        }

        if (contour.ParentKey != null && GetOwnerContour(_contoursByKey[contour.ParentKey]) is { } parent)
        {
            // Контур нарисован внутри родителя: возвращаем его XY на план родителя и продолжаем вверх.
            return PlaceInRoot(parent, local + new Point3d(marker.X, marker.Y, 0));
        }

        var copy = Ordered(_scene.Contours.Where(x => HasSingleMarker(x) && SameNode(x, contour) && x.ParentKey != null))
            .FirstOrDefault(x => HasPathToRoot(_contoursByKey[x.ParentKey!]));
        if (copy != null)
        {
            // От отдельно нарисованной копии берём только место её маркера, не её высоту/поворот.
            var copyMarker = GetMarker(copy);
            return PlaceInRoot(_contoursByKey[copy.ParentKey!], local + new Point3d(copyMarker.X, copyMarker.Y, 0));
        }

        if (contour.Kind == "Floor")
        {
            var building = Ordered(_scene.Contours.Where(x => x.Kind == "Building" && x.BuildingNumber == contour.BuildingNumber))
                .FirstOrDefault(HasPathToRoot);
            if (building != null)
            {
                // Отдельный этаж надевается на маркер здания, положение этажа на исходном плане не нужно.
                var buildingMarker = GetMarker(building);
                return PlaceInRoot(building, local + new Point3d(buildingMarker.X, buildingMarker.Y, 0));
            }
            if (_export.RootKind == "Building" && _export.RootId == contour.BuildingNumber.ToString())
            {
                // У виртуального здания нет собственного физического контура.
                return local;
            }
        }
        throw new InvalidOperationException($"По паспорту нет пути от {contour.Key} до {_export.RootText}.");
    }

    private bool HasPathToRoot(HierarchyTestContour contour)
    {
        if (!HasSingleMarker(contour))
        {
            return false;
        }
        if (IsRoot(contour))
        {
            return true;
        }
        if (contour.ParentKey != null)
        {
            return HasPathToRoot(_contoursByKey[contour.ParentKey]);
        }
        if (contour.Kind == "Floor")
        {
            return _export.RootKind == "Building" && _export.RootId == contour.BuildingNumber.ToString() ||
                _scene.Contours.Any(x => x.Kind == "Building" && x.BuildingNumber == contour.BuildingNumber && HasPathToRoot(x));
        }
        return _scene.Contours.Any(x => SameNode(x, contour) && x.ParentKey != null && HasPathToRoot(_contoursByKey[x.ParentKey]));
    }

    private bool IsRoot(HierarchyTestContour contour) => contour.Kind == _export.RootKind && contour.LogicalId == _export.RootId;

    private IEnumerable<HierarchyTestContour> Ordered(IEnumerable<HierarchyTestContour> contours) => contours
        .OrderByDescending(x => _selected.Contains(x.Id)).ThenBy(x => Array.IndexOf(_export.ContourOrder, x.Id));

    private static bool SameNode(HierarchyTestContour first, HierarchyTestContour second) => first.Kind == second.Kind && first.LogicalId == second.LogicalId;

    private HierarchyTestMarker GetMarker(HierarchyTestContour contour)
    {
        // В стенде внешний основной маркер хранится в том же описании, что и его неосновной маркер.
        return contour.Markers.FirstOrDefault(x => x.IsMain) ?? contour.Markers.First();
    }

    private static Point3d Rotate(Point3d point, double degrees)
    {
        var radians = degrees.ToRadians();
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new Point3d(point.X * cosine - point.Y * sine, point.X * sine + point.Y * cosine, point.Z);
    }
}
