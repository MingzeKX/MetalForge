using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;
using MetalForge.Core.Layout;

namespace MetalForge.Core.Tests;

public sealed class LayoutLoaderTests : IDisposable
{
    private readonly string _userDirectory;

    public LayoutLoaderTests()
    {
        _userDirectory = Path.Combine(Path.GetTempPath(), "metalforge-layout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDirectory);
    }

    private AssetResolver CreateResolver() => new(new AssetOptions
    {
        BuiltInAssetsDirectory = TestPaths.AssetsDirectory,
        UserDirectory = _userDirectory,
    });

    private void WriteUserLayout(string fileName, string content)
    {
        var directory = Path.Combine(_userDirectory, "layouts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, System.Text.Encoding.UTF8);
    }

    [Fact]
    public void LoadsBuiltInLayoutsWithoutErrors()
    {
        var loader = new LayoutLoader(CreateResolver());
        var (presets, diagnostics) = loader.LoadAll();

        var errors = diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "内置布局产生了错误诊断：" + string.Join(" | ", errors.Select(d => d.ToString())));

        Assert.Contains(presets, preset => preset.Id == "default");
        Assert.Contains(presets, preset => preset.Id == "debug");
    }

    [Fact]
    public void DefaultLayout_HasThreeColumnDocumentStructure()
    {
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();
        var preset = presets.Single(p => p.Id == "default");

        Assert.Equal(LayoutNodeKind.Split, preset.Root.Kind);
        Assert.Equal(LayoutOrientation.Vertical, preset.Root.Orientation);
        Assert.Equal(2, preset.Root.Children.Count);

        var topRow = preset.Root.Children[0];
        Assert.Equal(LayoutNodeKind.Split, topRow.Kind);
        Assert.Equal(3, topRow.Children.Count);
        Assert.Equal(LayoutNodeKind.Panel, topRow.Children[0].Kind);
        Assert.Equal("explorer", topRow.Children[0].Id);
        Assert.Equal(LayoutNodeKind.Document, topRow.Children[1].Kind);
        Assert.Equal("inspector", topRow.Children[2].Id);

        var bottom = preset.Root.Children[1];
        Assert.Equal("bottom", bottom.Id);
        Assert.Contains("output.build", bottom.Tabs);
    }

    [Fact]
    public void Proportions_AreNormalizedToSumOne()
    {
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();
        var preset = presets.Single(p => p.Id == "default");

        var proportions = preset.Root.EffectiveProportions();

        Assert.Equal(preset.Root.Children.Count, proportions.Count);
        Assert.Equal(1.0, proportions.Sum(), 6);
    }

    [Fact]
    public void Proportions_AcceptRelativeWeights()
    {
        // 允许作者写 1/2/1 这类权重，而不必自己凑成 1.0
        var node = new LayoutNode
        {
            Kind = LayoutNodeKind.Split,
            Children =
            [
                new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "a" },
                new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "b" },
                new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "c" },
            ],
            Proportions = [1, 2, 1],
        };

        var proportions = node.EffectiveProportions();

        Assert.Equal([0.25, 0.5, 0.25], proportions);
    }

    [Fact]
    public void Proportions_FallBackToEvenSplitWhenMissing()
    {
        var node = new LayoutNode
        {
            Kind = LayoutNodeKind.Split,
            Children =
            [
                new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "a" },
                new LayoutNode { Kind = LayoutNodeKind.Panel, Id = "b" },
            ],
        };

        var proportions = node.EffectiveProportions();

        Assert.Equal([0.5, 0.5], proportions);
    }

    [Fact]
    public void FindPanel_LocatesNestedPanel()
    {
        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();
        var preset = presets.Single(p => p.Id == "debug");

        var registers = preset.FindPanel("inspector");

        Assert.NotNull(registers);
        Assert.Contains("debug.registers", registers.Tabs);
        Assert.Null(preset.FindPanel("does-not-exist"));
    }

    [Fact]
    public void UserLayerCanAddLayoutPreset()
    {
        WriteUserLayout(
            "minimal.layout.json",
            """
            {
              "id": "minimal",
              "displayName": "极简",
              "root": { "type": "document", "initialTabs": ["welcome"] }
            }
            """);

        var loader = new LayoutLoader(CreateResolver());
        var (presets, diagnostics) = loader.LoadAll();

        Assert.Contains(presets, preset => preset.Id == "minimal");
        Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Error);
    }

    [Fact]
    public void UserLayerCanOverrideBuiltInLayout()
    {
        WriteUserLayout(
            "default.layout.json",
            """
            {
              "id": "default",
              "displayName": "我改过的默认布局",
              "root": { "type": "document" }
            }
            """);

        var loader = new LayoutLoader(CreateResolver());
        var (presets, _) = loader.LoadAll();

        var preset = presets.Single(p => p.Id == "default");
        Assert.Equal("我改过的默认布局", preset.DisplayName);
    }

    [Fact]
    public void SchemaViolation_IsReportedWithPointer()
    {
        WriteUserLayout("bad.layout.json", """{ "id": "bad", "displayName": "Bad", "root": { "type": "nope" }, "extra": 1 }""");

        var loader = new LayoutLoader(CreateResolver());
        var (_, diagnostics) = loader.LoadAll();

        Assert.Contains(diagnostics, d => d.Code == "MFCFG103" && d.Location?.Value == "/extra");
    }

    [Theory]
    [InlineData("""{ "type": "split", "children": [{ "type": "document" }] }""", "至少需要 2 个子节点")]
    [InlineData("""{ "type": "split", "proportions": [0.5, 0.25], "children": [{ "type": "document" }, { "type": "document" }, { "type": "document" }] }""", "不一致")]
    [InlineData("""{ "type": "panel", "title": "no id" }""", "缺少 id")]
    public void StructurallyInvalidLayout_IsRejectedWithReadableReason(string root, string expectedFragment)
    {
        WriteUserLayout("invalid.layout.json", $$"""{ "id": "invalid", "displayName": "Invalid", "root": {{root}} }""");

        var loader = new LayoutLoader(CreateResolver());
        var (presets, diagnostics) = loader.LoadAll();

        Assert.DoesNotContain(presets, preset => preset.Id == "invalid");
        var problem = diagnostics.FirstOrDefault(d => d.Code == "MFLAYOUT003");
        Assert.NotNull(problem);
        Assert.Contains(expectedFragment, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTabIds_ProduceWarningsNotErrors()
    {
        var preset = new LayoutPreset
        {
            Id = "custom",
            Root = new LayoutNode
            {
                Kind = LayoutNodeKind.Panel,
                Id = "bottom",
                Tabs = ["output.build", "not.implemented.yet"],
            },
        };

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "output.build" };
        var diagnostics = LayoutLoader.ValidateTabIds(preset, known, "custom.layout.json");

        var single = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, single.Severity);
        Assert.Contains("not.implemented.yet", single.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJson_DoesNotPreventOtherLayoutsFromLoading()
    {
        WriteUserLayout("broken.layout.json", """{ "id": "broken" "displayName": "x" }""");

        var loader = new LayoutLoader(CreateResolver());
        var (presets, diagnostics) = loader.LoadAll();

        Assert.Contains(presets, preset => preset.Id == "default");
        Assert.Contains(diagnostics, d => d.Code == "MFCFG143");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_userDirectory))
            {
                Directory.Delete(_userDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
