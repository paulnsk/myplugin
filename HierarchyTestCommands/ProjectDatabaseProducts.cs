using System;
using System.Collections.Generic;
using System.Linq;
using Nanosoft.CAE.Db.DatabaseProxyComponent;
using Nanosoft.Data;
using Nanosoft.Utils;

namespace MyPlugin.HierarchyTestCommands;

/// <summary>Изделия для объектов стенда: из БД проекта, а если их нет — импорт из подключённой внешней (демо) БД.</summary>
internal static class ProjectDatabaseProducts
{
    /// <summary>До maximum изделий типа из БД проекта; пусто — импорт первых maximum из первой внешней БД, где они есть.</summary>
    internal static List<int> GetOrImport(
        IDatabaseProxyComponent databases,
        Type dbType,
        int maximum,
        Func<object, bool>? filter = null)
    {
        var projectSource = databases.GetDb(dbType);
        var result = Objects(projectSource, dbType, filter).Take(maximum).Select(x => x.Id).ToList();
        if (result.Count > 0)
        {
            return result;
        }

        var demoSource = ExternalSources(dbType, projectSource).FirstOrDefault(x => Objects(x, dbType, filter).Any());
        if (demoSource == null)
        {
            return result;
        }

        result.AddRange(Import(projectSource, Objects(demoSource, dbType, filter).Take(maximum).ToArray()).Select(x => x.Id));
        return result;
    }

    internal static IEnumerable<IDataObject> Objects(IDataSource source, Type dbType, Func<object, bool>? filter = null)
    {
        var objects = source.GetDataTypedSource(dbType)?.DataObjects;
        return objects == null
            ? Enumerable.Empty<IDataObject>()
            : objects.Cast<IDataObject>().Where(x => filter == null || x.Object != null && filter(x.Object));
    }

    internal static IEnumerable<IDataSource> ExternalSources(Type dbType, IDataSource projectSource)
    {
        foreach (IDataBaseInfo info in AddIn.DataBase.AllSources)
        {
            var source = info.DataSource;
            if (source != null && !ReferenceEquals(source, projectSource) && source.GetDataTypedSource(dbType) != null)
            {
                yield return source;
            }
        }
    }

    /// <summary>Импорт в БД проекта одним экспортёром: зависимости и серия сохраняют общее соответствие ID.</summary>
    internal static List<IDataObject> Import(IDataSource projectSource, IReadOnlyList<IDataObject> dataObjects)
    {
        // Прямой импорт синхронный, без отложенного показа ProgressShower.
        var exporter = new DataBaseExporter(projectSource);
        return dataObjects.Select(x => exporter.Export(x)
            ?? throw new InvalidOperationException($"Не удалось импортировать изделие «{x.Text}» в БД проекта.")).ToList();
    }
}
