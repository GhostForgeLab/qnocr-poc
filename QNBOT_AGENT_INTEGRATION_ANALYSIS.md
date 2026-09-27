# V1.31 qnbot Agent 集成点分析

分析日期：2026-09-27。只读目标：`D:/CodexWork/qnagent/qnbot`，HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`；目标仓库工作区干净。本报告依据源码与现有隔离兼容性测试，不宣称已在运行中的 qnbot 装载 Agent Extension。

## 结论

最小接入点是 **原生消息的只读观察订阅 + 独立 Agent Extension sidecar + WPF 辅助入口**。原消息链继续由 qnbot 自己处理。现有 `qnocr-poc` 的 .NET 8 Agent Console 不能直接作为 qnbot 的 .NET Framework 4.8 项目引用；需要一个窄的、版本化进程边界。V1.31 只实现边界接口与离线适配 PoC，不改 qnbot、不接实时事件、不发送。

## 消息入口与可扩展位置

| 位置 | 源码证据 | 当前行为 | 集成判断 |
| --- | --- | --- | --- |
| 千牛/CDP 原生事件 | [QN.cs:21](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:21)、[QN.cs:853](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:853) | `QN.EvRecieveNewMessage` 向订阅者提供原始事件，然后反序列化并逐条调用普通处理入口 | 可在后续小补丁中只读订阅；订阅者必须自己解析、过滤买家、去重和处理异常 |
| 正常处理入口 | [QN.cs:487](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:487) | 身份/历史/重复/视觉/Robot 门控、订单计划等原逻辑 | **不得注入** OCR 历史消息或替换处理函数；这不是纯转换 API |
| 聚合与回复 | [QN.cs:105](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:105)、[QN.cs:595](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:595)、[QN.cs:730](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:730) | `BuyerMessageBurstCoordinator` 可能走 AI/发送路径 | 不作为 Extension 挂载点，不调用 `SendTextWithRetryAsync` |
| OCR 兼容适配 | [现有 QnocrMessageAdapter](Integration/QnocrMessageAdapter.cs) | OCR `MessageEnvelope` → 离线 qnbot 类型 | 与本次方向相反；继续保留，不把原生事件重新灌入它 |

特别注意：[IncomingMessageSafety.cs:265](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/IncomingMessageSafety.cs:265) 指出原始事件订阅者在普通买家消息循环前运行。它拿到的是候选事件，不代表已通过 qnbot 安全层。后续 bridge 必须独立确认方向、身份、原生消息 ID、会话新鲜度及来源；失败时保持 `Held`，不得以昵称或 `buyer-test` 升级为 Verified。

当前项目的 `MessageEnvelope` 仍是原契约，来源可标 `Qnbot.Native.ReadOnly`；原生 `messageId`、`targetId`、`ccode` 放在旁路证据，不写进 Envelope，也不伪造商品 ID。[V1.11-B 身份分析](BUYER_IDENTITY_SOURCE_ANALYSIS.md) 已说明这些字段在源码中只是可信身份候选，尚无同一 OCR 会话的独立核验。V1.31 离线 PoC 的 `Verified` 字符串与非空字段校验仅是**结构门槛**，不能替代真实平台证据认证。

## UI 入口

[WndAssist.xaml.cs:159](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/WndAssist.xaml.cs:159) 创建辅助窗口，`Loaded`/`Closed` 见 [391](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/WndAssist.xaml.cs:391)、[404](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/WndAssist.xaml.cs:404)。[RightPanel.xaml.cs:66](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/Widget/RightPanel.xaml.cs:66) 初始化标签，[178](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/Widget/RightPanel.xaml.cs:178) 建立 Robot 标签，[469](D:/CodexWork/qnagent/qnbot/src/Bot/AssistWindow/Widget/RightPanel.xaml.cs:469) 已有功能菜单。`CtlRobot`/`Desk.AddConversation` 是现有对话展示路径，可能与原回复流程关联；不应直接复用来制造 Agent 答复。后续最小 UI 补丁可在 RightPanel 增加一个“AI 助手”入口，打开只读/人工编辑的 loopback Agent Console；不接管聊天输入框或发送按钮。独立 Console 已在 [AgentConsole/Program.cs](AgentConsole/Program.cs) 提供预览、编辑、复制和反馈入口。

## 生命周期入口

[App.xaml.cs:30](D:/CodexWork/qnagent/qnbot/src/Bot/App.xaml.cs:30) 是应用初始化位置，[60](D:/CodexWork/qnagent/qnbot/src/Bot/App.xaml.cs:60)、[62](D:/CodexWork/qnagent/qnbot/src/Bot/App.xaml.cs:62) 挂 Startup/Exit。已有 [BuyerIdentityAliasRuntimeBridge.cs:22](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerIdentityAliasRuntimeBridge.cs:22) 演示了初始化后用 [QN.GetRuntimeSafetySnapshot](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.RuntimeSafety.cs:493) 订阅现存实例，并监听切换事件的增量模式。后续可采用相同生命周期**模式**，但新 bridge 独立存在：启动时注册、实例出现时订阅、关闭时解除订阅并停止 sidecar；不可复用身份别名桥的映射结果作为 Verified 证明。

## 拟议最小修改范围（后续阶段，当前未执行）

1. qnbot 的独立 `AgentExtensionBridge` 文件：只读订阅原生消息，规范化为小型 DTO；不修改 `QN.ProcessIncomingMessageAsync`、BurstCoordinator 或发送实现。异步队列应限流、可取消，不能阻塞原消息事件线程。
2. qnbot `App` 增加一次显式启动/退出钩子；`RightPanel` 增加一个打开本地 Console 的入口。此两处是未来补丁中仅有的集成触点。
3. 进程间协议：.NET 4.8 bridge 发送经过校验的 v1 DTO 到本机 .NET 8 sidecar；sidecar 返回收据与 Shadow 预览。协议需身份来源标签、关联 ID、超时、版本、单实例/店铺隔离和访问控制。默认禁用，启动失败不影响原客服流程。

当前 qnbot 内已有 [BotWebConsoleSyncService](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BotWebConsoleSyncService.cs:164) 可包装聚合器 handler，并有远端命令/发送路径 [667](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BotWebConsoleSyncService.cs:667)。它不适合作为 Shadow 插件承载点，否则难以证明未改原链和 `SentMessages=0`。

## 风险与回滚

| 风险 | 控制与可验收条件 |
| --- | --- |
| 原始事件先于安全层，误把卖家/历史/重复消息当新买家消息 | 只读桥独立过滤并记录 `Held`；没有原生消息 ID 或可信身份来源不投递 |
| .NET 4.8 / .NET 8 ABI 不兼容 | 不跨进程共享程序集；版本化 JSON DTO + 超时 + bounded queue |
| 双入口重复处理同一消息 | 原生 ID + 店铺/买家作用域去重；跨重启持久化及冲突策略需后续验证 |
| 原事件回调异常或 sidecar 不可用影响 qnbot | 回调只排队，异常隔离；失败停用 Extension，qnbot 原链保持可用 |
| 身份误绑定、商品误串线 | 不从昵称猜平台身份，不借历史商品；ProductId 未证实时留空，真实发送继续禁止 |
| UI 误导用户以为建议已发送 | 展示 `Shadow` / 身份状态；只编辑复制，不接 qnbot 发送能力 |

本阶段回滚：在 `qnocr-poc` 回退 V1.31 接口/文档提交即可，qnbot 无任何改动。未来若试点 bridge，关闭扩展开关、停止 sidecar、移除两个挂钩文件/调用即可恢复原状；不迁移或改写 qnbot 原消息/发送数据。
