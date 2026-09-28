# V1.32.3-B qnbot Session Context Observation PoC

分析日期：2026-09-28。只读 qnbot 源码：D:/CodexWork/qnagent/qnbot，HEAD 4802807f26a6abf4cfb4457d5b158b2f51f4ff18；Agent 仓库基线：683f128b32c72eba87c0919b9a6408db6dc8a4a8。本轮未读取真实聊天内容，也未运行 qnbot 业务调用。

## 1. 背景与结论

V1.32.3-A 只验证了进程状态 → RuntimeObservedEvent → Agent Bridge → Receiver，最高为 L0 运行时观察。本轮寻找实际存在的 Session 层及可供独立进程安全读取的生命周期状态。

**qnbot 有多个会话相关对象，但没有已验证的、无需修改 Core 即可订阅的通用买家 Session 生命周期出口。**页面连接、当前会话指针、买家工作代数及接待学习轮次各有不同含义；不能合并为一个 SessionId。本轮不实现 SessionObserver，不扩展 Agent Bridge，也不生成 Session 事件。

## 2. 当前 Session 模型分析

| 实际对象 | 创建和作用 | 外部读取与边界 |
| --- | --- | --- |
| WebSocketSession.SessionID / CDPClient | MyWebSocketServer 接入页面连接，以 SessionID 索引客户端并按卖家认领权威通道（MyWebSocketServer.cs:57,89-91,202-254,328-380）。 | 是页面传输连接，可有重复页面和 standby；不是买家会话 ID。诊断和日志有建立/断开信息。 |
| Conversation / QN.Buyer | Conversation 有 Nick/Display/Ccode/TargetId（DbEntity/Response/LocalUser.cs:40-63）；QN.SetActiveConversationByNick 可仅按昵称补建对象（QN.cs:391-417）。onConversationChange 更新可见指针并发 EvBuyerSwitched（CDPClient.cs:524-551；QN.cs:903-919）。 | 指针和事件在 Bot 进程内。切换不等于旧买家会话关闭；后台通知明确不改 QN.Buyer（QN.cs:444-458）。 |
| BuyerSessionAgent.SessionState | 静态字典按 sellerNick#buyerNick 建立；每条唯一消息获得 generation（BuyerSessionAgent.cs:108-180,575-590,763-789）。 | 状态可经历 Observed、Coalescing、Processing、Generating、Ready、Sending、Waiting 和终态（:10-23,347-405）。GetSnapshot 是进程内方法（:523-550）；Completed 是 generation 工作结果，不是平台客服会话关闭。 |
| ConversationContextStore / ConversationStateService | 内存 TimelineState 保存 turns，后者从文本提取主题、实体、意图与阶段（ConversationContextStore.cs:16-45,55-92；ConversationStateService.cs:10-32,69-108）。 | 是聊天内容与回复上下文，非生命周期状态机；本轮禁止读取这些文本，也没有外部只读出口。 |
| ConversationSessionLearningService.ActiveSession | 原始事件触发以卖家/买家为键的学习轮次，本地 EntityId 为 GUID；报告写入 bot.db（ConversationSessionLearningRuntimeBridge.cs:101-185；ConversationSessionLearningService.cs:19-40,87-118,151-196,275-290,657-699,819-827）。 | 这是自动学习任务，非平台接待会话。表有状态与时间列，也混有 Summary、Style、SuggestionsJson、Error 等内容；没有验证真实部署的只读读取与稳定性。 |
| MultiShopRuntimeSessionCoordinator / ActiveShopSessionRegistry | 跟踪聚焦 WebView、卖家和 CDP 连接（MultiShopRuntimeSessionCoordinator.cs:27-56,110-180,281-410）。 | 是当前活动店铺指针，不是单买家会话。 |
| 本仓库 MessageSession / SessionRunLifecycle | Shadow SessionRegistry 自行生成本地 ID 和状态文件（Models/MessageSession.cs:1-13；Shadow/SessionRegistry.cs:42-80；Shadow/SessionRunLifecycle.cs:21-71）。 | 属于 qnocr 隔离运行，不能证明 qnbot 原生有相同 SessionId。 |

此外，NativeInboundObserver.cs:41-62 的 Session 仅按 PID/HWND 管理 UI 采样；ManualRechargeAssistantService.cs:66-80 的 Session 是充值业务工作状态；ConversationMapIngressWatchdog 的 PendingSession 只是待恢复队列（BulkListManagementUiBridge.cs:202-212）。这些同名对象也不构成通用买家 Session。

## 3. 生命周期分析

| 层 | 创建与更新 | 结束或销毁 | 判断 |
| --- | --- | --- | --- |
| 页面连接 | WebSocket 建立 SessionID；登录状态、权威通道和 CDP 活动持续更新。 | SessionClosed 清理连接与映射并释放 CDP（MyWebSocketServer.cs:536-562）。 | 有连接生命周期，不与买家会话一一对应。 |
| 当前可见会话 | 初始化读取 GetCurrentConversationID，或收到 onConversationChange；QN.Buyer 随切换被覆盖或按昵称补建（MyWebSocketServer.cs:364-369；CDPClient.cs:524-551；QN.cs:391-436）。 | 没有匹配买家的 SessionClosed。 | 可观察当前选中项变化的概念，不能推出会话结束。 |
| 买家工作状态 | ObserveBuyerMessage 创建 seller/buyer 状态和 generation；聚合器转换状态（BuyerMessageBurstCoordinator.cs:544-571）。 | generation 到终态后，满足空闲条件才由 Prune 移除（BuyerMessageBurstCoordinator.cs:770-773；BuyerSessionAgent.cs:551-572）。 | 是进程内回复工作单元，非客服会话。 |
| 自动学习轮次 | 原始买家事件触发 NewSession、写报告；后续买家或卖家事件更新时间；启动后可恢复 1 日内未完成报告（ConversationSessionLearningService.cs:151-196,761-795）。 | 买家静默 5 分钟、卖家静默 30 秒后移出 Active；可重试、完成、跳过或失败。完成报告按 90 天/1000 条清理（:299-420,803-815）。 | 有持久学习任务边界，不能代表客服接待关闭。 |

没有一条统一的 Session Created → Active → Updated → Closed 可由独立进程直接安全观察。连接关闭、当前指针切换、学习完成和 generation 完成不可互相代替。

## 4. 可观察入口与本轮决定

| 候选 | 最多可见 | 决定 |
| --- | --- | --- |
| V1.32.3-A ExternalProcessObserver | 进程存在/PID，L0。 | 保留现状；不生成 Session 事件。 |
| qnbot 日志 / BotConnectionDiagnostics | 连接、当前 seller/buyer 名称、工作转换（MyWebSocketServer.cs:654-775；QN.cs:436；BuyerSessionAgent.cs:431-435）。 | 日志会轮转并可能混有正文与标识，缺严格单会话契约；不采集整份日志。 |
| 独立 UIA 结构 | 窗口/PID、结构角色；既有 reader 不读 Name/Text/Value（NativeInbound/QnNativeUiaReader.cs:15；NativeInbound/NativeInboundSnapshot.cs:53-74,704）。 | 可作 L0 结构线索；无稳定买家 SessionId 和关闭事件。 |
| bot.db 自动学习报告 | 理论上可只选 EntityId、Status 与时间戳。 | 生产者监听安全处理前原始事件；本轮未验证活动数据库路径、权限、并发和启用状态。不读取 Buyer/CCode/Summary/SuggestionsJson 等列进入 Agent，不生成事件。 |
| 进程内事件与快照 | EvBuyerSwitched、BuyerSessionAgent.GetSnapshot、QN.GetRuntimeSafetySnapshot。 | 独立 .NET 8 Observer 无现成加载/订阅入口；注入或改 Core 超出限制。 |
| 本地 WebSocket 41010 | 页面上报与 CDP 命令同通道（MyWebSocketServer.cs:398-430,519-529,563-624）。 | 双向业务通道，不作为被动观察端口。 |

由于没有**已验证的通用买家 Session 只读出口**，本轮不创建 SessionObserver 模块、SessionObserverChecks 或 Bridge 事件。现有 AgentEventEnvelope 只接受 BuyerMessageObserved 和 RuntimeObserved（Extension/AgentBridge/AgentEventEnvelope.cs:21-69）；复用前者会错误暗示已观察到买家消息。

未来若核验到安全入口，可定义与 qnbot 私有类型解耦的 ISessionObserver.Poll()，返回 SessionObservedEvent(EventType, SessionId?, Timestamp, Source, IdentityStatus 固定 Observed, Metadata)。SessionId 必须注明命名空间，例如“连接 ID”或“学习轮次 ID”；只允许白名单元数据。Bridge 需新增专用类型及 Receiver 校验，禁止映射成 MessageEnvelope 或 Verified。此处仅为接口设计门槛，非本轮实现。

## 5. 身份可信等级

| 等级 | 所需证据 | 本轮状态 |
| --- | --- | --- |
| L0 Runtime 状态 | 进程、窗口、连接或结构健康信号。 | V1.32.3-A 已有进程观察。 |
| L1 Session Observed | 明确来源、时间、类型与命名空间的会话状态线索。 | 源码证实候选存在；未运行验证外部 Session 观察。 |
| L2 Session Bound | 独立核对同一店铺、页面连接、稳定会话键及切换/重连时序。 | 未达到。昵称、GUID、generation、连接 ID 均不能单独完成绑定。 |
| L3 Verified Buyer Context | 安全处理后的方向、原生身份/会话证据、消息 ID、同一时刻店铺绑定和可审计门控。 | 不在本阶段；当前无此出口。 |

未来任何 SessionObservedEvent 的 IdentityStatus 都固定为 Observed。L1/L2 描述关联强度，不自动升级买家身份；L2 不等于 L3，不触发 AI、Intent、Knowledge 或发送。

## 6. 风险分析

1. WebSocket SessionID、学习轮次 EntityId、Shadow SessionId 和工作 generation 无共同身份空间。
2. 自动学习监听 QN.EvRecieveNewMessage（ConversationSessionLearningRuntimeBridge.cs:101-185），先于普通消息安全处理（QN.cs:853-891）；学习报告存在不能证明买家消息被安全接受。
3. UI 切换、CDP 断线、静默超时和 generation 完成分别代表不同事件；重连、多店铺、后台通知和恢复会造成延迟或重复。
4. 日志/报告混有聊天摘要和标识；完整读取越过本阶段边界。调用学习服务还可能进入远端历史、AI 复盘和知识写入，本轮仅分析源码。
5. 未验证真实部署的数据库、文件权限、只读事务、WAL 一致性、UI 时序与版本差异。

## 7. 推荐路线

保持 V1.32.3-A 的 L0 链路。若继续零 Core 修改，仅在隔离/脱敏实例上核验 bot.db 自动学习**状态列**，并将未来事件命名为 LearningRoundObserved，不能称为通用买家 Session。若需要可靠的客服 Session 生命周期，应另行评审 qnbot Core 的只读后置发布契约；本轮不实施。

## 8. 后续 PoC 计划

1. 确认实际数据库路径、权限、表和学习功能启用状态；仅用合成或脱敏数据。
2. 只读 SQLite 连接明确选择状态与时间列；测并发写入、重启恢复、重试、清理和跨店铺，不读取文本列。
3. 核对学习任务开始/更新/完成是否会被误报为客服会话关闭；若无法稳定区分，停止 Bridge 集成。
4. 独立评审泛化会话事件的来源与作用域后，再实现 ISessionObserver、SessionObservedEvent、专用 Bridge 类型与定向检查；持续保持 Observed，AI 调用、SentMessages、自动发送均为 0。

**验收回答：**qnbot 内部存在会话相关层，其中页面连接与自动学习轮次有各自生命周期，BuyerSessionAgent 有工作状态。当前没有安全且已验证的外部通用买家 Session 观察点。继续探索 Context 层仅适合元数据级隔离研究；尚不能建立 qnbot → Session Context Observer → Agent Bridge → Agent Extension 的可信买家上下文链路。
