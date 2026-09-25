# V1.5 Agent Pipeline Shadow 验证报告

实现提交：`a87ae25a490a547f2a9c510217440623e671dfbc`。本文件随独立验证报告提交保存。

## 状态与范围

本轮实现了外层 Session Context、隔离宿主的上下文输入、Reply Preview 与可关联的生命周期审计。
用户明确选择「暂不进行外部 AI 调用」，因此本轮没有运行真实 AI Shadow，也没有要求买家发送 V1.5 的 10 条问题。
**V1.5 真实 10/10 预览、AI Calls > 0 的验收标准未完成；不能把模拟回复或历史 OCR 记录当成通过。**

## 修改原因、架构影响

V1.4 已能稳定取得真实买家消息，却没有统一的会话输入快照和捕获→AI→预览的追踪链。
V1.5 在 qnocr-poc 的 Adapter/Extension 层增加 `SessionContextView`、`ReplyPreview` 和 `agent-pipeline.jsonl`。
MessageEnvelope schema v1、qnbot 核心源文件、既有 AI 生成方法及发送逻辑均未修改；qnbot 工作树仍为干净的 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

一次本地上下文检查发现，把历史消息作为新事件批量送入原协调器，会触发其 generation 生命周期，导致较早消息被取代。
现只把**当前一条** MessageEnvelope 送入原 Adapter/Coordinator；同会话、最多最近 10 条确认消息在协调器回调处组成外层模型问题文本。
原 `StreamingBuyerAnswerService.GetAnswerAsync` 调用接口保持不变，未新增商品知识、FAQ、销售话术或业务 Prompt。
现有隔离测试身份仍是测试映射，不能解释为真实千牛平台 BuyerId。

## 文件

新增：`Integration/AgentContextWire.cs`、`Shadow/AgentPipelineTrace.cs`、`Shadow/AgentShadowClient.cs`、
`Shadow/SessionContextBuilder.cs`、`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`、
`Validation/Export-AgentContextV15.ps1`、本报告。

修改：`Models/MessageFingerprint.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、`Shadow/ShadowAdapterClient.cs`、
`Validation/ChangeDetectionChecks/Program.cs`、`Validation/QnbotCompatibility/AiShadowRuntime.cs`、
`Validation/QnbotCompatibility/Program.cs`。删除：无。

## 本地验证

| 项目 | 结果 |
|---|---|
| .NET 8 主项目 `dotnet build --no-restore -warnaserror` | 成功，0 警告、0 错误 |
| .NET Framework 4.8 隔离宿主 `dotnet build ... --no-restore` | 成功，0 错误；原样 qnbot 源码第 744 行保留既有 CS4014 警告 1 条 |
| 累计检查 `dotnet run --project Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore` | 226/226，通过；覆盖原 213 项及 13 项新检查 |
| 原入口离线回归 | 17/17；实际原协调器 1 次回调，发送入口调用 0 |
| V1.4 真实测试会话的只读上下文导出 | 12 条既存消息；模型输入视图最近 10 条；当前 ProductId 为 `1051087533910` |
| 隔离宿主本地上下文检查 | 原协调器输入当前 1 条、回调 1 次；上下文 12 条、模型输入视图 10 条；PromptLength 364；AI Calls 0；SentMessages 0 |
| 合成故障检查 | `AI_ERROR`、`EMPTY_REPLY`、`SESSION_MISSING` 分开记录；空结果不生成预览；失败保留状态 |
| API Key 审计 | 新增生命周期事件不含 Key/敏感配置字段；异常事件只保留异常类型 |
| 当前执行环境单帧 Shadow 冒烟（AI OFF / Send OFF） | 启动及恢复日志正常；窗口枚举返回 `Win32Exception: EnumWindows failed`，成功帧 0/1；未取得新消息 |

验证日志（均为本地忽略文件）：`Output/v15-checks.log`、`Output/v15-original-entry.log`、
`Output/v15-context-check.log`、`Output/v15-live-baseline.log`；真实历史上下文导出在 `Output/ValidationV15/real-session-context.json`。
该导出含真实买家文本，仅保存在本机 Output，未提交。

下列 6 事件顺序由**合成回复**检查覆盖：`MESSAGE_CAPTURED` → `SESSION_CREATED` → `SESSION_UPDATED`
→ `AI_REQUEST` → `AI_RESPONSE` → `REPLY_PREVIEW_CREATED`。同一事件行携带 CaptureId、SessionId、AiRequestId；
预览另携带 ReplyPreviewId。合成回复不能证明第三方模型调用或真实回复质量。

## 真实验收缺口与安全边界

本轮 V1.5 新发真实买家消息：0；真实 Pipeline 入链：0/10；真实 Reply Preview：0/10；
外部 AI Calls：0；实际发送：0。10 条真实买家消息和 10 个真实回复预览**未验收**。
`--ai-shadow` 在本轮未运行；未向 OpenRouter 或其他端点发送测试会话/Prompt。
此前 V1.4 的真实消息仅作为本地上下文输入结构检查，不作为 V1.5 实时采集或 AI 验收计数。
当前工具执行身份无法枚举 Windows 桌面窗口；这项单帧失败不能推断千牛本身或原有窗口捕获已失效。

未来如获得针对该轮测试数据的明确授权，可在**独立状态目录**、明确的测试 `--ai-session`、AI ON / Send OFF 条件下再做 10 条真实测试，
核对每条 CaptureId→SessionId→AiRequestId→ReplyPreviewId 和最终 `SentMessages=0`。
原输出、V1.4 持久去重状态及用户聊天内容应保留审计，不因重测而删除或改写。

风险：OCR/身份仍有识别边界；历史会话最多最近 10 条进入模型问题，过长内容可能增加成本和延迟；
本轮无法证明真实 AI 输入质量、生成质量或外部故障恢复。
回滚：停止本项目 Shadow，回滚 V1.5 实现提交并重新构建；保留 Output/State 供审计。

下一步限于：真实身份绑定、商品上下文关联、人工确认发送闭环设计和长时间稳定性评估；
不在本阶段建设知识库。
