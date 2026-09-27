# V1.31 Agent Extension 边界与离线 PoC

## 边界

```text
qnbot .NET Framework 4.8                       qnocr-poc .NET 8 sidecar
原生千牛消息 → 只读观察桥 → v1 DTO/IPC → QnbotAgentAdapter → IAgentExtension
     └──────────────── qnbot 原消息/回复链照常运行       ├ Intent / Decision
                                                       ├ Knowledge
                                                       ├ AI Provider
                                                       ├ Reply Assistant / Preview
                                                       └ Feedback / Agent Console
```

本次新增 [IAgentExtension](Extension/IAgentExtension.cs) 和 [QnbotAgentAdapter](Extension/QnbotAgentAdapter.cs) 是 **qnocr-poc 内的离线接口 PoC**。未在 qnbot 注册事件或启动 IPC 服务；上图左侧观察桥与 IPC 是后续接入方案，不是本次实现。qnbot 桌面进程绝不直接引用 .NET 8 Agent 程序集。

| 模块 | Extension 责任 | qnbot 保留责任 |
| --- | --- | --- |
| Intent | 调用可配置的 JEV/RuleBased 分类器，记录来源和降级 | 原消息方向/安全判定不交给分类器 |
| Knowledge | 只按确证的店铺+商品引用查询已发布知识；缺失时标 Missing | 不改变原 Knowledge/Prompt/发送链 |
| AI Provider | 在明确授权与 Shadow 开关下生成建议 | 不让 Extension 接管 qnbot 原 AI 生成逻辑 |
| Reply Assistant | 保存/显示预览、人工编辑、复制、反馈 | qnbot 发送按钮与真实发送能力仍由原程序控制 |
| Feedback | 人工评价、分类、审计 | 不自动改知识、Prompt 或策略 |

## 接口与数据流

`IAgentExtension.Initialize(AgentExtensionOptions)` 默认 `ShadowOnly=true`、`SendEnabled=false`；`ProcessMessageAsync(MessageEnvelope, AgentExtensionEvidence)` 返回带关联 ID 的收据；`GetReplyPreview(correlationId)` 只准返回 `SendStatus=Shadow` 的预览。接口没有发送方法。`MessageEnvelope` 原类型和字段保持原样。

`QnbotInboundObservation` 是**宿主侧只读观察 DTO**，包含店铺、买家、方向、正文、时间、原生消息 ID、会话码、平台 targetId、身份状态与证据 ID。适配器只接受 `buyer` 文本与完整身份映射；对缺原生 ID、未验证身份、卖家回显、空正文等返回 `Held`。现有 `buyer-test` 只能在隔离测试中标 `IsolatedTest`；它永远不是可发送的真实身份。未来真实桥必须由可信平台身份链提供 `Verified` 证据，不能让调用方仅填一个字符串就升级身份。

通过门槛后，适配器构造 `Source=Qnbot.Native.ReadOnly` 的现有 Envelope v1，消息角色为 buyer。当前 ProductId 固定留空，除非后续独立商品绑定证据就绪；不借用旧会话商品。原生证据作为旁路 `AgentExtensionEvidence` 保留，不能倒灌进 `MessageEnvelope`。`CorrelationId` 使用店铺、买家与原生 ID 的组合；本 PoC 内存去重上限 10,000 条，重复返回 `Duplicate`，容量满返回 `Held`。这不是长期运行的持久去重实现。

Extension 返回的收据必须匹配关联 ID，且 `SendEnabled=false`、`SentMessages=0`；异常或结果不确定返回 `Uncertain`，不自动重放，因为下游可能已经观察到该消息。以上都是**边界检查**，不能从返回值反证下游没有副作用。正式接入时需进程隔离、禁用发送能力/凭证、审计和运行时负向验证；`AI Shadow` 与 `SentMessages=0` 是发布门槛。

## qnbot 适配层设计

未来 qnbot 侧仅增加一个只读观察桥：订阅 `QN.EvRecieveNewMessage`，使用原生 `QNChatMessage` 候选字段构造 DTO，按真实卖家/买家/会话关系核对，排除机器人发言、历史快照和回显；异步排队给 sidecar。不要调用 `BuyerMessageBurstCoordinator.Enqueue`、`QN.ProcessIncomingMessageAsync`、`SendTextWithRetryAsync`，不要替换聚合器 handler，也不要把 OCR `QnocrMessageAdapter` 反向使用。UI 只添加 Agent Console 启动入口；关闭应用时退订并取消队列。

接口 PoC 目前不引用 qnbot 类型，故可在 `qnocr-poc` 编译及隔离测试。正式 IPC 契约需要另立版本，并评审序列化、消息大小、鉴权、店铺隔离、时钟、背压、持久去重与重连语义；未完成前不能说“已接入 qnbot”。

## 风险、测试与回滚

离线检查覆盖：Envelope 保持 v1、Shadow 初始化、有效测试观察一次投递、重复拒绝、卖家/无身份/无消息 ID 拦截、异常不重放、非 Shadow 预览拒绝。现有真实入口回归需保持 17/17，主回归基线是 V1.30 的 576 项，V1.31 追加检查后以实际总数为准。并单独确认 qnbot 与 `MessageEnvelope` 无差异、真实发送次数为 0。

风险主要在原生事件早于 qnbot 安全层、跨进程失败、身份伪验证及重复消息；详见 [集成分析](QNBOT_AGENT_INTEGRATION_ANALYSIS.md)。回滚此 PoC 只需撤回 Extension/测试/文档；未来桥接试点则关闭默认关闭的 Extension 开关并移除只读订阅，原核心与发送逻辑无需回滚。
