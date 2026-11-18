using System;
using System.Collections.Generic;
using System.Linq;
using Nanosoft.Acad;
using Nanosoft.Acad.Interop.NativeComponent;
using Nanosoft.Acad.Net;
using Nanosoft.Piping.Common.Base;
using Nanosoft.Piping.Common.Ifaces;
using Nanosoft.Piping.Common.Pipes;
using Nanosoft.Platform.PlatformIfaces;
using Nanosoft.Studio;
using Nanosoft.Studio.Utils;
using Nanosoft.WS;
using Nanosoft.WS.Addin;
using Nanosoft.WS.Net.Builder;
using Nanosoft.WS.Net.Nodes;
using MyPlugin.HierarchyTestCommands;

namespace MyPlugin.WaterSupply.Psa12661;

/// <summary>
/// Демо-проекты PSA-12661: каждый кейс — отдельный новый проект ВК, чтобы WS_CALC видел только его сети.
/// </summary>
public sealed class WaterSupplyDemoGenerator : IProductDemoGenerator
{
    internal const string RootDirectory = @"C:\test\WsPsa12661";

    public bool Generate(string demoId)
    {
        Action<WaterSupplyPsa12661Scenes>? build = demoId switch
        {
            "PSA12661_OLD" => x => x.Old(),
            "PSA12661_BOILER" => x => x.Boiler(),
            "PSA12661_NO_FLOORS" => x => x.NoFloors(),
            _ => null
        };
        if (build == null)
        {
            return false;
        }

        var console = CwPlatform.ResolveNotNull<IAcadHighlightManager>();
        void Log(string line) => console.AcadConsolePrint("\n" + line);
        if (CwPlatform.ResolveNotNull<IApplicationInformation>().ApplicationType != ApplicationTypeEnum.WaterSupply)
        {
            throw new InvalidOperationException("Демо PSA-12661 создаётся в nanoCAD BIM ВК.");
        }

        try
        {
            Run(demoId, build, Log);
        }
        catch (Exception exception) when (exception is MissingMethodException or MissingFieldException or TypeLoadException)
        {
            // Плагин собран против Compile другой ветки: сообщаем, что именно не нашлось, без падения команды.
            Log($"{demoId}: в загруженной сборке продукта нет ожидаемого API ({exception.Message}). Пересоберите плагин против текущей ветки.");
        }
        return true;
    }

    private static void Run(string demoId, Action<WaterSupplyPsa12661Scenes> build, Action<string> log)
    {
        var projects = new HierarchyTestProjectService(CwPlatform.ResolveNotNull<IAcadNetFactory>(), CwPlatform.ResolveNotNull<IEngProjectService>());
        IAcadNet? net;
        using (var closer = new WaterSupplySettingsWindowCloser())
        {
            net = projects.Create(ProjectTypes.WaterSupply, RootDirectory);
            if (closer.Closed)
            {
                log($"{demoId}: окно настроек, которое ВК всегда показывает при создании проекта, закрыто автоматически.");
            }
        }
        if (net == null)
        {
            return;
        }

        var setup = WaterSupplyDemoSetup.Create(WaterSupplyDemoSetup.ProjectDirectory());
        log($"{demoId}: {setup.Summary}.");
        var objects = new WaterSupplyDemoObjects(setup, log);
        var contours = new HierarchyTestContourPainter(CwPlatform.ResolveNotNull<IWoObjectFactory>(), CwPlatform.ResolveNotNull<IUgoTypes>(),
            CwPlatform.ResolveNotNull<IUgoBaseComponent>(), CwPlatform.ResolveNotNull<ICwCadDrawManager>(), typeof(TbFloorContour));
        try
        {
            build(new WaterSupplyPsa12661Scenes(net, contours, objects));
        }
        catch (WaterSupplyDemoStepException exception)
        {
            // Без обновления модели и сохранения: недостроенную сеть дальше не трогаем.
            CwPlatform.ResolveNotNull<ICwCadDocService>().ZoomAll();
            log($"{demoId}: остановлено — {exception.Message} Проект не сохранён, его можно закрыть без сохранения.");
            return;
        }

        Refresh(setup, log);
        Check(log);
        CwPlatform.ResolveNotNull<ICwCadDocService>().ZoomAll();
        global::Nanosoft.Studio.Studio.Instance.Workspace.SaveAll();
        log($"{demoId}: проект создан. Дальше вручную: WS_CALC → «Показать сеть».");
    }

    // Штатное «Обновление модели»: ThreadId труб, системы по соединениям, вертикальные подводки к приборам.
    private static void Refresh(WaterSupplyDemoSetup setup, Action<string> log)
    {
        try
        {
            CwPlatform.ResolveNotNull<INetRefresh>().Refresh();
        }
        catch (Exception exception)
        {
            log($"«Обновление модели» завершилось ошибкой: {exception.Message}. Выполните его вручную перед WS_CALC.");
        }

        // Привязка стояков к трубе, как в руководстве; обычно её уже поставил автоподбор по горизонтальной трубе.
        var net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
        net.Load();
        foreach (var stand in net.GetNodes<TbStandPipe>().Where(x => x.GetPipeParts().Count > 0 && x.PipeDbIdProp <= 0))
        {
            stand.PipeDbIdProp = setup.PipeDbId;
            stand.Modify();
        }
        net.ModelObjects.SaveMeModifiedObjects();
    }

    // Та же сборка расчётной сети, что в WsProjectCalculator.Calculate, но без расчёта и окон.
    private static void Check(Action<string> log)
    {
        var checks = CwPlatform.Resolve<ITbNetCheckService>();
        var standPipes = CwPlatform.Resolve<IStandPipes>();
        if (checks == null || standPipes == null)
        {
            log("Проверка сети пропущена: не найдены сервисы построения сети ВК.");
            return;
        }

        var net = CwPlatform.ResolveNotNull<IAcadNetFactory>().NewNet();
        net.Load(false);
        var consumers = net.Nodes.OfType<ConsumerBase>().Count();
        var nets = new TbNetBuilder(net, CwPlatform.ResolveNotNull<IWaterSystemService>(), checks, standPipes).BuildNets();
        var lines = new List<string> { $"Проверка: сетей {nets.Count}, приборов в проекте {consumers}." };
        foreach (var item in nets)
        {
            var reached = item.Nodes.OfType<TbNodeConsumer>().Count();
            lines.Add($"  {item.SystemName} «{item.SystemInput?.InputName}»: узлов {item.Nodes.Count}, приборов {reached}, ошибок сети {item.NetErrors.Count}" +
                      (item.Root == null ? ", корень сети не построен" : ""));
            lines.AddRange(item.NetErrors.Take(5).Select(x => "    " + x.Message));
        }
        lines.ForEach(log);
    }
}
