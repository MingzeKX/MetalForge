using System.Text.Json;
using System.Text.RegularExpressions;
using MetalForge.Core.Diagnostics;

namespace MetalForge.Core.Configuration;

/// <summary>
/// 极简 JSON Schema 校验器（Draft 2020-12 的受限子集）。
///
/// 为什么自己写而不是引入第三方库：
///   - 需要的只是"配置校验 + 指出错误位置"，用到的关键字约 20 个；
///   - JsonSchema.Net 的 NuGet 包携带 OSMFEULA（对年收入 ≥ 10,000 美元的商业使用收取维护费），
///     引入即产生许可义务；NJsonSchema 体量大且偏向代码生成；
///   - 自研实现可以精确控制错误消息与 JSON Pointer，便于给出"人话报错"。
/// 详见 docs/adr/ADR-0006-json-schema-validation.md。
///
/// 支持的关键字：type / enum / const / properties / required / additionalProperties /
/// items / minItems / maxItems / uniqueItems / minimum / maximum / exclusiveMinimum /
/// exclusiveMaximum / minLength / maxLength / pattern / oneOf / anyOf / allOf / not / $ref（仅文档内）。
/// 不支持的外部 <c>$ref</c> 与 <c>$schema</c> 会被忽略（配置校验场景不需要）。
/// </summary>
public sealed class JsonSchemaValidator
{
    private const int MaxDepth = 64;

    private readonly JsonElement _rootSchema;

    public JsonSchemaValidator(JsonElement rootSchema)
    {
        _rootSchema = rootSchema;
    }

    /// <summary>校验文档，返回全部发现的问题（按遍历顺序）。空集合代表通过。</summary>
    public IReadOnlyList<Diagnostic> Validate(JsonElement document, string? sourceName)
    {
        var diagnostics = new List<Diagnostic>();
        Walk(_rootSchema, _rootSchema, document, JsonPointer.Root, sourceName, diagnostics, depth: 0);
        return diagnostics;
    }

    private static void Walk(
        JsonElement rootSchema,
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth)
    {
        if (depth > MaxDepth)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG100",
                $"配置嵌套超过 {MaxDepth} 层，疑似结构错误。",
                source,
                pointer,
                Hint: "检查是否有循环引用或异常深的嵌套对象。"));
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            var target = ResolveLocalReference(rootSchema, reference.GetString());
            if (target is { } resolved)
            {
                Walk(rootSchema, resolved, instance, pointer, source, diagnostics, depth + 1);
            }

            // 本地引用解析完成后，其余并列关键字仍按 2020-12 语义继续求值。
        }

        ApplyType(rootSchema, schema, instance, pointer, source, diagnostics, depth);

        if (instance.ValueKind == JsonValueKind.Object)
        {
            ApplyObjectKeywords(rootSchema, schema, instance, pointer, source, diagnostics, depth);
        }
        else if (instance.ValueKind == JsonValueKind.Array)
        {
            ApplyArrayKeywords(rootSchema, schema, instance, pointer, source, diagnostics, depth);
        }
        else if (instance.ValueKind == JsonValueKind.String)
        {
            ApplyStringKeywords(schema, instance, pointer, source, diagnostics);
        }
        else if (instance.ValueKind == JsonValueKind.Number)
        {
            ApplyNumberKeywords(schema, instance, pointer, source, diagnostics);
        }

        ApplyEnumAndConst(schema, instance, pointer, source, diagnostics);
        ApplyCombinators(rootSchema, schema, instance, pointer, source, diagnostics, depth);
    }

    private static void ApplyType(
        JsonElement rootSchema,
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth)
    {
        if (!schema.TryGetProperty("type", out var typeNode))
        {
            return;
        }

        var actual = DescribeKind(instance.ValueKind);
        var matches = typeNode.ValueKind switch
        {
            JsonValueKind.String => MatchesType(typeNode.GetString(), instance.ValueKind),
            JsonValueKind.Array => typeNode.EnumerateArray().Any(t => MatchesType(t.GetString(), instance.ValueKind)),
            _ => true,
        };

        if (!matches)
        {
            var expected = typeNode.ValueKind switch
            {
                JsonValueKind.String => $"'{typeNode.GetString()}'",
                JsonValueKind.Array => string.Join(" 或 ", typeNode.EnumerateArray().Select(t => $"'{t.GetString()}'")),
                _ => "未知",
            };

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG101",
                $"字段类型不正确：期望 {expected}，实际为 '{actual}'。",
                source,
                pointer,
                Hint: "检查该字段的写法；数字不要加引号，布尔值不要写成字符串。"));
        }
    }

    private static void ApplyObjectKeywords(
        JsonElement rootSchema,
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth)
    {
        var knownProperties = new HashSet<string>(StringComparer.Ordinal);

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                knownProperties.Add(property.Name);
                if (instance.TryGetProperty(property.Name, out var value))
                {
                    Walk(rootSchema, property.Value, value, pointer.Append(property.Name), source, diagnostics, depth + 1);
                }
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (name is null || instance.TryGetProperty(name, out _))
                {
                    continue;
                }

                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFCFG102",
                    $"缺少必需字段 '{name}'。",
                    source,
                    pointer,
                    Hint: "补上该字段，或删除整个对象以使用内置默认值。"));
            }
        }

        foreach (var property in instance.EnumerateObject())
        {
            if (knownProperties.Contains(property.Name))
            {
                continue;
            }

            // "$schema" 是给编辑器用的注解，按规范校验器应忽略未知关键字，
            // 而我们自己的 Schema 文件刻意不声明它（它不是数据模型的一部分）。
            if (string.Equals(property.Name, "$schema", StringComparison.Ordinal))
            {
                continue;
            }

            var additional = schema.TryGetProperty("additionalProperties", out var additionalNode) ? additionalNode : default;

            if (additional.ValueKind == JsonValueKind.False)
            {
                var known = knownProperties.Count == 0 ? "（该对象不接受任何字段）" : $"可用字段：{string.Join(", ", knownProperties.Order(StringComparer.Ordinal))}";
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFCFG103",
                    $"未知字段 '{property.Name}'。{known}",
                    source,
                    pointer.Append(property.Name),
                    Hint: "这通常是拼写错误。未知字段不会被应用，请删除或更正。"));
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                Walk(rootSchema, additional, property.Value, pointer.Append(property.Name), source, diagnostics, depth + 1);
            }

            if (schema.TryGetProperty("propertyNames", out var propertyNames) && propertyNames.ValueKind == JsonValueKind.Object)
            {
                using var nameDocument = JsonDocument.Parse(JsonSerializer.Serialize(property.Name));
                Walk(rootSchema, propertyNames, nameDocument.RootElement, pointer.Append(property.Name), source, diagnostics, depth + 1);
            }
        }
    }

    private static void ApplyArrayKeywords(
        JsonElement rootSchema,
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth)
    {
        var count = instance.GetArrayLength();

        if (schema.TryGetProperty("minItems", out var minItems) && minItems.TryGetInt32(out var minimum) && count < minimum)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG104",
                $"数组元素过少：至少 {minimum} 项，实际 {count} 项。",
                source,
                pointer));
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) && maxItems.TryGetInt32(out var maximum) && count > maximum)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG105",
                $"数组元素过多：至多 {maximum} 项，实际 {count} 项。",
                source,
                pointer));
        }

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) && uniqueItems.ValueKind == JsonValueKind.True)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                if (!seen.Add(element.GetRawText()))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "MFCFG106",
                        $"数组存在重复元素（位置 {index}）。",
                        source,
                        pointer.AppendIndex(index)));
                }

                index++;
            }
        }

        if (!schema.TryGetProperty("items", out var items))
        {
            return;
        }

        var itemIndex = 0;
        foreach (var element in instance.EnumerateArray())
        {
            if (items.ValueKind == JsonValueKind.Object)
            {
                Walk(rootSchema, items, element, pointer.AppendIndex(itemIndex), source, diagnostics, depth + 1);
            }
            else if (items.ValueKind == JsonValueKind.Array && itemIndex < items.GetArrayLength())
            {
                Walk(rootSchema, items[itemIndex], element, pointer.AppendIndex(itemIndex), source, diagnostics, depth + 1);
            }

            itemIndex++;
        }
    }

    private static void ApplyStringKeywords(
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics)
    {
        var value = instance.GetString() ?? string.Empty;

        if (schema.TryGetProperty("minLength", out var minLength) && minLength.TryGetInt32(out var minimum) && value.Length < minimum)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG107",
                $"字符串过短：至少 {minimum} 个字符，实际 {value.Length} 个。",
                source,
                pointer));
        }

        if (schema.TryGetProperty("maxLength", out var maxLength) && maxLength.TryGetInt32(out var maximum) && value.Length > maximum)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG108",
                $"字符串过长：至多 {maximum} 个字符，实际 {value.Length} 个。",
                source,
                pointer));
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            var expression = pattern.GetString()!;
            try
            {
                if (!Regex.IsMatch(value, expression, RegexOptions.CultureInvariant))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Error,
                        "MFCFG110",
                        FormatPatternMismatch(value, expression),
                        source,
                        pointer));
                }
            }
            catch (ArgumentException exception)
            {
                // Schema 自身写坏 pattern：这是开发期缺陷，不是用户配置错误。
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    "MFCFG109",
                    $"Schema 中的 pattern 不是合法正则表达式：{exception.Message}",
                    source,
                    pointer,
                    Hint: "这是内置 Schema 的缺陷，请上报；该校验本次被跳过。",
                    Exception: exception));
            }
        }
    }

    /// <summary>对过长的字符串截断展示，避免把整份配置内容刷进诊断消息。</summary>
    private static string FormatPatternMismatch(string value, string expression)
    {
        const int maxDisplay = 60;
        var display = value.Length <= maxDisplay ? value : string.Concat(value.AsSpan(0, maxDisplay), "…");
        return $"字符串 '{display}' 不符合要求的格式（pattern: {expression}）。";
    }

    private static void ApplyNumberKeywords(
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics)
    {
        var value = instance.GetDouble();

        if (schema.TryGetProperty("minimum", out var minimum) && minimum.TryGetDouble(out var min) && value < min)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "MFCFG111", $"数值过小：最小 {min}，实际 {value}。", source, pointer));
        }

        if (schema.TryGetProperty("maximum", out var maximum) && maximum.TryGetDouble(out var max) && value > max)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "MFCFG112", $"数值过大：最大 {max}，实际 {value}。", source, pointer));
        }

        if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && exclusiveMinimum.TryGetDouble(out var exclusiveMin) && value <= exclusiveMin)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "MFCFG113", $"数值必须大于 {exclusiveMin}，实际 {value}。", source, pointer));
        }

        if (schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && exclusiveMaximum.TryGetDouble(out var exclusiveMax) && value >= exclusiveMax)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "MFCFG114", $"数值必须小于 {exclusiveMax}，实际 {value}。", source, pointer));
        }
    }

    private static void ApplyEnumAndConst(
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics)
    {
        if (schema.TryGetProperty("const", out var constant) && !JsonElementEquals(constant, instance))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG115",
                $"该字段必须是固定值 {constant.GetRawText()}，实际为 {instance.GetRawText()}。",
                source,
                pointer));
        }

        if (!schema.TryGetProperty("enum", out var enumValues) || enumValues.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var candidate in enumValues.EnumerateArray())
        {
            if (JsonElementEquals(candidate, instance))
            {
                return;
            }
        }

        var allowed = string.Join(", ", enumValues.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()));
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Error,
            "MFCFG116",
            $"取值不在允许范围内：{instance.GetRawText()}。允许值：{allowed}。",
            source,
            pointer));
    }

    private static void ApplyCombinators(
        JsonElement rootSchema,
        JsonElement schema,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth)
    {
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var branch in allOf.EnumerateArray())
            {
                Walk(rootSchema, branch, instance, pointer, source, diagnostics, depth + 1);
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            RequireAtLeastOneBranch(rootSchema, anyOf, instance, pointer, source, diagnostics, depth, atLeast: 1, keyword: "anyOf");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            RequireAtLeastOneBranch(rootSchema, oneOf, instance, pointer, source, diagnostics, depth, atLeast: 1, keyword: "oneOf");
        }

        if (schema.TryGetProperty("not", out var notSchema) && notSchema.ValueKind == JsonValueKind.Object)
        {
            var scratch = new List<Diagnostic>();
            Walk(rootSchema, notSchema, instance, pointer, source, scratch, depth + 1);
            if (scratch.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "MFCFG117",
                    "该字段命中了被禁止的结构（not 分支）。",
                    source,
                    pointer));
            }
        }
    }

    private static void RequireAtLeastOneBranch(
        JsonElement rootSchema,
        JsonElement branches,
        JsonElement instance,
        JsonPointer pointer,
        string? source,
        List<Diagnostic> diagnostics,
        int depth,
        int atLeast,
        string keyword)
    {
        var matched = 0;
        var branchMessages = new List<string>();

        foreach (var branch in branches.EnumerateArray())
        {
            var scratch = new List<Diagnostic>();
            Walk(rootSchema, branch, instance, pointer, source, scratch, depth + 1);
            if (scratch.Count == 0)
            {
                matched++;
            }
            else
            {
                branchMessages.Add(scratch[0].Message);
            }
        }

        if (matched < atLeast)
        {
            var detail = branchMessages.Count > 0 ? $" 可能的原因：{string.Join(" / ", branchMessages.Distinct(StringComparer.Ordinal))}" : string.Empty;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "MFCFG118",
                $"该字段不满足 {keyword} 中的任何一种合法结构。{detail}",
                source,
                pointer));
        }
    }

    private static JsonElement? ResolveLocalReference(JsonElement rootSchema, string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference[0] != '#')
        {
            return null;
        }

        // 只支持 # 与 #/a/b/c 两种形式。
        if (reference.Length == 1)
        {
            return rootSchema;
        }

        if (reference[1] != '/')
        {
            return null;
        }

        var current = rootSchema;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
            {
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private static bool MatchesType(string? type, JsonValueKind kind) => type switch
    {
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        "string" => kind == JsonValueKind.String,
        "number" => kind == JsonValueKind.Number,
        "integer" => kind == JsonValueKind.Number && IsIntegral(kind),
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "null" => kind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsIntegral(JsonValueKind kind) => kind == JsonValueKind.Number;

    private static string DescribeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        JsonValueKind.Null => "null",
        _ => "未知",
    };

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => ArrayEquals(left, right),
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        var rightProperties = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return leftProperties.Count == rightProperties.Count
            && leftProperties.All(pair => rightProperties.TryGetValue(pair.Key, out var other) && JsonElementEquals(pair.Value, other));
    }

    private static bool ArrayEquals(JsonElement left, JsonElement right)
        => left.GetArrayLength() == right.GetArrayLength()
           && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonElementEquals(pair.First, pair.Second));
}
