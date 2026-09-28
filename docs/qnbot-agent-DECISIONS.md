# qnbot-agent 关键架构决策（截至 V1.31）

这些决策以当前代码、验证报告和 V1.31 集成设计为依据。状态“已执行”表示当前实现遵守该边界；“计划”不表示相应功能已经接入 qnbot。快照代码基线见 [STATUS](qnbot-agent-STATUS-V1.31.md)。

## D1：暂停真实/自动发送，采用 AI 辅助与人工发送

**状态：已执行。** OCR 可见昵称、buyer-test 测试映射和本地 SessionId 都不能证明平台 Buyer 唯一身份。[V1.11-A](../VALIDATION_V111A.md) 的发送前置检查会拒绝非 Verified；[V1.11-C](../BUYER_IDENTITY_RUNTIME_V111C.md) 没有取得可核对的运行时 targetId、ccode、messageId 与当前 OCR Session 的同刻证据。不能为了通过验收填造 Verified。

因此 AI 可以在授权的 Shadow 测试中生成预览，客服可以编辑、复制并自行在千牛操作；程序默认 Send OFF，不自动点击、粘贴或调用真实发送。V1.7 的隔离确认适配测试不是实际买家发送。[Agent Console 验证](../VALIDATION_V130.md)确认页面没有真实发送路由。若未来讨论人工确认后的程序发送，必须先获得可信身份与目标会话证明，再单独授权和验收；这不属于当前路线的默认能力。

## D2：最终产品入口选择 qnbot 桌面客户端

**状态：产品方向已定，接入仍是计划。** 现有 Agent Console 是 [本机 Web MVP](../AgentConsole/README.md)，解决人工查看、编辑、复制和反馈；它不是最终独立 Web 客服产品。qnbot 的 WPF 辅助窗口、RightPanel 与 App 生命周期提供潜在小范围挂载点，见 [V1.31 集成分析](../QNBOT_AGENT_INTEGRATION_ANALYSIS.md)。

选择桌面入口的结果是：后续先做只读观察桥与进程边界，再提供桌面打开/嵌入辅助面板；不能直接让 qnbot .NET Framework 4.8 引用 qnocr-poc .NET 8 程序集，也不接管原聊天输入或发送按钮。V1.31 并未实际修改 qnbot UI。

## D3：知识系统采用 Provider → Repository → Source 抽象

**状态：接口与 LocalFile 实现已执行，外部来源是计划。** [IKnowledgeProvider](../Knowledge/IKnowledgeProvider.cs) 面向 Agent 查询，[IProductKnowledgeRepository](../Knowledge/IProductKnowledgeRepository.cs) 按 SellerId + ProductId 查商品，[IKnowledgeSource](../Knowledge/IKnowledgeSource.cs) 隔离底层存储。当前 [LocalFileKnowledgeSource](../Knowledge/LocalFileKnowledgeSource.cs) 读取本地测试 JSON 和审核状态；Feishu、Database、Obsidian 或其他来源仅为可扩展目标，**尚未连接**。[V1.25 验证](../VALIDATION_V125.md)说明了实际边界。

走审核状态文件的知识事实需经 Draft、人工审核、Published 且允许 AI 使用后才能给 Provider；旧合成测试文件仅在显式启用时读取。修订保留历史版本，冲突不自动合并。现有示例商品是合成数据，不等于真实商品资产；此前真实商品迁移请求未完成现场资料核对，[V1.24 报告](../VALIDATION_V124_KNOWLEDGE_CONSOLE.md)只验证了测试商品的本地知识控制台。自动批准、自动生成事实、RAG 或向量库都不是现状。

## D4：以 Adapter / Interface / Extension Layer 小范围集成，不重构 qnbot Core

**状态：边界原则已执行，运行时接入待做。** [V1.0](QNBOT_MESSAGE_CONTRACT_ANALYSIS.md)只证明 OCR Envelope 与 qnbot 原类型的离线兼容；[V1.31](../AGENT_EXTENSION_DESIGN.md)新增反方向的只读 Agent Extension 接口/Adapter PoC。两条路径不能混用。qnbot 的 QN 原消息处理、BuyerMessageBurstCoordinator、安全、AI 及发送链保持其原行为；不向它注入 OCR 历史消息，不替换聚合器 handler。

该决策让未来桥接可由配置关闭并独立回滚，也保留了 .NET 4.8 桌面程序与 .NET 8 Agent sidecar 的边界。代价是要单独验证 IPC、身份、去重、背压、故障隔离与桌面生命周期。qnbot 原生事件在其安全检查前对订阅者可见，因此“只读订阅”仍需自己的过滤与审计，不能自动认定为安全输入。

## D5：Intent 与商品上下文不自动制造事实

**状态：已执行。** Shadow CLI 的 Intent 默认配置可选择 JEV，缺密钥或请求失败回退 RuleBased；[V1.27](../VALIDATION_V127.md)和[真实测试 V1.28](../VALIDATION_V128.md)给出证据。但 Intent 只回答“需要什么信息”，并不证明当前商品是什么。无当前商品、OCR 不确定或与历史引用冲突时，Knowledge 保持 Missing；[V1.29](../VALIDATION_V129.md)仅用隔离商品验证了人工/OCR 绑定与知识命中。AI 建议仍由人核实，不把合成商品事实用于真实客服。
