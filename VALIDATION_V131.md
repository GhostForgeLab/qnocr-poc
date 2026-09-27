# V1.31 Agent Extension 集成设计验证

日期：2026-09-27。工作仓库：`D:/CodexWork/qnocr-poc`；只读 qnbot 源码基线：`D:/CodexWork/qnagent/qnbot`，HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

## 范围与结果

本轮只实现 `IAgentExtension` 和 `QnbotAgentAdapter` 的离线边界 PoC，以及两份集成设计文档。未把 Extension 挂载到运行中的 qnbot，也未建立 IPC 或启动 Agent AI。qnbot 源码、现有 `MessageEnvelope`、原消息入口和发送链没有改动。PoC Fake Extension 不调用 AI，不具备发送方法；本轮 `AI Calls=0`、`SentMessages=0`。

| 检查 | 实际结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -v q` | 成功，0 警告、0 错误 |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -v q` | 成功，0 警告、0 错误 |
| `dotnet run --project Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-build` | **586/586** 通过；V1.30 的 576 项基线保持通过，追加 10 项 Extension 边界检查。日志：`Output/ValidationV131/regression-final.log`（本地忽略产物） |
| 原 qnbot 入口隔离回归 | `QnbotCompatibility.exe` + 原 fixture：**17/17**；原样 qnbot 6 个源码文件校验通过，`QnbotCoreSourceUnmodified=true`，`ForbiddenBusinessCalls=0`。日志：`Output/ValidationV131/original-entry/`（本地忽略产物） |

新增边界检查涵盖：显式 Shadow/Send OFF 初始化、现有 Envelope v1 转换、原生旁路证据、同一原生 ID 去重、卖家回显拦截、未验证身份拦截、缺消息 ID 拦截、无平台证据的 `Verified` 标签拦截、异常不自动重放、非 Shadow 预览拒绝及未见消息预览拒绝。

## 未证明的事项

本次**没有**运行 qnbot 桌面插件，没有从真实 qnbot 消息事件读取身份，没有核实 `targetId`/`ccode` 与 OCR Session 一一对应，也没有验证进程间协议、持久去重或跨重启恢复。适配器对 `Verified` 的非空字段检查不是平台身份认证；本地 DTO 调用方若伪造字段，PoC 无法辨别。真实集成与发送仍被阻止，后续必须有可信来源、跨进程隔离与运行时审计。

设计、风险与回滚见 [集成点分析](QNBOT_AGENT_INTEGRATION_ANALYSIS.md) 和 [Extension 设计](AGENT_EXTENSION_DESIGN.md)。
