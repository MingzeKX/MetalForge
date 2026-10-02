# ADR-0004：外部进程统一经 IProcessRunner，用 Job Object 终止进程树

- **状态**：已接受
- **日期**：2026-02
- **相关**：DESIGN.md §12 进程与外部工具纪律、§八 质量底线

## 背景

MetalForge 的本质是"编排外部工具"：GCC/Clang/NASM/CMake、QEMU、GDB、OpenOCD、clangd、
esptool。这类系统的崩溃与卡死几乎都出自同一批原因：
stdout/stderr 未异步读取导致管道填满死锁、路径含空格被拆开、编码不对导致乱码、
取消操作没能真正终止子进程、长驻进程在窗口关闭后残留。

项目要求：所有外部进程调用必须支持超时与取消；警惕进程调用死锁、路径转义、
UTF-8 编码、句柄泄漏、异常吞没。

## 决策

### 1. 唯一入口

只有 `MetalForge.Core.Processes.ProcessRunner` 允许触碰 `System.Diagnostics.Process`；
其余代码一律通过 `IProcessRunner` / `IProcessHandle` 接口。
配套的反射测试（M2 落地）会扫描 Core 与 App 中 `System.Diagnostics.Process` 的直接使用并失败。

### 2. 逐条把"质量底线"变成实现规则

| 风险 | 规则 |
|---|---|
| 死锁 | 必须通过 `OutputDataReceived`/`ErrorDataReceived` 异步订阅读取；禁止在未排空输出的情况下阻塞等待 |
| 路径空格/转义 | 一律使用 `ProcessStartInfo.ArgumentList`，禁止手工拼接并加引号 |
| 编码 | 显式设置 `StandardOutputEncoding`/`StandardErrorEncoding` 为 UTF-8（无 BOM）；串口按字节流 + 显式解码器 |
| 超时 | `ProcessRequest.Timeout` 到期 → 走统一终止流程并标记 `TimedOut`，不依赖单点 `WaitForExit(ms)` |
| 取消 | 分级：先请求优雅退出（可用时发信号）→ 宽限期 → 强制终止整棵进程树 |
| 进程树残留 | Windows 上用 **Job Object**（`CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）持有子进程；句柄关闭时内核负责清理 |
| 句柄泄漏 | 所有 `Process`/`JobObject` 句柄在 `DisposeAsync` 中释放；长驻会话在窗口关闭、项目切换时强制回收 |
| 异常吞没 | 禁止空 `catch`；捕获后必须转为结构化 `Diagnostic`（携带原异常对象）或重抛 |
| 输出爆炸 | 环形缓冲 + 批量 UI 刷新（≥16ms 合并），保证"刷新延迟 <100ms"的同时不因 10k 行/秒 输出拖死界面 |

### 3. 为什么用 Job Object 而不是只靠 Kill

.NET 的 `Process.Kill(entireProcessTree: true)` 有已知缺口：当中间层子进程自己也在一个设置了
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 的 Job 里时，进程树遍历可能漏掉孙进程
（dotnet/runtime#107992）；社区也在推动改用 `TerminateJobObject`（dotnet/runtime#126273）。
QEMU 会派生辅助进程，残留的 QEMU 会让用户机器持续占用 CPU 与端口，属于"必须消除"的问题类别，
因此采用内核级兜底而不是依赖遍历。

### 4. 只在 Windows 上走 Job Object

Job Object 是 Win32 概念。跨平台路径（未来）退化为"遍历 + 强杀"，并把平台差异封装在
`ProcessRunner` 内部，接口不变。

## 后果

**正面**
- 死锁、乱码、残留进程这些"偶发且难复现"的问题从"靠运气"变成"结构上不可能"。
- 接口可测：用真实的短命进程（如 `cmd /c`、`dotnet`）测超时与取消，测试带 5 秒上限，不会挂 CI。
- 后续 QEMU/GDB/clangd 的长驻会话直接复用同一套生命周期管理。

**负面 / 成本**
- 交互式会话（GDB MI、clangd LSP）需要暴露 `StandardInput` 写入与逐行解析，
  接口比"跑完拿结果"复杂。这部分复杂度无法避免——这些工具本来就是长驻协议会话。

## 备选方案

| 方案 | 否决原因 |
|---|---|
| 直接用 `System.Diagnostics.Process` | 每个调用点都要重复处理死锁/编码/取消，必然出现遗漏 |
| 引入第三方进程库（如 Medallion.Shell、CliWrap） | CliWrap 质量不错，但本项目需要 Job Object、逐行流式回调与结构化诊断，仍要写一层封装；且引入 >5MB 依赖需先确认。M2 可重新评估 |
| 只用 `Kill(entireProcessTree: true)` | 见上文 §3 的已知缺口；对"QEMU 残留"这类问题不够 |
| 用 Windows 服务或驱动做进程托管 | 严重过度设计，且需要提权 |

## 待办

- M2 落地时补充：优雅退出信号在 Windows 上的可用手段（`GenerateConsoleCtrlEvent`）及其限制。
- M2 落地时补充：`IProcessHandle.StandardInput` 的背压处理（写入过快导致子进程缓冲区填满）。
