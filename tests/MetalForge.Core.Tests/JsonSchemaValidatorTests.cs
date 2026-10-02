using System.Text.Json;
using MetalForge.Core.Configuration;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Tests;

public sealed class JsonSchemaValidatorTests
{
    private static IReadOnlyList<Diagnostic> Validate(string schemaJson, string documentJson)
    {
        using var schemaDocument = JsonDocument.Parse(schemaJson);
        using var instanceDocument = JsonDocument.Parse(documentJson);

        return new JsonSchemaValidator(schemaDocument.RootElement).Validate(instanceDocument.RootElement, "test.json");
    }

    private static void AssertHasCode(IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        Assert.True(
            diagnostics.Any(d => d.Code == code),
            $"期望诊断 {code}，实际得到：{string.Join(" | ", diagnostics.Select(d => d.Code + ":" + d.Message))}");
    }

    [Fact]
    public void ValidDocument_ProducesNoDiagnostics()
    {
        var diagnostics = Validate(
            """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }""",
            """{ "name": "MetalForge" }""");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MissingRequiredField_IsReported()
    {
        var diagnostics = Validate(
            """{ "type": "object", "required": ["name"] }""",
            """{ "other": 1 }""");

        AssertHasCode(diagnostics, "MFCFG102");
        Assert.Contains("name", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownField_IsReportedWhenAdditionalPropertiesIsFalse()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "accent": { "type": "string" } }, "additionalProperties": false }""",
            """{ "accent": "#fff", "acccent": "#000" }""");

        AssertHasCode(diagnostics, "MFCFG103");
        Assert.Contains("acccent", diagnostics[0].Message, StringComparison.Ordinal);
        // 提示里应列出可用字段，便于初学者自查拼写
        Assert.Contains("accent", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongType_IsReportedWithActualKind()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "count": { "type": "integer" } } }""",
            """{ "count": "3" }""");

        AssertHasCode(diagnostics, "MFCFG101");
        Assert.Contains("字符串", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumViolation_ListsAllowedValues()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "variant": { "enum": ["dark", "light"] } } }""",
            """{ "variant": "neon" }""");

        AssertHasCode(diagnostics, "MFCFG116");
        Assert.Contains("dark", diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("light", diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PatternViolation_IsReported()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "color": { "type": "string", "pattern": "^#(?:[0-9a-fA-F]{6})$" } } }""",
            """{ "color": "#12" }""");

        AssertHasCode(diagnostics, "MFCFG110");
    }

    [Fact]
    public void InvalidPatternInSchema_IsReportedAsSchemaDefectNotUserError()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "color": { "type": "string", "pattern": "([unclosed" } } }""",
            """{ "color": "anything" }""");

        AssertHasCode(diagnostics, "MFCFG109");
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public void ArrayBounds_AreEnforced()
    {
        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "items": { "type": "array", "minItems": 2 } } }""", """{ "items": [1] }"""),
            "MFCFG104");

        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "items": { "type": "array", "maxItems": 1 } } }""", """{ "items": [1, 2] }"""),
            "MFCFG105");

        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "items": { "type": "array", "uniqueItems": true } } }""", """{ "items": ["a", "a"] }"""),
            "MFCFG106");
    }

    [Fact]
    public void ArrayItems_AreValidatedWithIndexedPointers()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "links": { "type": "array", "items": { "type": "object", "required": ["label"] } } } }""",
            """{ "links": [ { "label": "ok" }, { "uri": "x" } ] }""");

        AssertHasCode(diagnostics, "MFCFG102");
        Assert.Equal("/links/1", diagnostics[0].Location?.Value);
    }

    [Fact]
    public void LocalRef_IsResolved()
    {
        var diagnostics = Validate(
            """
            {
              "type": "object",
              "properties": { "node": { "$ref": "#/$defs/node" } },
              "$defs": {
                "node": { "type": "object", "required": ["kind"], "properties": { "kind": { "enum": ["split", "panel"] } } }
              }
            }
            """,
            """{ "node": { "kind": "banana" } }""");

        AssertHasCode(diagnostics, "MFCFG116");
    }

    [Fact]
    public void OneOf_RequiresAMatchingBranch()
    {
        var schema =
            """
            {
              "type": "object",
              "properties": { "node": { "oneOf": [ { "required": ["a"] }, { "required": ["b"] } ] } }
            }
            """;

        Assert.Empty(Validate(schema, """{ "node": { "a": 1 } }"""));
        AssertHasCode(Validate(schema, """{ "node": { "c": 1 } }"""), "MFCFG118");
    }

    [Fact]
    public void NumberBounds_AreEnforced()
    {
        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "v": { "type": "number", "minimum": 1 } } }""", """{ "v": 0 }"""),
            "MFCFG111");

        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "v": { "type": "number", "maximum": 1 } } }""", """{ "v": 5 }"""),
            "MFCFG112");

        AssertHasCode(
            Validate("""{ "type": "object", "properties": { "v": { "type": "number", "exclusiveMinimum": 0 } } }""", """{ "v": 0 }"""),
            "MFCFG113");
    }

    [Fact]
    public void Const_IsEnforced()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "type": { "const": "split" } } }""",
            """{ "type": "panel" }""");

        AssertHasCode(diagnostics, "MFCFG115");
    }

    [Fact]
    public void Diagnostic_CarriesJsonPointerForNavigation()
    {
        var diagnostics = Validate(
            """{ "type": "object", "properties": { "metrics": { "type": "object", "properties": { "cornerRadius": { "type": "number" } } } } }""",
            """{ "metrics": { "cornerRadius": "six" } }""");

        Assert.Single(diagnostics);
        Assert.Equal("/metrics/cornerRadius", diagnostics[0].Location?.Value);
        Assert.Equal("test.json", diagnostics[0].Source);
    }

    [Fact]
    public void RootTypeMismatch_IsReported()
    {
        var diagnostics = Validate("""{ "type": "object" }""", """[1, 2, 3]""");

        AssertHasCode(diagnostics, "MFCFG101");
    }
}
