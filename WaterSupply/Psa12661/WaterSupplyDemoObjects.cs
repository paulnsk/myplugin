using System;
using System.Collections.Generic;
using System.Linq;
using Guard;
using Nanosoft.Acad;
using Nanosoft.Acad.Ifaces;
using Nanosoft.Acad.Interop.Enum;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.Net;
using Nanosoft.Piping.Common.Fixture;
using Nanosoft.Piping.Common.Pipes;
using Nanosoft.Utils.Point3d;
using Nanosoft.WS;
using Nanosoft.WS.Equipment;
using Nanosoft.WS.Model;

namespace MyPlugin.WaterSupply.Psa12661;

/// <summary>
/// Объекты сети ВК так, как их ставит пользователь: УГО из базы, трубы между точками подключения,
/// конфигурация трубы системы В1. Все трубы на одной высоте от пола этажа, вертикальные подводки строит «Обновление модели».
/// </summary>
internal sealed class WaterSupplyDemoObjects
{
    internal const double PipeHeight = 300;
    // Имя шаблона выноски, которого нет ни у одного шаблона продукта: штатное обновление выносок её не трогает (как PlanSignNotes).
    private const string LabelPattern = "Psa12661NetLabel";
    // Выноска прямо над объектом.
    private const double LabelRise = 600;
    // Высота текста в модели, мм — как подписи этажей и зон стенда (HierarchyTestContourPainter.DrawCaption).
    private const double LabelTextHeight = 525;
    // Яркие на тёмном фоне; без зелёного — им «Показать сеть» подсвечивает объекты сети.
    private static readonly CwColor[] LabelColors = { CwColors.Red, CwColors.Yellow, CwColors.Magenta, CwColors.DarkOrange, CwColors.DodgerBlue };

    private readonly IWoObjectFactory _factory;
    private readonly IUgoTypes _ugoTypes;
    private readonly IUgoBaseComponent _ugoBase;
    private readonly IAcadTraceManager _traces;
    private readonly IAcadNetFactory _nets;
    private readonly IWoAttributeManager _attributes;
    private readonly INoteService _notes;
    private readonly ICadDocumentScale _scale;
    private readonly IAcadLayerService _layers;
    private readonly WaterSupplyDemoSetup _setup;
    private readonly Action<string> _log;
    private readonly Dictionary<Type, int?> _productIds = new();

    internal WaterSupplyDemoObjects(WaterSupplyDemoSetup setup, Action<string> log)
    {
        ThrowIf.Argument.IsNull(setup, nameof(setup));
        ThrowIf.Argument.IsNull(log, nameof(log));
        _factory = CwPlatform.ResolveNotNull<IWoObjectFactory>();
        _ugoTypes = CwPlatform.ResolveNotNull<IUgoTypes>();
        _ugoBase = CwPlatform.ResolveNotNull<IUgoBaseComponent>();
        _traces = CwPlatform.ResolveNotNull<IAcadTraceManager>();
        _nets = CwPlatform.ResolveNotNull<IAcadNetFactory>();
        _attributes = CwPlatform.ResolveNotNull<IWoAttributeManager>();
        _notes = CwPlatform.ResolveNotNull<INoteService>();
        _scale = CwPlatform.ResolveNotNull<ICadDocumentScale>();
        _layers = CwPlatform.ResolveNotNull<IAcadLayerService>();
        _setup = setup;
        _log = log;
    }

    internal TbSystemIn Input(IAcadNet net, Point3d position, string title)
    {
        var input = Place<TbSystemIn>(net, position);
        input.SystemPrp = _setup.SystemName;
        input.TitleName = title;
        input.CalculationOption = WsCalculationOptionsEnum.CALCULATE_ALL;
        input.SecuredHead = 30;
        return Save(input);
    }

    internal T Consumer<T>(IAcadNet net, Point3d position, string title, double mountingHeight) where T : ConsumerBase
    {
        var consumer = Place<T>(net, position);
        consumer.SystemPrp = _setup.SystemName;
        consumer.TitleName = title;
        // Автомаркировка «Обновления модели» перезаписала бы поясняющие имена.
        consumer.IsDoMarking = false;
        consumer.UserIndex = _setup.ConsumerId;
        // Обычно ставит WsDeviceCreator при создании; без типа прибора расход не считается.
        if (consumer.DischargeTypeDbId == -1)
        {
            consumer.DischargeType = CalcTools.GetDefaultDeviceType(consumer.DbIdType);
        }
        if (ProductId(consumer.DbIdType) is { } dbId)
        {
            consumer.DbId = dbId;
        }
        consumer.MountingHeightMm = mountingHeight;
        return Save(consumer);
    }

    internal TbStandPipe Stand(IAcadNet net, Point3d position, string name, double storeyHeight)
    {
        var stand = Place<TbStandPipe>(net, position);
        stand.SystemPrp = _setup.SystemName;
        // Обозначение стояка = TrunkName: по нему расчёт продолжает сеть на соседнем этаже того же здания.
        stand.TitleName = name;
        stand.PipeHeightUp = storeyHeight;
        stand.PipeHeightDown = 0;
        return Save(stand);
    }

    // Пара разрывов с одинаковым Name соединяется при загрузке сети отрезком длины Length (мм).
    internal TubeTraceBreak Break(IAcadNet net, Point3d position, string name, double length)
    {
        var item = Place<TubeTraceBreak>(net, position);
        item.SystemPrp = _setup.SystemName;
        item.Name = name;
        item.Length = length;
        return Save(item);
    }

    /// <summary>
    /// Выноска с текстом на вводе или разрыве: «Сеть 4», «Сеть 4 →», «→ Сеть 4».
    /// Цвета у выноски нет, поэтому выноски каждой сети лежат на своём цветном слое.
    /// </summary>
    internal T Label<T>(T node, (int Net, string Text) label) where T : IAcadNode
    {
        var text = label.Text;
        var position = Position(node) + new Point3d(0, LabelRise, 0);
        var note = _attributes.AddNoteToObject(node.AcId, (int)CadNoteTypeList.UP_LINE_TYPE, LabelPattern,
            (int)AcadNoteLineArrow.SIMPLE_LINE, new[] { text }, position, _notes.NoteShift);
        if (note.IsNull)
        {
            _log($"Выноска «{text}» на «{node.TitleName}» не создана.");
            return node;
        }
        // Размер текста выноски задаётся в мм бумаги.
        if (node.Net.OpenObject(note) is AcadAttributeNote attributeNote)
        {
            attributeNote.TextSize = LabelTextHeight / _scale.GetPaperScale(node.AcId.paperId);
            // Иначе слой выноски по правилу из настроек проекта вернётся в «ВК_Выноски».
            attributeNote.NoteLayerRule = NoteLayerRule.AsIs;
            _layers.SetObjectLayer(attributeNote, new AcadSystemLayer($"PSA12661_Сеть_{label.Net}", LabelColors[(label.Net - 1) % LabelColors.Length]));
        }
        return node;
    }

    /// <summary>Труба между узлами через промежуточные точки; у многоточечного прибора — ближайшая точка подключения.</summary>
    internal TbTrace Pipe(IAcadNode first, IAcadNode second, params Point3d[] waypoints)
    {
        Step($"труба «{first.TitleName}» → «{second.TitleName}»");
        var net = _nets.NewNet();
        var a = Reopen(net, first);
        var b = Reopen(net, second);
        var aCp = NearestCp(a, waypoints.Length > 0 ? waypoints[0] : Position(b));
        var bCp = NearestCp(b, waypoints.Length > 0 ? waypoints[^1] : Position(a));
        if (aCp < 0 || bCp < 0)
        {
            return Fail($"У «{first.TitleName}» или «{second.TitleName}» нет точки подключения — труба не проложена.");
        }
        var trace = _traces.LinkObjects<TbTrace>(net, a.AcId, b.AcId, a.GetCpPosition(aCp), b.GetCpPosition(bCp),
            aCp, bCp, null!, string.Empty, waypoints.ToList());
        return trace == null
            ? Fail($"Труба «{first.TitleName}» → «{second.TitleName}» не создана.")
            : Configure(trace.AcId);
    }

    /// <summary>
    /// Магистраль от start до end с перпендикулярными отводами к branches (тройники).
    /// Строим то, что получается после ручной врезки: отвод от прибора до точки на оси магистрали,
    /// а магистраль — отрезками между этими точками; в каждой точке концы трёх труб связаны явно.
    /// Автоматический разрез трубы движком (moveConnect) при программной прокладке не срабатывает,
    /// а CreatePerpTrace/LinkObjectToConnector (incut в целую трубу) ронял движок — см. заметки стенда.
    /// </summary>
    internal void Main(IAcadNode start, IAcadNode end, params IAcadNode[] branches)
    {
        var net = _nets.NewNet();
        var first = Reopen(net, start);
        var last = Reopen(net, end);
        var firstCp = NearestCp(first, Position(last));
        var lastCp = NearestCp(last, Position(first));
        if (firstCp < 0 || lastCp < 0)
        {
            Fail($"У «{start.TitleName}» или «{end.TitleName}» нет точки подключения — магистраль не проложена.");
        }
        var from = first.GetCpPosition(firstCp);
        var to = last.GetCpPosition(lastCp);

        // Точка прибора, ближайшая к оси (у смесителя вторая точка, Т3, остаётся свободной), и основание перпендикуляра.
        var taps = branches.Select(device =>
            {
                var node = Reopen(net, device);
                var best = node.GetConnPointList(ConnectionType.ConnNode)
                    .Select(cp => (cp, position: node.GetCpPosition(cp)))
                    .Select(x => (x.cp, x.position, t: Along(from, to, x.position)))
                    .Where(x => x.t.HasValue)
                    .Select(x => (x.cp, x.position, t: x.t!.Value, foot: from + (to - from) * x.t!.Value))
                    .OrderBy(x => (x.foot - x.position).Length())
                    .DefaultIfEmpty((-1, default, 0d, default)).First();
                if (best.cp < 0)
                {
                    Fail($"«{device.TitleName}» не напротив магистрали «{start.TitleName}» → «{end.TitleName}».");
                }
                return (device, best.cp, best.position, best.t, best.foot);
            })
            .OrderBy(x => x.t).ToArray();

        // Звенья цепочки: узел и его точка подключения; для отвода — его конец на оси магистрали.
        var previous = (id: first.AcId, cp: firstCp, position: from, title: start.TitleName);
        foreach (var tap in taps)
        {
            Step($"отвод «{tap.device.TitleName}»");
            // Второй конец пока свободен (индекс -1): к нему ниже подключатся оба отрезка магистрали.
            var branch = Configure(Link(net, tap.device.AcId, tap.device.AcId, tap.position, tap.foot, tap.cp, -1,
                $"отвод «{tap.device.TitleName}»").AcId);
            var junction = branch.GetCpIndexByPosition(tap.foot);
            if (junction < 0)
            {
                Fail($"У отвода «{tap.device.TitleName}» нет точки подключения на оси магистрали.");
            }
            Step($"магистраль «{previous.title}» → отвод «{tap.device.TitleName}»");
            Configure(Link(net, previous.id, branch.AcId, previous.position, tap.foot, previous.cp, junction,
                $"отрезок магистрали до «{tap.device.TitleName}»").AcId);
            previous = (branch.AcId, junction, tap.foot, $"отвод «{tap.device.TitleName}»");
        }
        Step($"магистраль «{previous.title}» → «{end.TitleName}»");
        Configure(Link(net, previous.id, last.AcId, previous.position, to, previous.cp, lastCp,
            $"отрезок магистрали до «{end.TitleName}»").AcId);
    }

    /// <summary>
    /// Сеть с кольцом от start до end: труба до развилки, от неё две ветви — прямая и обходная (сбоку на detour) —
    /// сходятся в точке слияния, дальше труба к end. Развилка и слияние собраны как тройники в Main:
    /// к концу одной трубы явно подключены две другие. WS_CALC должен дать ошибку «циклы в рамках одной системы».
    /// </summary>
    internal void Ring(IAcadNode start, IAcadNode end, double forkOffset, double ringLength, double detour)
    {
        var net = _nets.NewNet();
        var first = Reopen(net, start);
        var last = Reopen(net, end);
        var firstCp = NearestCp(first, Position(last));
        var lastCp = NearestCp(last, Position(first));
        if (firstCp < 0 || lastCp < 0)
        {
            Fail($"У «{start.TitleName}» или «{end.TitleName}» нет точки подключения — кольцо не проложено.");
        }
        var from = first.GetCpPosition(firstCp);
        var to = last.GetCpPosition(lastCp);
        var direction = (to - from) * (1 / (to - from).Length());
        var aside = new Point3d(-direction.Y, direction.X, 0) * detour;
        var fork = from + direction * forkOffset;
        var join = fork + direction * ringLength;

        Step("кольцо: труба до развилки");
        var lead = Configure(Link(net, first.AcId, first.AcId, from, fork, firstCp, -1, "труба до развилки кольца").AcId);
        var forkCp = CpAt(lead, fork, "развилки кольца");
        Step("кольцо: прямая ветвь");
        var straight = Configure(Link(net, lead.AcId, lead.AcId, fork, join, forkCp, -1, "прямая ветвь кольца").AcId);
        var joinCp = CpAt(straight, join, "слияния кольца");
        Step("кольцо: обходная ветвь");
        var bypass = _traces.LinkObjects<TbTrace>(net, lead.AcId, straight.AcId, fork, join, forkCp, joinCp,
                         null!, string.Empty, new List<Point3d> { fork + aside, join + aside })
                     ?? throw new WaterSupplyDemoStepException("Не создана обходная ветвь кольца.");
        Configure(bypass.AcId);
        Step($"кольцо: труба к «{end.TitleName}»");
        Configure(Link(net, straight.AcId, last.AcId, join, to, joinCp, lastCp, $"труба от кольца к «{end.TitleName}»").AcId);
    }

    private static int CpAt(AcadTrace trace, Point3d position, string what)
    {
        var cp = trace.GetCpIndexByPosition(position);
        if (cp < 0)
        {
            Fail($"У трубы нет точки подключения в точке {what}.");
        }
        return cp;
    }

    private AcadTrace Link(IAcadNet net, GlobalId firstId, GlobalId secondId, Point3d firstPosition, Point3d secondPosition,
        int firstCp, int secondCp, string what) =>
        _traces.LinkObjects<TbTrace>(net, firstId, secondId, firstPosition, secondPosition, firstCp, secondCp,
            null!, string.Empty, new List<Point3d>())
        ?? throw new WaterSupplyDemoStepException($"Не создан {what}.");

    // Доля пути от from к to до основания перпендикуляра из point; null — основание у концов или вне отрезка.
    private static double? Along(Point3d from, Point3d to, Point3d point)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length < 1)
        {
            return null;
        }
        var t = ((point.X - from.X) * direction.X + (point.Y - from.Y) * direction.Y) / (length * length);
        return t * length > 100 && (1 - t) * length > 100 ? t : null;
    }

    // Как штатная прокладка (PipeSettingsPickerCommon.SetDataToTrace): высота, конфигурация В1, ThreadId нитей.
    private TbTrace Configure(GlobalId id)
    {
        var trace = (TbTrace)_nets.NewNet().OpenObject(id);
        trace.FirstPointHeight = PipeHeight;
        trace.LastPointHeight = PipeHeight;
        trace.CopyThread(_setup.Configuration);
        foreach (var thread in trace.Threads)
        {
            // У новой нити ThreadId пуст, а UpdateThreadIds на пустом падает (ThreadIdHelper.GetSystemName).
            // Штатная прокладка зовёт internal TbTrace.SetThreadIds; публичный путь к нему — сеттер SystemPrp нити.
            thread.SystemPrp = _setup.SystemName;
            thread.SaveMe();
        }
        trace.UpdateView();
        trace.SaveMe();
        return trace;
    }

    private T Place<T>(IAcadNet net, Point3d position) where T : AcadNode
    {
        var typeId = _ugoTypes.GetTypeId(typeof(T));
        var candidates = _ugoBase.UgoList.Where(x => x.UgoElemTypeId == typeId).ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"В базе УГО нет УГО для {typeof(T).Name}.");
        }
        // Папка «Аксонометрия» содержит схемные УГО тех же типов; для плана берём неаксонометрическое.
        var ugo = candidates.FirstOrDefault(x => x.UgoCatalogPath?.IndexOf("Аксонометр", StringComparison.OrdinalIgnoreCase) is null or < 0);
        if (ugo.IsNull())
        {
            ugo = candidates[0];
        }
        var previous = _factory.IsShowPropPageOnCreate;
        _factory.IsShowPropPageOnCreate = false;
        try
        {
            return _factory.CreateObject(net, ugo, typeof(T), position, 0, Point3d.AxeZ) as T
                   ?? throw new InvalidOperationException($"{typeof(T).Name} не создан.");
        }
        finally
        {
            _factory.IsShowPropPageOnCreate = previous;
        }
    }

    private int? ProductId(Type dbType)
    {
        if (!_productIds.TryGetValue(dbType, out var id))
        {
            id = WaterSupplyDemoSetup.ProductIds(dbType).Cast<int?>().FirstOrDefault();
            if (id == null)
            {
                _log($"Нет изделия {dbType.Name} ни в БД проекта, ни в демо-БД: прибор без привязки к БД.");
            }
            _productIds[dbType] = id;
        }
        return id;
    }

    private static T Save<T>(T node) where T : IAcadNode
    {
        node.SaveMe();
        node.UpdateView();
        return node;
    }

    private static IAcadNode Reopen(IAcadNet net, IAcadNode node) =>
        net.OpenObject(node.AcId) as IAcadNode ?? throw new InvalidOperationException($"«{node.TitleName}» не найден на плане.");

    // Точка подключения узла (у многоточечного — первая): ориентир для выбора точки на другом конце трубы.
    private static Point3d Position(IAcadNode node) =>
        node.GetConnectionPoints().FirstOrDefault();

    private static int NearestCp(IAcadNode node, Point3d target) =>
        node.GetConnPointList(ConnectionType.ConnNode)
            .OrderBy(cp => (node.GetCpPosition(cp) - target).Length()).DefaultIfEmpty(-1).First();

    // Перед шагом пишем его в консоль: если движок выдаст нативную ошибку, видно, на каком шаге.
    private void Step(string description) => _log($"  шаг: {description}");

    // Первая же неудача останавливает кейс: следующие шаги на недостроенной сети только портят модель.
    private static TbTrace Fail(string message) => throw new WaterSupplyDemoStepException(message);
}

/// <summary>Шаг построения не удался; кейс остановлен, модель дальше не трогаем.</summary>
internal sealed class WaterSupplyDemoStepException : Exception
{
    internal WaterSupplyDemoStepException(string message) : base(message)
    {
    }
}
