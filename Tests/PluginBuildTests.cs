using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;

namespace MyPlugin.Tests;

// Проверка готовых сборок без загрузки native-зависимостей nanoCAD в процесс тестов.
public class PluginBuildTests
{
    private static string Root => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    private static string Configuration => new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
    private static string Core => Path.Combine(Root, "bin", Configuration, "net6.0-windows");
    private static string Electrica => Path.Combine(Root, "Electrica", "bin", Configuration, "net6.0-windows");

    [Test]
    public void Latest_contains_the_complete_current_build_without_vendor_dlls()
    {
        var latest = Path.Combine(Root, "artifacts", "LATEST");
        Assert.That(Directory.GetFiles(latest, "*.dll").Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "MyPlugin.dll", "MyPlugin.Electrica.dll" }));
        foreach (var file in new[] { "MyPlugin.dll", "MyPlugin.Electrica.dll" })
        {
            var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Electrica, file)).ProductVersion;
            Assert.That(System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(latest, file)).ProductVersion, Is.EqualTo(expected));
        }
        Assert.That(File.Exists(Path.Combine(latest, "Mcp", "settings.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(latest, "Mcp", "Skills", "platform", "SKILL.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(latest, "README.md")), Is.True);
    }

    [Test]
    public void All_commands_have_platform_registration_attributes()
    {
        using var stream = File.OpenRead(Path.Combine(Core, "MyPlugin.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var names = metadata.CustomAttributes.Select(metadata.GetCustomAttribute)
            .Where(x => x.Constructor.Kind == HandleKind.MemberReference)
            .Where(x => IsCommandAttribute(metadata, x))
            .Select(x => ReadCommandName(metadata, x)).ToArray();
        Assert.That(names, Is.EquivalentTo(new[]
        {
            "CW_MYPLUGIN_VERSION", "CW_MCP_START", "CW_MCP_STOP",
            "CW_HIERARCHY_TEST_GENERATE_SAMPLE", "CW_HIERARCHY_TEST_GENERATE_SAMPLE_ERROR",
            "CW_HIERARCHY_TEST_GENERATE3D", "CW_HIERARCHY_TEST_GENERATE3D_ALL", "CW_HIERARCHY_TEST_VALIDATE"
        }));
    }

    [Test]
    public void Both_modules_have_the_same_three_part_product_version()
    {
        var core = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Core, "MyPlugin.dll"));
        var module = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Electrica, "MyPlugin.Electrica.dll"));
        Assert.That(core.ProductVersion, Does.Match(@"^0\.\d+\.\d+$"));
        Assert.That(module.ProductVersion, Is.EqualTo(core.ProductVersion));
    }

    [Test]
    public void Product_module_depends_on_core_but_core_does_not_depend_on_electrica()
    {
        Assert.That(References(Core, "MyPlugin.dll"), Has.None.StartsWith("Nanosoft.Electrica"));
        Assert.That(References(Core, "MyPlugin.dll"), Has.None.StartsWith("Nanosoft.ObjectLib.ESN"));
        Assert.That(References(Electrica, "MyPlugin.Electrica.dll"), Does.Contain("MyPlugin"));
    }

    [Test]
    public void Build_outputs_do_not_contain_copies_of_vendor_dlls()
    {
        foreach (var directory in new[] { Core, Electrica })
        {
            Assert.That(Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetFileName), Has.All.StartsWith("MyPlugin"));
        }
    }

    [Test]
    public void Configuration_skills_and_sample_plans_are_deployed_with_core()
    {
        foreach (var path in new[]
        {
            "Mcp/settings.json", "Mcp/Skills/platform/SKILL.md", "Mcp/Skills/electrica/SKILL.md",
            "Mcp/Skills/electrica-lighting/SKILL.md", "Mcp/Skills/electrica-network/SKILL.md",
            "Mcp/Skills/electrica-cables/SKILL.md", "Mcp/Tests/QuickStartPlans/План1.dwg", "Mcp/Tests/QuickStartPlans/План2.dwg"
        })
        {
            Assert.That(File.Exists(Path.Combine(Core, path)), Is.True, path);
        }
    }

    private static bool IsCommandAttribute(MetadataReader metadata, CustomAttribute attribute)
    {
        var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        return parent.Kind == HandleKind.TypeReference &&
               metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "CommandMethodAttribute";
    }

    private static string? ReadCommandName(MetadataReader metadata, CustomAttribute attribute)
    {
        var blob = metadata.GetBlobReader(attribute.Value);
        Assert.That(blob.ReadUInt16(), Is.EqualTo(1));
        return blob.ReadSerializedString();
    }

    private static string[] References(string directory, string file)
    {
        using var stream = File.OpenRead(Path.Combine(directory, file));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(x => metadata.GetString(metadata.GetAssemblyReference(x).Name)).ToArray();
    }
}
