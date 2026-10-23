using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Nanosoft.Acad;
using Nanosoft.CAE;
using Nanosoft.Electrica.Calc.LightCalc;
using Nanosoft.ObjectLib.ESN.ElContours;
using Nanosoft.ObjectLib.WireCommon.CaeFider.GroupRouter;

using Guard;

namespace MyPlugin.Electrica;

/// <summary>Единственная граница к приватным алгоритмам. Сигнатуры проверяются при регистрации MCP.</summary>
internal sealed class ElectricaPrivateOperations
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly MethodInfo BusChanger = Required(typeof(Nanosoft.ObjectLib.ESN.Modul.ElBusTypeSetter),
        "CreateBusChanger", typeof(IBusChanger), new[] { typeof(IAcadObject) });
    private readonly MethodInfo _place = Required(typeof(ElLightingCalculator), "CalculateAndInstallLightingForRoom",
        typeof(bool), new[] { typeof(ElRoomExtension), typeof(WoUgoData) });
    private readonly MethodInfo _devices = Required(typeof(GroupBuilderWizard), "CreateDevices", typeof(void),
        new[] { typeof(IAcadNet), typeof(ICollection<IAcadObject>), typeof(ICollection<IAcadObject>) });
    private readonly MethodInfo _route;
    private readonly Type _routeResultType;
    private readonly PropertyInfo _notRouted;

    internal ElectricaPrivateOperations()
    {
        _route = typeof(GroupBuilderWizard).GetMethod("AutoBuildSelectedObject", PrivateInstance)
                 ?? throw new MissingMethodException(typeof(GroupBuilderWizard).FullName, "AutoBuildSelectedObject");
        var parameters = _route.GetParameters();
        var prefix = new[] { typeof(bool), typeof(IAcadNet), typeof(List<IAcadObject>), typeof(IAcadObject) };
        if (_route.ReturnType != typeof(void) || parameters.Length != 5 || !parameters.Take(4).Select(x => x.ParameterType).SequenceEqual(prefix))
        {
            throw new InvalidOperationException("Изменилась сигнатура прокладки кабелей; требуется обновить MCP-адаптер.");
        }
        _routeResultType = parameters[4].ParameterType;
        _notRouted = _routeResultType.GetProperty("NotRoutedSlaves")
                     ?? throw new MissingMemberException(_routeResultType.FullName, "NotRoutedSlaves");
        if (_notRouted.PropertyType != typeof(List<GlobalId>))
        {
            throw new InvalidOperationException("Изменился результат прокладки кабелей; требуется обновить MCP-адаптер.");
        }
    }

    internal static IBusChanger CreateBusChanger(Nanosoft.ObjectLib.ESN.Modul.ElBusTypeSetter setter, IAcadObject obj)
    {
        ThrowIf.Argument.IsNull(setter, nameof(setter));
        ThrowIf.Argument.IsNull(obj, nameof(obj));
        return (IBusChanger)Invoke(BusChanger, setter, new object[] { obj })!;
    }

    internal bool Place(
        ElLightingCalculator calculator,
        ElRoomExtension room,
        WoUgoData ugo)
    {
        ThrowIf.Argument.IsNull(calculator, nameof(calculator));
        ThrowIf.Argument.IsNull(room, nameof(room));
        return (bool)Invoke(_place, calculator, new object[] { room, ugo })!;
    }

    internal IReadOnlyList<GlobalId> Route(
        GroupBuilderWizard wizard,
        IAcadNet net,
        IReadOnlyList<IAcadObject> targets,
        bool replace)
    {
        ThrowIf.Argument.IsNull(wizard, nameof(wizard));
        ThrowIf.Argument.IsNull(net, nameof(net));
        ThrowIf.Argument.IsNull(targets, nameof(targets));
        var traces = new List<IAcadObject>();
        Invoke(_devices, wizard, new object[] { net, traces, new List<IAcadObject>() });
        var result = Activator.CreateInstance(_routeResultType, true)!;
        foreach (var target in targets)
        {
            Invoke(_route, wizard, new[] { replace, net, traces, target, result });
        }
        return ((List<GlobalId>)_notRouted.GetValue(result)!).Distinct().ToArray();
    }

    private static MethodInfo Required(
        Type owner,
        string name,
        Type result,
        Type[] parameters)
    {
        var method = owner.GetMethod(name, PrivateInstance, null, parameters, null);
        if (method == null || method.ReturnType != result)
        {
            throw new InvalidOperationException($"Изменилась сигнатура {owner.Name}.{name}; требуется обновить MCP-адаптер.");
        }
        return method;
    }

    private static object? Invoke(
        MethodInfo method,
        object target,
        object[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
