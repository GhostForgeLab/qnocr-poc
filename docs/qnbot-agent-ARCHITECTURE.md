# qnbot-agent 长期架构（V1.31 基线）

此文档区分**当前实现**与**计划中的 qnbot Core + Agent Extension**。代码基线与限制见 [STATUS](qnbot-agent-STATUS-V1.31.md)；集成点源码证据见 [V1.31 分析](../QNBOT_AGENT_INTEGRATION_ANALYSIS.md)。目前不应把离线 PoC 描述成已装入 qnbot 的插件。

## 当前运行边界

qnocr-poc 主项目为 Windows/.NET 8。Capture、Layout、OCR、Detection 和 Messages 将当前可见千牛窗口转换为买家消息；[MessageEnvelope](../Models/MessageEnvelope.cs) 是不变的跨层消息契约。[QnocrShadowRunner](../Shadow/QnocrShadowRunner.cs) 串起 Session、商品上下文、Decision、Knowledge、隔离 AI 适配与日志。qnocr 的 [QnocrMessageAdapter](../Integration/QnocrMessageAdapter.cs) 是 OCR Envelope → qnbot 类型的离线兼容路径，不能反向用作 qnbot 原生事件的入口。

qnbot 桌面应用为 .NET Framework 4.8。其原生消息由 CDP/千牛事件进入 QN，再经过自己的安全、聚合、AI/回复链；[V1.0 分析](QNBOT_MESSAGE_CONTRACT_ANALYSIS.md)记录了入口与类型。当前 Agent 功能主要在 qnocr-poc 的外层运行，复用的是隔离构建/宿主内的 qnbot 源码和生成链，**不是**运行中 qnbot 桌面客户端内的 Agent 扩展。独立 [Agent Console](../AgentConsole/README.md) 与 [Knowledge Console](../KnowledgeConsole/README.md) 均为本地 Web 工具；前者是人工辅助入口，后者操作本地测试知识。

## 目标边界：qnbot Core + Agent Extension

拟议的小范围接入为：qnbot 原生消息 → 只读观察桥 → 版本化 DTO/本机 IPC → .NET 8 Agent Extension → Shadow Reply Preview → 人工查看/编辑/复制。与此同时，qnbot 原消息、安全、回复和发送链照常运行。qnbot 不直接引用 .NET 8 程序集。V1.31 仅新增了 [IAgentExtension](../Extension/IAgentExtension.cs) 与 [QnbotAgentAdapter](../Extension/QnbotAgentAdapter.cs) 离线 PoC；**观察桥、IPC、桌面入口尚未实现**。

| 模块 | 现有位置/接口 | 责任与边界 |
| --- | --- | --- |
| Intent | [Decision](../Decision/IIntentClassifier.cs)、[配置工厂](../Decision/IntentClassifierFactory.cs) | 规则/JEV 返回 IntentResult；JEV 不可用时回退规则。不修改 qnbot 原安全判定。 |
| Knowledge | [IKnowledgeProvider](../Knowledge/IKnowledgeProvider.cs) → [Repository](../Knowledge/IProductKnowledgeRepository.cs) → [IKnowledgeSource](../Knowledge/IKnowledgeSource.cs) | 按店铺、当前商品和所需字段只读查询；当前仅 LocalFile 来源。接入审核状态文件时，仅 Published 且允许 AI 使用的条目可提供事实；旧合成测试文件仍可在显式启用时读取。 |
| AI Provider | [隔离宿主 Provider](../Validation/QnbotCompatibility/IAIProvider.cs) 与配置 | 受控 Shadow 调用并生成本地预览；密钥来自环境/安全配置，不进入消息契约。 |
| Reply Assistant | [Shadow 服务](../Shadow/AssistantReplyService.cs)、[Agent Console](../AgentConsole/Program.cs) | 预览、人工编辑/批准、复制到剪贴板；不调用千牛真实发送。 |
| Feedback | [IFeedbackRouter](../Feedback/IFeedbackRouter.cs)、[规则实现](../Feedback/RuleBasedFeedbackRouter.cs) | 保存人工采纳/修改/拒绝并给出调查方向；不自动改知识、Prompt 或策略。 |
| Product Context | [Session 绑定](../Product/SessionProductContextResolver.cs)、[Knowledge 请求](../Knowledge/KnowledgeRequestBuilder.cs) | 只用当前可信绑定；无商品或冲突时标 Missing，不借历史商品。 |
| UI/知识运营 | [Agent Console](../AgentConsole/README.md)、[Knowledge Console](../KnowledgeConsole/README.md) | 本机、显式启动的辅助界面；不是 qnbot 内嵌面板。 |

## 四项集成原则

**Adapter 原则：** 输入/输出转换放在边界，不向原生 qnbot 消息入口注入 OCR 快照，也不替换 BuyerMessageBurstCoordinator 的 handler。qnbot 原生只读观察路径与旧 OCR → qnbot 离线适配路径是不同方向。

**Interface 原则：** Intent、Knowledge、Knowledge Source、AI Provider、Feedback 与 Agent Extension 都由窄接口隔离具体实现。跨 .NET 4.8/.NET 8 的边界应是经版本化的 DTO/IPC，而不是共享进程内对象。MessageEnvelope 不为集成而改造；原生 messageId、targetId、ccode 作为旁路证据保留。

**Extension Layer 原则：** 后续仅添加可关闭的原生事件观察桥、生命周期钩子和桌面打开入口。观察侧须自行核验买家方向、卖家/会话作用域、身份和新消息；原始事件订阅发生在 qnbot 常规安全处理之前。IPC/sidecar 失败时必须隔离故障，不阻塞原客服流程。

**不修改核心原则：** qnbot 业务处理、现有消息链、AI 生成链、发送逻辑保持原样。默认 Shadow、Send OFF；没有 Verified 平台身份与受控人工发送验收前，不接真实发送。V1.31 Adapter 的非空字段校验不等于身份认证。

## 数据、审计和已知缺口

Session 与商品引用属于外层状态；知识按 SellerId + ProductId 隔离，示例记录均为合成测试资料。Reply Preview、人工编辑、复制、反馈与 Batch 有本地状态/审计；不能把“复制”当作“已在千牛发送”。V1.31 Extension Receipt 检查 Shadow 与零发送计数，但下游实现尚未接入，返回值本身不能证明运行中 qnbot 无副作用。

真实身份仍未 Verified；真实商品知识命中未验收；V1.31 的去重仅为内存 PoC。计划中的 IPC 还需版本、身份来源、访问控制、店铺隔离、超时、背压、持久去重和重连语义。这些是 V1.32 设计/验收门槛，不是当前能力。
