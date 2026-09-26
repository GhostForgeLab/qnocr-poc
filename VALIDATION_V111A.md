# V1.11-A 真实买家身份绑定前置验证报告

实现提交：`cd17f48ef37ec6988e471d1cf923cef90492c848`。本报告随独立验证提交保存。

## 结论

本轮完成身份状态模型、Session/Context旁路绑定及发送前置拒绝规则；**当前真实千牛买家尚未达到Verified**。
现有真实OCR链使用`buyer-test:*`隔离映射，用户确认暂时没有可独立核对的SellerId、平台BuyerId、ConversationId来源；检查时也没有运行中的qnbot/Bot实例。
因此真实身份仍为Mapped/Test，真实发送数为0。合成可信来源的正向测试只证明机制可用，不是实际买家身份验收。

## 修改原因与当前身份链

OCR聊天头提供买家显示名，不提供可信平台买家主键；UIA在本项目只读取几何，不读取消息或买家身份。
`SessionRegistry`将显示名与显式配置映射组成SessionId，当前测试映射产生`buyer-test:<SessionId前缀>`。
`OcrMessageSource`把测试BuyerId交给原MessageEnvelope，独立Adapter再生成qnbot测试输入。
详细来源与信任边界见 [docs/BUYER_IDENTITY_CHAIN_V111A.md](docs/BUYER_IDENTITY_CHAIN_V111A.md)。

新增：`Models/BuyerIdentity.cs`、`Shadow/SessionIdentityBindingService.cs`、
`Validation/ChangeDetectionChecks/IdentityChecks.cs`、身份链分析文档、本报告。
修改：`Integration/AgentContextWire.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/AgentPipelineTrace.cs`、`Shadow/IReplySendAdapter.cs`、`Shadow/QnocrShadowRunner.cs`、
`Shadow/SessionContextBuilder.cs`、`Shadow/SessionRegistry.cs`、
`Validation/ChangeDetectionChecks/ConfirmationChecks.cs`、`Program.cs`。删除：无。

MessageEnvelope结构、qnbot核心、AI生成链和发送流程均未修改。Session仅增加可选`BuyerIdentity`旁路字段，旧记录仍可读；
Context wire也可携带它，但现有AI问题文本不使用身份字段。qnbot工作树保持干净。

## 身份状态与安全门

`BuyerIdentity`记录BuyerId、Source、Confidence、CapturedAt、SessionId和Unknown/Observed/Mapped/Verified状态。
OCR-only为Observed；`buyer-test:*`为Mapped、`IsTest=true`，Confidence为空，不伪造概率。
非测试操作者映射仍为Mapped，不能自行提升Verified。

`SessionIdentityBindingService`默认无可信平台来源；只有注入的独立qnbot运行时证据同时匹配SellerId、平台BuyerId、ConversationId、
SessionId和OCR显示名，且证据新鲜，才会持久化Verified。
`CanSendToBuyer`还要求Verified、非测试、精确Session/Buyer匹配、完整证据及5分钟内的新鲜核验；
新的OCR观察或再次核验失败会撤销旧Verified。`QnbotSendAdapter`在调用发送端口前执行此检查。
没有CLI命令能把OCR昵称或测试映射手动标为Verified。

## 验证结果

| 项目 | 结果 |
|---|---|
| .NET 8主项目与验证项目 | Build Success；0警告、0错误 |
| .NET Framework 4.8隔离宿主 | Build Success；全量构建仅保留原上游CS4014警告 |
| 累计检查 | 276/276，通过，高于V1.10的264项 |
| qnbot原入口 | 17/17，通过；禁止业务调用0 |
| buyer-test | Mapped/Test，不能提升Verified；假称可信证明时甚至不调用证明源 |
| OCR-only | Observed，无平台BuyerId，不能发送 |
| 操作者映射 | Mapped，未经运行时核验不能发送 |
| 合成Verified正向路径 | 完全匹配的合成可信来源可持久化Verified；5分钟内同Session/Buyer可通过前置检查 |
| 反例 | 显示名不符、来源缺失、Session/Buyer不符、超时证据均拒绝；失败复核撤销旧Verified |
| Adapter边界 | 测试/未验证身份不会调用发送端口；正向发送端口只用合成ProbePort，未连接真实千牛 |
| Context兼容 | 新Identity字段通过原协调器离线检查；AI Calls0、SentMessages0 |

日志（均为Git忽略的本地文件）：`Output/v111a-checks.log`、`Output/v111a-original-entry.log`、
`Output/v111a-context-check.log`。本阶段未执行真实AI调用或真实发送。

## 当前状态与下一步

**尚未达到真实 OCR Buyer → Verified Identity。**已达到的是“真实OCR身份来源被正确标记为未验证，并在未来发送入口前被阻断”。
合成Verified示例不能替代运行中qnbot提供的平台买家与会话证据。
后续先接入经审计的qnbot可信身份来源，并对唯一测试Session作独立双重核验；
只有真实身份达到新鲜Verified后，才继续V1.11人工确认发送闭环。若仍无可信来源，继续保持Send OFF。

本阶段未建设知识库，未改MessageEnvelope或qnbot核心，未发送买家消息。
