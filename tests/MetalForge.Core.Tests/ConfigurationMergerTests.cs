using System.Text.Json.Nodes;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Tests;

public sealed class ConfigurationMergerTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static ResolvedConfiguration Merge(params (ConfigLayerKind Kind, string Json)[] layers)
        => ConfigurationMerger.Merge(
            [.. layers.Select((layer, index) => (
                new ConfigLayer(layer.Kind, layer.Kind.ToString(), null),
                (JsonNode?)Parse(layer.Json)))]);

    [Fact]
    public void Merge_OverlaysNestedObjectsFieldByField()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "metrics": { "cornerRadius": 6, "spacingUnit": 4 } }"""),
            (ConfigLayerKind.User, """{ "metrics": { "spacingUnit": 8 } }"""));

        Assert.Equal(6, result.Root!["metrics"]!["cornerRadius"]!.GetValue<int>());
        Assert.Equal(8, result.Root["metrics"]!["spacingUnit"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_ReplacesArraysWholesale()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "items": [ "a", "b", "c" ] }"""),
            (ConfigLayerKind.User, """{ "items": [ "z" ] }"""));

        var items = result.Root!["items"]!.AsArray();
        Assert.Single(items);
        Assert.Equal("z", items[0]!.GetValue<string>());
    }

    [Fact]
    public void Merge_RemovesKeyWhenOverlayIsNull()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "keep": 1, "drop": 2 }"""),
            (ConfigLayerKind.User, """{ "drop": null }"""));

        Assert.True(result.Root!.AsObject().ContainsKey("keep"));
        Assert.False(result.Root.AsObject().ContainsKey("drop"));
    }

    [Fact]
    public void Merge_TracksOriginOfEveryLeaf()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "palette": { "accent": "#7AA2C8", "border": "#2A2F36" } }"""),
            (ConfigLayerKind.Project, """{ "palette": { "accent": "#FF0000" } }"""));

        Assert.Equal(ConfigLayerKind.Project, result.OriginOf("/palette/accent"));
        Assert.Equal(ConfigLayerKind.BuiltIn, result.OriginOf("/palette/border"));
        Assert.Null(result.OriginOf("/palette/missing"));
    }

    [Fact]
    public void Merge_DropsOriginOfRemovedKey()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "gone": 1 }"""),
            (ConfigLayerKind.User, """{ "gone": null }"""));

        Assert.Null(result.OriginOf("/gone"));
    }

    [Fact]
    public void Merge_IgnoresMissingLayers()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "value": 1 }"""),
            (ConfigLayerKind.Project, """{ "other": 2 }"""));

        Assert.Equal(1, result.Root!["value"]!.GetValue<int>());
        Assert.Equal(2, result.Root["other"]!.GetValue<int>());
        Assert.Equal(ConfigLayerKind.Project, result.OriginOf("/other"));
    }

    [Fact]
    public void Merge_HandlesEmptyLayerList()
    {
        var result = ConfigurationMerger.Merge([]);

        Assert.Null(result.Root);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Merge_DoesNotShareNodesWithInputLayers()
    {
        var builtIn = Parse("""{ "nested": { "value": 1 } }""");
        var user = Parse("""{ "nested": { "value": 2 } }""");

        var result = ConfigurationMerger.Merge(
        [
            (new ConfigLayer(ConfigLayerKind.BuiltIn, "built-in", null), builtIn),
            (new ConfigLayer(ConfigLayerKind.User, "user", null), user),
        ]);

        // 修改输入层不得影响合并结果（DeepClone 的意义）
        user["nested"]!["value"] = 99;

        Assert.Equal(2, result.Root!["nested"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_OriginLookupUsesJsonPointerEscaping()
    {
        var result = Merge(
            (ConfigLayerKind.BuiltIn, """{ "a/b": { "c~d": 1 } }"""));

        // RFC 6901：'/' 转义为 ~1，'~' 转义为 ~0
        Assert.Equal(ConfigLayerKind.BuiltIn, result.OriginOf("/a~1b/c~0d"));
    }

    [Fact]
    public void ResolvedConfiguration_HasErrorsReflectsSeverity()
    {
        var configuration = Merge((ConfigLayerKind.BuiltIn, """{ "a": 1 }"""));
        Assert.False(configuration.HasErrors);

        var withError = new ResolvedConfiguration(
            configuration.Root,
            configuration.Origins,
            [new Diagnostic(DiagnosticSeverity.Error, "TEST001", "boom")]);

        Assert.True(withError.HasErrors);
    }
}
