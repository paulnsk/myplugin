using System;
using System.Reflection;
using System.Windows;
using MyPlugin.HierarchyTestCommands;
using MyPlugin.Mcp;
using Teigha.Runtime;

namespace MyPlugin;

public class PluginCommands
{
    private static readonly Lazy<HierarchyTestServices> Tests = new(() => new HierarchyTestServices());

    [CommandMethod("CW_MYPLUGIN_VERSION", CommandFlags.Session)]
    public void ShowVersion()
    {
        var version = typeof(PluginCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        MessageBox.Show(version, "MyPlugin");
    }

    [CommandMethod("CW_MCP_START", CommandFlags.Session)]
    public void StartMcp() => Run(McpRuntime.Start);

    [CommandMethod("CW_MCP_STOP", CommandFlags.Session)]
    public void StopMcp() => Run(McpRuntime.Stop);

    [CommandMethod("CW_HIERARCHY_TEST_GENERATE_SAMPLE", CommandFlags.Session)]
    public void GenerateSample() => Run(() => Tests.Value.Generate(false));

    [CommandMethod("CW_HIERARCHY_TEST_GENERATE_SAMPLE_ERROR", CommandFlags.Session)]
    public void GenerateSampleError() => Run(() => Tests.Value.Generate(true));

    [CommandMethod("CW_HIERARCHY_TEST_GENERATE_ROTATIONS", CommandFlags.Session)]
    public void GenerateRotations() => Run(() => Tests.Value.GenerateRotations());

    [CommandMethod("CW_HIERARCHY_TEST_GENERATE3D", CommandFlags.Session)]
    public void Generate3D() => Run(() => Tests.Value.Export(false));

    [CommandMethod("CW_HIERARCHY_TEST_GENERATE3D_ALL", CommandFlags.Session)]
    public void Generate3DAll() => Run(() => Tests.Value.Export(true));

    [CommandMethod("CW_HIERARCHY_TEST_VALIDATE", CommandFlags.Session)]
    public void Validate() => Run(() => Tests.Value.Validate());

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (System.Exception exception)
        {
            MessageBox.Show(exception.ToString(), "MyPlugin — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
