using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Nanosoft.Studio.Utils;
using Nanosoft.Utils;

// Модуль ВК использует внутренние описания сцен стенда иерархии.
[assembly: InternalsVisibleTo("MyPlugin.WaterSupply")]

namespace MyPlugin;

/// <summary>Демо-проект, который строит продуктовый модуль: ядро не зависит от сборок продукта.</summary>
public interface IProductDemoGenerator
{
    /// <summary>Создать демо-проект; false — модуль такого демо не знает.</summary>
    bool Generate(string demoId);
}

internal static class ProductModules
{
    // Модуль работающего продукта; остальные продуктовые зависимости не загружаем.
    internal static Assembly? Load()
    {
        var product = CwPlatform.ResolveNotNull<IApplicationInformation>().ApplicationType;
        var name = $"MyPlugin.{product}";
        var path = Path.Combine(Path.GetDirectoryName(typeof(ProductModules).Assembly.Location)!, name + ".dll");
        if (!File.Exists(path))
        {
            return null;
        }

        var context = AssemblyLoadContext.GetLoadContext(typeof(ProductModules).Assembly)!;
        // Повторная загрузка того же пути в контекст бросает исключение: берём уже загруженную сборку.
        return context.Assemblies.FirstOrDefault(x => x.GetName().Name == name) ?? context.LoadFromAssemblyPath(path);
    }

    internal static void GenerateDemo(string demoId)
    {
        var assembly = Load()
            ?? throw new InvalidOperationException("Для текущего продукта нет модуля MyPlugin с демо-проектами. Демо PSA-12661 создаётся в nanoCAD BIM ВК.");
        var generators = assembly.GetExportedTypes()
            .Where(x => !x.IsAbstract && typeof(IProductDemoGenerator).IsAssignableFrom(x))
            .Select(x => (IProductDemoGenerator)Activator.CreateInstance(x)!).ToArray();
        if (!generators.Any(x => x.Generate(demoId)))
        {
            throw new InvalidOperationException($"Модуль {assembly.GetName().Name} не умеет строить демо {demoId}. Демо PSA-12661 создаётся в nanoCAD BIM ВК.");
        }
    }
}
