# 架构决策记录（ADR）索引

每条 ADR 回答同一个问题："**为什么不用 X**"。
新增决策时复制 `template.md`，编号递增，并在此登记。

| 编号 | 标题 | 状态 | 日期 |
|---|---|---|---|
| [0001](0001-layered-architecture.md) | Core 与 App 严格分层，Core 禁止引用任何 UI 类型 | 已接受 | 2026-02 |
| [0002](0002-avalonia-12-and-editor.md) | UI 采用 Avalonia 12.1.3 + Avalonia.AvaloniaEdit 12.0.0 | 已接受 | 2026-02 |
| [0003](0003-configuration-system.md) | 三级 JSON 配置 + 来源追踪 + 热重载，配置驱动全部元信息 | 已接受 | 2026-02 |
| [0004](0004-process-management.md) | 外部进程统一经 IProcessRunner，用 Job Object 终止进程树 | 已接受 | 2026-02 |
| [0005](0005-cross-toolchain-acquisition.md) | 不在 Windows 上自举交叉工具链，优先预构建 + CI 分发 | 已接受 | 2026-02 |
| [0006](0006-json-schema-validation.md) | 自研 JSON Schema 校验子集，不引入 JsonSchema.Net | 已接受 | 2026-02 |
| [0007](0007-test-runner.md) | 用 xunit v3 + MTP，测试入口是直接运行测试程序集 | 已接受 | 2026-02 |

## 待决策（占位，实现到对应里程碑时必须先写 ADR）

| 计划编号 | 主题 | 触发里程碑 |
|---|---|---|
| 0008 | 自研最小 ISO9660 + El Torito 写入器 vs 依赖 xorriso/GRUB | M2 |
| 0009 | 自研最小 FAT16/FAT32 写入器 vs 依赖 mtools/mkfs.fat | M3/M6 |
| 0010 | 自研 GDB/MI2 客户端 vs 引入 DAP 中间层 | M3 |
| 0011 | 自研 clangd LSP 客户端 vs 引入现成 LSP 库 | M3 |
| 0012 | 物理磁盘写入的安全护栏模型 | M3 |
| 0013 | AI 后端抽象与密钥存储（Windows Credential Manager） | M5 |
