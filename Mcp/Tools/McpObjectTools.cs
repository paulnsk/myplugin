using System;
using System.Collections.Generic;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.BuildingModel;
using Nanosoft.Acad.Ifaces.Contours;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.SlopeTraceKit;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using MyPlugin.Mcp.Protocol;
using Nanosoft.Data;
using Nanosoft.Extensions;
using Nanosoft.Utils;
using Nanosoft.Utils.Point3d;
using Newtonsoft.Json.Linq;

namespace MyPlugin.Mcp.Tools;

internal sealed class McpObjectTools
{
    private readonly McpCadContext _context;
    private readonly McpTypeCatalog _types;

    public McpObjectTools(McpCadContext context, McpTypeCatalog types)
    {
        _context = context;
        _types = types;
    }

    public void Register(McpToolRegistry tools)
    {
        ThrowIf.Argument.IsNull(tools, nameof(tools));
        tools.Add("objects_find",
            "Поиск: только id и title. Подробности — objects_get. Продолжение страницы — nextOffset; если его нет, поиск закончен.",
            new JObject
            {
                ["type"] = McpSchema.String("Optional exact registered CAD type."),
                ["query"] = McpSchema.String("Substring of TitleName."),
                ["planPath"] = McpSchema.String("Optional workspace DWG path."),
                ["offset"] = McpSchema.Integer("Default 0."),
                ["limit"] = McpSchema.Integer("Default 30, max 100.")
            }, Array.Empty<string>(), Find);
        tools.Add("objects_get",
            "Карточка объекта. fields выбирает свойства, [\"*\"] — все. geometry=true добавляет вершины контура/трассы и связи трассы.",
            new JObject
            {
                ["id"] = McpSchema.String("ID объекта."),
                ["fields"] = McpSchema.Array(McpSchema.String("Имя свойства; * — все.")),
                ["geometry"] = McpSchema.Boolean("Вершины и связи; по умолчанию false.")
            }, new[] { "id" },
            args => Describe(_context.Object(args.Value<string>("id")!), args["fields"] != null,
                args["fields"]?.Values<string>(), args.Value<bool?>("geometry") ?? false), supportsItems: true);
        var itemSchema = new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["type"] = McpSchema.String("Registered point-object type from types_search."),
                ["position"] = McpSchema.Point(),
                ["rotationDegrees"] = McpSchema.Number("Around +Z, default 0."),
                ["ugoName"] = McpSchema.String("Имя из ugo_search; без него первое УГО типа."),
                ["ugoFile"] = McpSchema.String("Файл из ugo_search, если имя неоднозначно."),
                ["properties"] = McpSchema.Properties("Exact scalar property names from types_describe."),
                ["dbType"] = McpSchema.String("Mapped DB type; required together with dbId."),
                ["dbId"] = McpSchema.Integer("PROJECT DB id; import external records first.")
            },
            ["required"] = new JArray("position")
        };
        var defaultProperties = (JObject)itemSchema["properties"]!.DeepClone();
        defaultProperties.Remove("position");
        tools.Add("objects_create",
            "Создать 1–100 точечных объектов. defaults задаёт общие поля; items переопределяют их, включая отдельные properties. type обязателен в defaults или элементе. Возвращает ID в порядке items. Контуры/трассы создаются отдельными инструментами.",
            new JObject
            {
                ["planPath"] = McpSchema.String("Target workspace DWG."),
                ["defaults"] = new JObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["properties"] = defaultProperties
                },
                ["items"] = McpSchema.Array(itemSchema)
            },
            new[] { "planPath", "items" }, CreateObjects, true);
        tools.Add("objects_update",
            "Изменить properties, position, vertices контура, привязку dbType/dbId или waypoints трассы. waypoints — промежуточные точки; [] выпрямляет, связи концов сохраняются. Координаты абсолютные, общего отката нет.",
            new JObject
            {
                ["id"] = McpSchema.String("Object id."),
                ["dbType"] = McpSchema.String("Тип записи БД; вместе с dbId меняет привязку."),
                ["dbId"] = McpSchema.Integer("ID из БД проекта."),
                ["properties"] = McpSchema.Properties("Exact property names from types_describe."),
                ["position"] = McpSchema.Point(),
                ["vertices"] = McpSchema.Array(McpSchema.Point()),
                ["waypoints"] = McpSchema.Array(McpSchema.Point())
            }, new[] { "id" }, Update, true, supportsItems: true);
        tools.Add("objects_delete", "Штатное удаление по ID, с обычными последствиями для связей и дочерних объектов. Пакет — до100 ID.",
            new JObject { ["id"] = McpSchema.String("ID удаляемого объекта.") }, new[] { "id" }, Delete, true, supportsItems: true);
        tools.Add("contour_create",
            "Create a closed polygon contour of any registered contour type. Coordinates are source DWG mm. If marker is supplied, configure the auto-created marker or create one, explicitly bound to the contour.",
            new JObject
            {
                ["planPath"] = McpSchema.String("Target workspace DWG."),
                ["type"] = McpSchema.String("Registered contour type from types_search."),
                ["vertices"] = McpSchema.Array(McpSchema.Point()),
                ["properties"] =
                    McpSchema.Properties(
                        "Exact properties: discover string/numeric IDs, floor heights etc. with types_describe."),
                ["marker"] = new JObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["position"] = McpSchema.Point(),
                        ["rotationDegrees"] = McpSchema.Number("Marker rotation, default 0 degrees."),
                        ["height"] = McpSchema.Number("Marker ZeroLevel in mm, default 0."),
                        ["label"] = McpSchema.String("Default contour TitleName.")
                    }
                }
            }, new[] { "planPath", "type", "vertices" }, CreateContour, true, supportsItems: true);
        tools.Add("traces_connect",
            "Соединить узлы трассой. branch=true делает перпендикулярную врезку узла в существующую трассу того же типа. waypoints — промежуточные точки. Повтор пары может вернуть существующую трассу.",
            new JObject
            {
                ["planPath"] = McpSchema.String("Workspace DWG containing both endpoints."),
                ["type"] = McpSchema.String("Registered AcadTrace subtype, e.g. discovered ScsTrace."),
                ["firstId"] = McpSchema.String("First node id."), ["secondId"] = McpSchema.String("Second node id."),
                ["branch"] = McpSchema.Boolean("По умолчанию false. Для врезки один конец — трасса, второй — обычный узел."),
                ["waypoints"] = McpSchema.Array(McpSchema.Point()),
                ["height"] =
                    McpSchema.Number(
                        "Optional common endpoint height in mm for ISlopeTrace; otherwise retain factory defaults.")
            }, new[] { "planPath", "type", "firstId", "secondId" }, Connect, true, supportsItems: true);
    }

    private object Find(JObject args)
    {
        var type = args.Value<string>("type") is { } typeName ? _types.ResolveObject(typeName) : null;
        int? paper = null;
        if (args.Value<string>("planPath") is { } path)
        {
            _context.ActivatePlan(path);
            paper = CwPlatform.ResolveNotNull<IAcadDBSetter>().GetCurrentPaperId();
        }

        var query = args.Value<string>("query") ?? "";
        var bank = CwPlatform.ResolveNotNull<IObjectBankCollection>();
        var ids = paper.HasValue
            ? bank.GetObjects(type ?? typeof(IAcadObject), paper.Value)
            : bank.GetObjects(type ?? typeof(IAcadObject));
        var matches = ids.Select(id => (IAcadObject?)_context.Net.OpenObject(id)).OfType<IAcadObject>()
            .Where(x => (type == null || type.IsInstanceOfType(x)) && (!paper.HasValue || x.AcId.paperId == paper) &&
                        (x.TitleName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 30, 1, 100);
        var page = matches.Skip(offset).Take(limit).Select(x => new { id = x.AcId.ToGuid().ToString(), title = x.TitleName }).ToArray();
        return new
        {
            total = matches.Length, offset,
            nextOffset = offset + page.Length < matches.Length ? (int?)(offset + page.Length) : null,
            objects = page
        };
    }

    private static object Describe(
        IAcadObject obj,
        bool detailed,
        IEnumerable<string>? fields = null,
        bool geometry = false)
    {
        var node = obj as IAcadNode;
        var trace = obj as AcadTrace;
        var selectedFields = fields?.ToArray();
        return new
        {
            id = obj.AcId.ToGuid().ToString(), type = obj.GetType().FullName, title = obj.TitleName,
            obj.AcId.paperId,
            position = node == null ? null : Coordinates(node.AreaPosition),
            dbId = (obj as IDbIdProvider)?.DbId,
            dbType = obj is IDbIdProvider ? obj.DbRef?.GetType().FullName : null,
            properties = detailed ? McpProperties.Read(obj, selectedFields?.Contains("*") == true ? null : selectedFields) : null,
            vertices = !geometry ? null : obj switch
            {
                IAcadContour contour => contour.CurvePnts.Select(Coordinates).ToArray(),
                AcadTrace connector => connector.GetVertices().Select(Coordinates).ToArray(),
                _ => null
            },
            connectedObjectIds = geometry && trace != null ? trace.GetTraceElements().Select(x => x.ToGuid().ToString()).ToArray() : null,
            markerContourId = obj is TbFloorMarker marker && !marker.LocalCoordinateSystemContourId.IsNull
                ? marker.LocalCoordinateSystemContourId.ToGuid().ToString()
                : null
        };
    }

    private static double[] Coordinates(Point3d point)
    {
        return new[] { point.X, point.Y, point.Z };
    }

    private sealed class ObjectPlacement
    {
        public Type Type { get; init; } = null!;
        public Point3d Position { get; init; }
        public double Rotation { get; init; }
        public IReadOnlyList<Action<object>> Setters { get; init; } = null!;
        public Func<IAcadObject, bool>? BindDatabase { get; init; }
        public WoUgoData Ugo { get; init; }
    }

    private ObjectPlacement PrepareObject(JObject item)
    {
        RejectUnknown(item, "type", "position", "rotationDegrees", "properties", "dbType", "dbId", "ugoName", "ugoFile");
        var type = _types.ResolveObject(item.Value<string>("type") ??
                                        throw new ArgumentException("Нужен type в defaults или элементе items."));
        if (typeof(IAcadContour).IsAssignableFrom(type) || typeof(AcadTrace).IsAssignableFrom(type))
        {
            throw new ArgumentException("Use " + McpTypeCatalog.Mode(type) + " for " + type.Name);
        }

        var binding = PrepareBinding(type, item);

        var rotation = item.Value<double?>("rotationDegrees") ?? 0;
        if (!double.IsFinite(rotation))
        {
            throw new ArgumentException("Rotation must be finite.");
        }

        var placement = new ObjectPlacement
        {
            Type = type,
            Position =
                McpProperties.Point(item["position"] ?? throw new ArgumentException("Each item needs position.")),
            Rotation = rotation.ToRadians(), Setters = McpProperties.Prepare(type, ObjectProperties(item)),
            BindDatabase = binding,
            Ugo = McpUgoCatalog.Select(type, item.Value<string>("ugoName"), item.Value<string>("ugoFile"))
        };
        return placement;
    }

    //based on ElectricaHierarchyTestObjectBuilder.PlaceLuminaire, without a product-specific type or DB selection.
    private object CreateObjects(JObject args)
    {
        if (args["items"] is not JArray items || items.Count is < 1 or > 100 || items.Any(x => x is not JObject))
        {
            throw new ArgumentException("items must contain 1–100 objects.");
        }

        _context.ActivatePlan(args.Value<string>("planPath")!);
        var defaults = args["defaults"] switch
        {
            null => new JObject(),
            JObject values => values,
            _ => throw new ArgumentException("defaults должен быть объектом.")
        };
        RejectUnknown(defaults, "type", "rotationDegrees", "properties", "dbType", "dbId", "ugoName", "ugoFile");
        var mergeSettings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge };
        var placements = items.Cast<JObject>().Select(item =>
        {
            var values = (JObject)defaults.DeepClone();
            values.Merge(item, mergeSettings);
            return PrepareObject(values);
        }).ToArray();
        var created = new JArray();
        using var quiet = new QuietObjectCreation(_context.Factory);
        try
        {
            foreach (var placement in placements)
            {
                var node = _context.Factory.CreateObject(_context.Net, placement.Ugo, placement.Type, placement.Position, placement.Rotation, Point3d.AxeZ)
                           ?? throw new InvalidOperationException("Не удалось создать " + placement.Type.Name);
                created.Add(node.AcId.ToGuid().ToString());
                McpProperties.Apply(node, placement.Setters);
                var savedByBinding = placement.BindDatabase?.Invoke(node) == true;
                if (!savedByBinding)
                {
                    node.SaveMe();
                }
                node.UpdateView();
            }

            return new
            {
                createdObjectIds = created
            };
        }
        catch (Exception exception)
        {
            exception.Data["createdObjectIds"] = created;
            throw;
        }
    }

    private object Update(JObject args)
    {
        _context.ForgetOpenedObjects();
        var obj = _context.Object(args.Value<string>("id")!);
        var setters = McpProperties.Prepare(obj.GetType(), ObjectProperties(args));
        var binding = PrepareBinding(obj.GetType(), args);
        var position = args["position"] == null ? (Point3d?)null : McpProperties.Point(args["position"]!);
        var vertices = args["vertices"] == null ? null : ReadContourVertices(args["vertices"]!);
        var waypoints = args["waypoints"] == null ? null : ReadWaypoints(args["waypoints"]!);
        if (waypoints != null && (obj is not AcadTrace || vertices != null || position.HasValue))
        {
            throw new ArgumentException("waypoints задаёт маршрут трассы отдельно от position/vertices.");
        }
        if (vertices != null && obj is not IAcadContour)
        {
            throw new ArgumentException("vertices можно менять только у контуров.");
        }
        if (vertices != null && position.HasValue)
        {
            throw new ArgumentException("Передайте либо vertices, либо position: вершины уже задают абсолютные координаты.");
        }
        if (position.HasValue && obj is not IAcadNode)
        {
            throw new ArgumentException("Only point/contour nodes can be moved.");
        }

        var applied = new JArray();
        try
        {
            applied.Add(obj.AcId.ToGuid().ToString());
            McpProperties.Apply(obj, setters);
            var savedByBinding = binding?.Invoke(obj) == true;
            if (waypoints != null)
            {
                SetTraceWaypoints((AcadTrace)obj, waypoints);
            }
            if (vertices != null)
            {
                var contour = (IAcadContour)obj;
                contour.SetVertexArray(vertices);
                // SetVertexArray сам перехватывает исключения: проверяем фактическую геометрию из CAD.
                _context.ForgetOpenedObjects();
                var actual = _context.Object(obj.AcId.ToGuid().ToString()) as IAcadContour;
                if (actual == null || !actual.CurvePnts.SequenceEqual(vertices))
                {
                    throw new InvalidOperationException("Не удалось изменить вершины контура.");
                }
            }
            if (position.HasValue)
            {
                ((IAcadNode)obj).MoveToPosition(position.Value);
            }

            if (!savedByBinding || waypoints != null || vertices != null || position.HasValue)
            {
                obj.SaveMe();
            }
            (obj as IAcadNode)?.UpdateView();
            return new { id = obj.AcId.ToGuid().ToString() };
        }
        catch (Exception exception)
        {
            exception.Data["appliedObjectIds"] = applied;
            throw;
        }
    }

    private object Delete(JObject args)
    {
        _context.ForgetOpenedObjects();
        var id = args.Value<string>("id")!;
        _context.Object(id).Delete();
        return new { id };
    }

    private object CreateContour(JObject args)
    {
        var type = _types.ResolveObject(args.Value<string>("type")!);
        if (!typeof(IAcadContour).IsAssignableFrom(type))
        {
            throw new ArgumentException("type must implement IAcadContour.");
        }

        var points = ReadContourVertices(args["vertices"]!);

        var setters = McpProperties.Prepare(type, ObjectProperties(args));
        var markerDescription = args["marker"] as JObject;
        if (args["marker"] != null && markerDescription == null)
        {
            throw new ArgumentException("marker must be an object.");
        }

        if (markerDescription != null && !typeof(ILocalCoordinateSystemContour).IsAssignableFrom(type))
        {
            throw new ArgumentException(
                "This contour does not support a coordinate-system marker (rooms do not need one).");
        }

        if (markerDescription != null)
        {
            RejectUnknown(markerDescription, "position", "rotationDegrees", "height", "label");
        }

        var markerPosition = markerDescription?["position"] is { } token ? McpProperties.Point(token) : points[0];
        var markerHeight = markerDescription?.Value<double?>("height") ?? 0;
        var markerAngle = markerDescription?.Value<double?>("rotationDegrees") ?? 0;
        if (!double.IsFinite(markerHeight) || !double.IsFinite(markerAngle))
        {
            throw new ArgumentException("Marker height/rotation must be finite.");
        }

        _context.ActivatePlan(args.Value<string>("planPath")!);
        var created = new JArray();
        using var quiet = new QuietObjectCreation(_context.Factory);
        try
        {
            var create = typeof(IWoObjectFactory).GetMethods().Single(x => x.Name == "CreateContour" &&
                                                                           x.GetGenericArguments().Length == 1 &&
                                                                           x.GetParameters().Length == 2 &&
                                                                           x.GetParameters()[1].ParameterType ==
                                                                           typeof(List<Point3d>));
            var contour = (IAcadContour?)create.MakeGenericMethod(type)
                              .Invoke(_context.Factory, new object[] { _context.Net, points })
                          ?? throw new InvalidOperationException("Contour creation failed.");
            created.Add(contour.AcId.ToGuid().ToString());
            McpProperties.Apply(contour, setters);
            contour.CirclePos = AcadContour.CalcCenterPoint(points.Take(points.Count - 1).ToList());
            contour.SaveMe();
            contour.UpdateView();
            var markers = _context.Net.GetNodes<TbFloorMarker>()
                .Where(x => x.LocalCoordinateSystemContourId == contour.AcId).ToArray();
            foreach (var marker in markers)
            {
                created.Add(marker.AcId.ToGuid().ToString());
            }

            TbFloorMarker? configuredMarker = null;
            if (markerDescription != null)
            {
                if (markers.Length > 1)
                {
                    throw new InvalidOperationException(
                        "Factory unexpectedly created multiple markers for this contour.");
                }

                configuredMarker = markers.SingleOrDefault();
                if (configuredMarker == null)
                {
                    configuredMarker = (TbFloorMarker)CreatePointObject(typeof(TbFloorMarker), markerPosition,
                        markerAngle.ToRadians());
                    created.Add(configuredMarker.AcId.ToGuid().ToString());
                }
                else
                {
                    configuredMarker.MoveToPosition(markerPosition);
                    configuredMarker.SetUgoRotation(Point3d.AxeX.GetRotated(Point3d.AxeZ, markerAngle.ToRadians()));
                }

                configuredMarker.IsMainFloorMarker = true;
                configuredMarker.MarkerLabel = markerDescription.Value<string>("label") ?? contour.TitleName;
                configuredMarker.ZeroLevel = markerHeight;
                configuredMarker.LocalCoordinateSystemContour = (ILocalCoordinateSystemContour)contour;
                configuredMarker.SaveMe();
                configuredMarker.UpdateView();
            }

            return new
            {
                contour = new { id = contour.AcId.ToGuid().ToString() },
                marker = configuredMarker == null ? null : new { id = configuredMarker.AcId.ToGuid().ToString() }
            };
        }
        catch (Exception exception)
        {
            exception.Data["createdObjectIds"] = created;
            throw;
        }
    }

    private static List<Point3d> ReadContourVertices(JToken value)
    {
        if (value is not JArray array || array.Count is < 3 or > 10001)
        {
            throw new ArgumentException("vertices должен содержать 3–10000 вершин многоугольника.");
        }
        var points = array.Select(McpProperties.Point).ToList();
        if (points[0].Equals(points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }
        if (points.Count > 10000 || points.Distinct().Count() < 3)
        {
            throw new ArgumentException("Нужны минимум три различные вершины, максимум 10000.");
        }
        points.Add(points[0]);
        return points;
    }

    private object Connect(JObject args)
    {
        var type = _types.ResolveObject(args.Value<string>("type")!);
        if (!typeof(AcadTrace).IsAssignableFrom(type))
        {
            throw new ArgumentException("type must inherit AcadTrace.");
        }

        var height = args.Value<double?>("height");
        var waypoints = args["waypoints"] == null ? null : ReadWaypoints(args["waypoints"]!);
        if (height.HasValue && (!double.IsFinite(height.Value) || !typeof(ISlopeTrace).IsAssignableFrom(type)))
        {
            throw new ArgumentException("A finite height can only be set on an ISlopeTrace type.");
        }

        _context.ActivatePlan(args.Value<string>("planPath")!);
        var first = _context.Object(args.Value<string>("firstId")!);
        var second = _context.Object(args.Value<string>("secondId")!);
        var branch = args.Value<bool?>("branch") ?? false;
        var paper = CwPlatform.ResolveNotNull<IAcadDBSetter>().GetCurrentPaperId();
        if (first.AcId == second.AcId || first.AcId.paperId != paper || second.AcId.paperId != paper ||
            first is not IAcadNode || second is not IAcadNode)
        {
            throw new ArgumentException("Endpoints must be two distinct nodes on planPath.");
        }
        if (branch && ((first is AcadTrace) == (second is AcadTrace) ||
                       !type.IsInstanceOfType(first is AcadTrace ? first : second)))
        {
            throw new ArgumentException("Для branch нужен один обычный узел и одна трасса выбранного type.");
        }

        var method = typeof(IAcadTraceManager).GetMethods().Single(x =>
            x.Name == (branch ? "CreatePerpTrace" : "CreateTrace") && x.IsGenericMethod && x.GetParameters().Length == 2);
        using var quiet = new QuietObjectCreation(_context.Factory);
        var created = new JArray();
        try
        {
            var id = (GlobalId)method.MakeGenericMethod(type).Invoke(CwPlatform.ResolveNotNull<IAcadTraceManager>(),
                new object[] { first.AcId, second.AcId })!;
            if (id.IsNull)
            {
                throw new InvalidOperationException("Trace manager did not create a connection.");
            }

            created.Add(id.ToGuid().ToString());
            // Соединитель использует собственную сеть: заново читаем изменённые объекты из CAD.
            _context.ForgetOpenedObjects();
            var trace = _context.Net.OpenObject(id) as AcadTrace ??
                        throw new InvalidOperationException("Created trace cannot be read.");
            if (waypoints != null)
            {
                SetTraceWaypoints(trace, waypoints);
            }
            if (height.HasValue)
            {
                ((ISlopeTrace)trace).FirstPointHeight = height.Value;
                ((ISlopeTrace)trace).LastPointHeight = height.Value;
            }

            trace.SaveMe();
            trace.UpdateView();
            return new
            {
                trace = new { id = trace.AcId.ToGuid().ToString() }
            };
        }
        catch (Exception exception)
        {
            exception.Data["createdObjectIds"] = created;
            throw;
        }
    }

    private static Point3d[] ReadWaypoints(JToken value)
    {
        if (value is not JArray { Count: <= 10000 } array)
        {
            throw new ArgumentException("waypoints — массив из 0–10000 промежуточных точек.");
        }
        return array.Select(McpProperties.Point).ToArray();
    }

    private static void SetTraceWaypoints(AcadTrace trace, IReadOnlyList<Point3d> waypoints)
    {
        if (trace.IsCurved)
        {
            throw new ArgumentException("waypoints поддерживает ломаную трассу; дуговой маршрут пока не поддерживается.");
        }
        // Концевые вершины и их привязки не трогаем; меняем только промежуточные.
        var middleCount = trace.GetVertexCount() - 2;
        for (var i = middleCount; i > waypoints.Count; i--)
        {
            trace.RemoveVertex(i);
        }
        for (var i = 0; i < waypoints.Count; i++)
        {
            if (i < middleCount)
            {
                trace.SetVertexPosition(i + 1, waypoints[i]);
            }
            else
            {
                trace.AddVertex(i + 1, waypoints[i]);
            }
        }
    }

    private IAcadNode CreatePointObject(
        Type type,
        Point3d position,
        double angle)
    {
        return _context.Factory.CreateObject(_context.Net, McpUgoCatalog.Select(type), type, position, angle, Point3d.AxeZ)
               ?? throw new InvalidOperationException("Factory did not create " + type.FullName);
    }

    private Func<IAcadObject, bool>? PrepareBinding(Type type, JObject args)
    {
        var dbType = args.Value<string>("dbType") is { } name ? _types.ResolveDatabase(name) : null;
        var dbId = args.Value<int?>("dbId");
        if ((dbType == null) != (dbId == null))
        {
            throw new ArgumentException("Передайте вместе dbType и dbId из БД проекта, либо ни одного.");
        }
        if (dbType == null)
        {
            return null;
        }
        if (!typeof(IDbIdProvider).IsAssignableFrom(type) || !McpTypeCatalog.MappedDatabaseTypes(type).Contains(dbType))
        {
            throw new ArgumentException("Тип записи БД не поддерживается объектом " + type.Name);
        }
        var product = CwPlatform.ResolveNotNull<IDatabaseProxyComponent>().GetDataObject(dbType, dbId!.Value)?.Object as IDbObject
                      ?? throw new ArgumentException("Записи нет в БД проекта. Сначала database_import.");
        var binder = _context.DatabaseBinders.SingleOrDefault(x => x.Supports(type));
        return obj =>
        {
            var savedByBinding = false;
            if (binder != null)
            {
                savedByBinding = binder.Bind(obj, product);
            }
            else
            {
                ((IDbIdProvider)obj).DbId = product.DbId;
                obj.Load();
            }
            if (obj.DbRef?.GetType() != dbType)
            {
                throw new InvalidOperationException("Фактический тип привязки БД отличается от запрошенного.");
            }
            return savedByBinding;
        };
    }

    private static JObject? ObjectProperties(JObject args)
    {
        if (args["properties"] == null)
        {
            return null;
        }

        return args["properties"] as JObject ?? throw new ArgumentException("properties must be an object.");
    }

    private static void RejectUnknown(JObject args, params string[] allowed)
    {
        var unknown = args.Properties().Select(x => x.Name).Except(allowed).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException("Unknown properties: " + string.Join(", ", unknown));
        }
    }

    private sealed class QuietObjectCreation : IDisposable
    {
        private readonly IWoObjectFactory _factory;
        private readonly bool _showProperties;

        public QuietObjectCreation(IWoObjectFactory factory)
        {
            _factory = factory;
            _showProperties = factory.IsShowPropPageOnCreate;
            factory.IsShowPropPageOnCreate = false;
        }

        public void Dispose()
        {
            _factory.IsShowPropPageOnCreate = _showProperties;
        }
    }
}
