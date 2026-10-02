# ADR-0007：测试入口是"直接运行测试程序集"，而不是 `dotnet test`

- **状态**：已接受
- **日期**：2026-02
- **相关**：DEPENDENCIES.md §4、scripts/Test.ps1

## 背景

.NET 10 SDK 移除了 VSTest 路径对 Microsoft.Testing.Platform（MTP）项目的支持。
使用 xunit v3 的 MTP 包时，`dotnet test` 直接报错：

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.
```

官方给出的接入方式是：在 `global.json` 里声明

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

## 实测结果（按顺序试过，全部记录）

| 尝试 | 配置 | 结果 |
|---|---|---|
| 1 | `xunit.v3` + `Microsoft.NET.Test.Sdk`，`dotnet test` | ❌ 报 "VSTest target is no longer supported" |
| 2 | 加 `dotnet.config`（`[dotnet.test.runner] name = "Microsoft.Testing.Platform"`） | ❌ 未被识别，同样报错 |
| 3 | 加 `global.json` 的 `test.runner` | ⚠️ 不再报错，但**运行了零个测试（退出码 5）** |
| 4 | 去掉 `Microsoft.NET.Test.Sdk`，改用 `xunit.v3.mtp-v2` + `TestingPlatformDotnetTestSupport` | ⚠️ 仍然"运行了零个测试（退出码 5）" |
| 5 | **用 `dotnet new xunit3` 官方模板生成的工程**（`xunit.v3.mtp-v2` 4.0.1）跑 `dotnet test` | ⚠️ **同样"运行了零个测试（退出码 5）"** |
| 6 | 直接执行测试程序集（`dotnet <test>.dll`） | ✅ 正确发现并运行全部测试 |

第 5 步是关键证据：**官方模板在此 SDK 组合下也无法通过 `dotnet test` 跑起来**，
因此这不是本项目配置写错，而是工具链组合的已知缺口。

## 决策

1. 仓库统一入口为 **`scripts/Test.ps1`**：先构建，再直接执行测试程序集
   （`dotnet build/.../MetalForge.Core.Tests.dll`），转发 DTO 参数。
2. 测试工程仍然使用 MTP 标准的包与属性（`xunit.v3.mtp-v2`、
   `TestingPlatformDotnetTestSupport=true`、`global.json` 的 `test.runner`），
   以便上游修复后 `dotnet test` 可以直接工作，无需改工程。
3. `global.json` 固定 SDK `10.0.401` + `rollForward: latestFeature`，
   让行为在已知可用的 SDK 版本上稳定。
4. 在 `DEPENDENCIES.md` 与 `docs/manual-verification/M0.md` 记录完整复现步骤。

## 后果

**正面**
- 测试确实在跑：当前 73 个测试，0.5 秒内完成，退出码可信（0 = 全部通过，1 = 有失败）。
- 不依赖 VSTest/`Microsoft.NET.Test.Sdk`，少一层适配。

**负面**
- IDE 里的"测试资源管理器"可能无法直接发现测试。缓解：测试程序集本身是可执行文件，
  也可作为普通启动项调试。
- 上游修复后需要重新验证并可能调整脚本。
- 需要自己保证"测试确实被发现"：如果过滤器写错导致零个测试，MTP 会给出非零退出码，
  脚本会如实报告失败，不会把"没跑测试"误报为成功。

## 重新评估的触发条件

xunit.v3 或 .NET SDK 发布新版本，且用官方模板 + `dotnet test` 能正确发现测试时，
回到本 ADR 更新决策与脚本。
