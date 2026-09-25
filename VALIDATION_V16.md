# V1.6 Real AI Shadow Pipeline 验收报告

实现提交：`a4af5f6114a4c7ed58bafacabcbd83c902467a9c`。真实测试证据仅保存在本机被 Git 忽略的 `Output`，不提交截图、聊天原文或 API 配置。

## 结果

**已达到 V1.6 技术验收：1 条真实千牛买家新消息 → OCR → MessageEnvelope → Session Context → 原 qnbot Adapter/Coordinator/AI 生成链 → 1 份 Reply Preview；AI Calls = 1，SentMessages = 0。**

用户明确授权本轮隔离测试 Session 的新消息、同会话已确认历史及现有 qnbot Prompt 发往 `https://openrouter.ai/api/v1`（`openai/gpt-5.5`）。
AI 预检显示 `AiEnabled=true`、`SendEnabled=false`，运行日志为 `AI_ENABLED=true SEND_ENABLED=false`。
其他 Session 不进入 AI；宿主未启动 Bot 应用或发送入口。

## 修改原因、文件及架构影响

V1.5 已有本地 Session/Preview 结构，但真实 Windows 执行、实际 AI 调用与响应追踪尚未验收。
本次在千牛视觉宿主的 Extension 层补齐实际调用计数、`AiResponseId`、可审计开关，
并对同屏至少两条完全一致买家标签加一个末尾数字 OCR 异常设定严格容差。
两个不同标签且无多数、或非单个末尾数字异常，仍不绑定 Session。

修改源文件：`Models/MessageSession.cs`、`Program.cs`、`Shadow/AgentPipelineTrace.cs`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、`Shadow/SessionRegistry.cs`、
`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`、`Validation/ChangeDetectionChecks/Program.cs`。
文档：`README.md`、本报告。新增业务源文件：无。删除文件：无。

MessageEnvelope schema v1、qnbot 核心源码、原 AI 生成方法、发送逻辑均未修改。
隔离宿主仍复用原 `BuyerMessageBurstCoordinator` 与 `StreamingBuyerAnswerService.GetAnswerAsync`。
原 qnbot 工作树保持干净，基线提交 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。
本阶段没有新增商品知识库、FAQ、销售话术或业务 Prompt。

## 编译与回归

| 验证 | 结果 |
|---|---|
| .NET 8 `dotnet build --no-restore -warnaserror` | 成功；0 警告、0 错误 |
| 隔离 .NET Framework 4.8 宿主 `dotnet build ... --no-restore` | 成功；0 错误；原 qnbot 源码既有 CS4014 警告 1 条 |
| 累计自动检查 | 229/229，通过；包含既有 226 项及 3 项新增校验 |
| 原 qnbot 入口回归 | 17/17，通过；原协调器仍只接收原契约消息 |
| 新增异常/边界检查 | 单个末尾数字 OCR 异常可恢复；不同买家或二选一歧义仍隔离；AI Response Id 可追踪；调用数持久化 |

自动检查日志：`Output/v16-checks.log`；原入口回归：`Output/v16-original-entry.log`。

## 真实千牛 Shadow 记录

运行目录：`Output/Shadow/shadow_20260925_203418_155_b46af18501c545f98ccea726567920db`。
在交互式 Windows 用户会话运行，首帧只建立基线，未派发历史消息；新消息在 `frame_000021` 被捕获。
共 37 帧成功、0 帧失败；目标 Session 新增消息 1、Adapter 成功 1、AI Calls 1、Reply Preview 1、SentMessages 0、发送入口调用 0、禁止业务调用 0。
期间出现另一个真实买家会话 1 次；其候选事件记录为 `SESSION_OUT_OF_SCOPE`，没有进入 AI。
OCR 对已有消息另有 11 次重复观察，均未生成第二份目标 Envelope。

这条测试消息的可审计链：

| 阶段 | 证据 |
|---|---|
| 买家输入 | `请问这款有200ml规格吗？`；截图 `frame_000021/window.png`，聊天裁剪与 OCR 同帧保存 |
| CaptureId | `shadow_20260925_203418_155_b46af18501c545f98ccea726567920db:frame_000021` |
| SessionId | `A66A2D8E64F64A1A1A8990EEC5743AE21CFBCED6D2D2DB1734C574A4E259BC12` |
| BuyerId | `buyer-test:A66A2D8E64F64A1A`，明确为隔离测试身份，不是平台账号 ID |
| ProductId | `1051087533910`，当前可见商品的 OCR 元数据；未作为已核实商品资料传入生成服务 |
| AI Request Id | `73D104EEBC9A579CF19B70468D9322BC39E23F5F593C741630CA7CB9BCFA3685`；上下文消息数 1，模型问题长度 14 |
| AI Response Id | `b3d4527971244d21a75cdd07f7dd2623`；回执 `Converted`，实际 AI Calls 1，生成耗时 4352 ms |
| Reply Preview Id | `7e4fcb2ab9e848ec826d1d2024c4dab1`；CreatedAt `2026-09-25 20:36:08 +08:00`；SendStatus `Shadow`；Confidence `null` |
| AI 回复预览 | `规格以页面可选项为准，若没有200ml选项可能暂未上架哦。` |

完整事件顺序：`MESSAGE_CAPTURED` → `SESSION_CREATED` → `SESSION_UPDATED` → `AI_REQUEST`
→ `AI_RESPONSE` → `REPLY_PREVIEW_CREATED`。请求、响应与预览 ID 在 `agent-pipeline.jsonl` 和本地 `reply-preview.json` 中互相对应。
审计文件没有 API Key 字段或匹配密钥形态的文本。

现有 SalesAgent Shadow 观察将问题归为 `ProductQuestion`，策略为需更多信息；
现有 Knowledge V2 没有找到候选条目，生成服务随后进入通用 AI 路由。
这说明回复来自已配置的现有生成链，**不能证明 200ml 规格确实存在或不存在**。
由于 V1.6 使用独立状态目录，Session Context 此次只有新消息 1 条；V1.4 历史没有跨状态目录导入或重放。
商品 ID 仍是旁路记录，不能把这次回复解释为已绑定商品知识后的结论。

本地证据路径：`Output/Shadow/.../agent-pipeline.jsonl`、`Output/Shadow/.../summary.json`、
`Output/State/V16-acceptance/outbox/D0D4626DF07473D712D34C53EEFE14BFF04404A594CBD0C65770DF01B77F0E46/reply-preview.json`、
同目录 `attempt_0001/shadow-adapter-result.json`；控制台日志 `Output/v16-live-shadow.log`。

## 当前限制与下一步

本轮证明 1 条真实消息端到端生成预览，不等于长期稳定性或回复事实准确性已通过。
另一个买家会话被正确隔离，显示 Session 范围限制生效；OCR 仍可能产生无法绑定身份的事件，需保留证据并审查。
后续仅评估人工确认发送闭环、真实身份绑定、商品上下文关联与长时间运行稳定性；不进入知识库建设。

回滚：停止本项目 Shadow，回滚上方实现提交并重新构建；保留被 Git 忽略的 `Output/State/V16-acceptance` 与运行证据供审计。
