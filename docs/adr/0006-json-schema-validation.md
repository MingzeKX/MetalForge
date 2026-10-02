# ADR-0006：自研 JSON Schema 校验子集，不引入 JsonSchema.Net

- **状态**：已接受
- **日期**：2026-02
- **相关**：DEPENDENCIES.md §2.1、DESIGN.md §11

## 背景

配置系统需要：校验用户手写的 JSON、指出**具体哪个字段**出错、给出可读的修复建议
（因为项目明确要求"配置错误时回退到默认值并提示用户"，且非程序员要能改这些文件）。

候选方案：

1. `JsonSchema.Net` 9.4.0（json-everything 项目）——功能完整，Draft 2020-12 支持最好；
2. `NJsonSchema` 11.6.1——偏重从 Schema 生成 C# 类型；
3. 自研受限子集。

## 关键事实（实测，不是听说）

下载 `JsonSchema.Net.9.4.0.nupkg` 并解包检查，包内许可证文件是 **`OSMFEULA.txt`**：

> 该包本身以 MIT 许可发布，但 Binary Release 附带 *Open Source Maintenance Fee Agreement*：
> 对**年总收入 ≥ 10,000 美元**且将软件用于营利活动的用户，按月收取维护费。

这不是"不能用"，而是"引入即产生许可义务"。对一个尚在起步、可能被商业用户使用的项目，
把这种条款引入核心依赖链需要在 DEPENDENCIES.md 里长期跟踪，并在每次升级时复核。

同时评估需求侧：本项目真正需要的关键字约 20 个
（`type`/`enum`/`const`/`properties`/`required`/`additionalProperties`/`items`/`minItems`/
`maxItems`/`uniqueItems`/`minimum`/`maximum`/`exclusiveMinimum`/`exclusiveMaximum`/
`minLength`/`maxLength`/`pattern`/`oneOf`/`anyOf`/`allOf`/`not`/文档内 `$ref`）；
外部 `$ref`、`if/then/else`、`unevaluatedProperties`、`format` 断言等一概用不到。
且自研实现能精确控制错误消息与 JSON Pointer，这恰恰是"人话报错"的落点。

## 决策

**自研 `JsonSchemaValidator`**（`src/MetalForge.Core/Configuration/JsonSchemaValidator.cs`，约 500 行），
实现上述 Draft 2020-12 子集：

- 未知关键字忽略，**但显式跳过 `$schema`**（它是编辑器注解，不属于数据模型）；
- `additionalProperties: false` 时报告未知字段，并在消息里列出可用字段（拼写错误的最常见场景）；
- 错误消息带中文说明 + JSON Pointer 定位 + `Hint` 修复建议；
- Schema 自身写坏（如非法正则）报为 **Warning** 并标注"这是内置 Schema 的缺陷"，与用户配置错误区分开；
- 递归深度上限 64，防止畸形配置导致栈溢出。

## 后果

**正面**
- 零新增第三方依赖，无许可义务。
- 错误消息完全可控：`branding/app.json/taglin: [Error] MFCFG103: 未知字段 'taglin'。可用字段：...`
- 校验器本身可测：20 个单元测试覆盖各类关键字与错误路径。

**负面 / 风险**
- 不支持完整 Draft 2020-12。若将来需要 `if/then/else`、`$dynamicRef`、`format` 断言，
  需要自己补实现或重新评估引入第三方库。
  缓解：`JsonSchemaValidator` 是独立组件，替换成本被限制在一个类内；
  ADR 明确记录"何时应重新评估"：当需要的未支持关键字超过 3 个，或出现外部 `$ref` 需求时。
- 需要自己维护正则与数值边界等细节的正确性。缓解：单元测试覆盖，且校验失败一律降级为"提示 + 回退默认值"，
  不会因为校验器 bug 导致启动失败。

## 重新评估的触发条件

满足任一条件即回到本 ADR 重新决策：

1. 需要 3 个以上当前未实现的关键字；
2. 需要跨文件 `$ref`（例如项目配置引用目标架构 Schema）；
3. 需要把 Schema 用于代码生成；
4. `JsonSchema.Net` 移除或放宽 OSMFEULA 条款，且团队愿意承担该依赖。
