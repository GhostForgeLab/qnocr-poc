# V1.32.1 Named Pipe 通信 PoC 验证

日期：2026-09-28。基线：qnocr-poc V1.31（当前工作仓库父提交 `7ac41cd749f749e0b7e307469d3303bd7cb1323b`）；只读 qnbot 源码 HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。本阶段只验证模拟 Observer 到 Agent 侧的 IPC，不接真实 qnbot。

## 范围

```text
模拟只读 Observer (NamedPipeAgentBridgeServer)
    → 同用户 Windows Named Pipe，v1 长度前缀 JSON
    → IAgentBridge / NamedPipeAgentBridgeClient
    → AgentEventEnvelope 接收与元数据日志
```

`AgentEventEnvelope` 独立于 qnbot 内部对象及 `MessageEnvelope`，包含 EventType、SessionId、Timestamp、IdentityStatus、BuyerId、Content、ProductId 和 SchemaVersion。模拟事件只允许 `IdentityStatus=Observed`；测试数据不能宣称平台身份 Verified。`ProductId` 在测试中为空。每次连接只传一条事件，帧上限 16 KiB；管道仅限当前 Windows 用户，客户端连接限时 2 秒、整个接收限时 5 秒。日志仅记录阶段、事件类型和 SessionId，不记录消息内容。

`IAgentBridge` 是 Agent 侧读取接口；以后更换 IPC 时可替换具体 Client。Server 只是模拟只读 Observer，未引用 qnbot，没有 AI、知识查询、回复或发送入口。本轮没有修改 qnbot Core、`MessageEnvelope`、既有 `IAgentExtension`、原消息/AI/发送链，也没有运行时挂载。

上一轮未激活的 V1.32 草案已清理；qnbot 仓库恢复干净。本轮新增内容仅在 qnocr-poc 仓库。

## 编译和验证

| 项目 | 结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -v q` | 成功，**0 Warning / 0 Error** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -v q` | 成功，**0 Warning / 0 Error** |
| ChangeDetectionChecks | **591/591**，其中原有 **586/586** 保持通过，新增 5 项 Agent Bridge 检查；日志 `Output/ValidationV1321/regression-final.log`（忽略的本地产物） |
| 原 qnbot 入口隔离回归 | **17/17**，`SourceFilesVerified=6`，`QnbotCoreSourceUnmodified=true`，`ForbiddenBusinessCalls=0`；日志 `Output/ValidationV1321/compatibility.log` 和 `Output/Integration/run_20260928_110203_291/`（忽略的本地产物） |

兼容宿主的旧 .NET Framework 4.8 重编译报告 1 条原 qnbot `BuyerMessageBurstCoordinator.cs` 的 CS4014 警告、0 错误。该警告不来自本轮新增模块；遵守“不修改 qnbot Core”，本轮没有改动或屏蔽它。

新增检查确认：模拟 Server 启动 → Client 连接 → v1 事件发送 → 精确反序列化接收；两侧元数据日志可核对；模拟 Observer 的 `Verified` 标签在建管道前被拒绝；超大帧在反序列化前拒绝；取消等待连接后没有发送事件。

## 安全边界与当前状态

本 PoC **AI 调用 0、商品知识查询 0、自动回复 0、SentMessages 0**。新增模块没有这些依赖或调用入口；发送仅指 Named Pipe 数据帧。测试未连接真实 qnbot，也未调用 `QnbotAgentAdapter.ProcessMessageAsync`。既有回归使用的隔离/合成 AI 检查不计作本 PoC 的 AI 调用。

V1.32.1 通信 PoC 完成。真实 qnbot 只读挂载、身份/会话核验、重连持久语义、Agent 预览均属于后续阶段，不能由本次通信测试推断已经实现。

## 下一步

在后续单独阶段评审真实 Observer 的身份来源、过滤和生命周期，再决定是否挂载；未获得可信证据时继续保留 `Observed`，不得升为 `Verified` 或接入发送。
