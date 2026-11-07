# V1.32.2-B qnbot Runtime Observation Feasibility Analysis

分析日期：2026-09-28。只读源码：D:/CodexWork/qnagent/qnbot，HEAD 4802807f26a6abf4cfb4457d5b158b2f51f4ff18，检查时工作区干净。Agent 仓库基线 HEAD 7f10eda6dddfa67e7e9c32894303e332b632220b。本报告依据静态源码，不代表已接入运行中的 qnbot 或千牛。

## 结论

在不修改 qnbot Core 的前提下，可以低侵入地观察运行健康、部分下游状态及当前可见 UI，尚无已证实可提供 Verified Buyer Message 的实时只读出口。独立进程读取现有日志/文件、独立 UIA 观察可作为下一阶段 Shadow 研究方向，最高只能产生 Observed 候选。现有原生事件在安全处理前，且外部进程不能直接订阅 qnbot 内的托管事件。已有 Named Pipe Bridge 只证明模拟 Observed 事件传输，不补足上游身份核验。

## 1. 运行形态、常驻组件和生命周期

| 层 | 源码证据 | 存活范围与观察价值 |
| --- | --- | --- |
| Bot 主进程 | src/Bot/Bot.csproj:8,12,55 为 .NET Framework 4.8 WPF WinExe；StartUp/StartUp.cs:20-59 以 qnrobot mutex 限单实例，初始化日志、数据库后运行 WndNotifyIcon。 | 托盘进程常驻；进程存在只说明 Bot 运行，不证明千牛已连接或当前买家身份。启动可能提权，外部权限须运行时核对。 |
| App 初始化 | App.xaml.cs:30-63 显式启动多个服务/桥；:66-85 有 Startup、Exit、SessionEnding。WndNotifyIcon.xaml.cs:43-66 在 Loaded 后调用 BootStrap.Init。 | 是长期生命周期入口，但本阶段不在其中装载代码。 |
| 千牛窗口与 CDP | BootStrap.cs:20-28 启动 DeskScanner、WebSocket、注入与恢复；DeskScanner.cs:32-84 每秒维护窗口/Desk；MyWebSocketServer.cs:398-430,563-624 在 127.0.0.1:41010 建服务。 | Bot 与 AliWorkbench/AliRender 是不同进程。窗口 PID/HWND 和连接可作健康观察；WebSocket 连接不是买家 SessionId。 |
| QN 实例与业务状态 | QN.cs:925-935 按卖家创建/复用 QN；QN.RuntimeSafety.cs:491-497 提供进程内实例快照；BuyerIdentityAliasRuntimeBridge.cs:22-48 定时附着实例。QN.cs:487-593 管理方向、去重、订单、安全、视觉和 Robot 门控。 | QN、CDP、买家指针与聚合 generation 随连接/切换变化，均非稳定跨进程身份。只适合进程内候选观察。 |
| UIA 诊断 | DeskScanner.cs:39-48 已调用内部 NativeInboundObserver.ReconcileWindows；NativeInboundObserver.cs:12-43,91-106 按 PID/HWND 管理采样，当前只写结构诊断，不发消息候选；QnNativeUiaReader.cs:208-211 标为 Unverified。 | 可参考其窗口定位经验，不能当现成 Agent 消息订阅点。 |

## 2. 不修改 Core 的观察路径

| 方向 | 可得信息 | 可行性、风险与判断 |
| --- | --- | --- |
| A. 进程内 Extension | QN.EvRecieveNewMessage 可获得原始 JSON，QN.GetRuntimeSafetySnapshot 可找到现存实例。 | 在检查的 src/Bot 范围未发现通用插件加载契约或 Agent 程序集自动装载入口。外部 .NET 8 Agent 无法直接订阅 .NET Framework 4.8 托管事件；新增启动钩子、反射注入和运行时注入均超出本轮限制。原事件在安全处理前同步调用（QN.cs:853-881），订阅回调还可能阻塞门控。当前不构成零 Core 修改的 Agent 入口。 |
| B1. 独立进程读日志 | AppLife.cs:17-29 将运行日志存于用户数据 logs/运行日志.txt；QN.cs:574-580 有已识别问题日志。 | 可用于连接、错误、时序诊断。日志可轮转、受开关/版本影响，不是消息契约；部分行含客户文本，缺原生 ID 与原子会话快照。NativeInboundShadowLogger.cs:38-86 刻意只写哈希/状态，不写正文。不能据此宣称完整入站流。 |
| B2. 既有 IPC/本地服务 | MyWebSocketServer.cs:519-529,563-624 的环回 WebSocket 接收千牛页面事件。BotWebConsoleSyncService.cs:398-430,525-543,667-688 向远端同步并可处理命令/发送。 | 41010 不是只读广播；外部连接、嗅探或重放会干扰来源归属。Web 同步是双向控制面，不是本地只读订阅，且有手动发送路径。V1.32.1 的 Agent Named Pipe 当前仅是本仓库模拟通信，qnbot 不生产事件。当前无合适的只读 IPC 出口。 |
| C. 独立 UI 状态 | 可参考 DeskScanner.cs:32-84 的 PID/HWND 发现与 QnNativeUiaReader.cs:44-76 的结构快照，观察窗口、当前会话和可见内容。 | 不进入 Bot 进程；但仅有当前渲染范围，虚拟列表、遮挡、切换及多店铺共窗可能漏读/串线。现有 qnbot UIA V0.2 只采结构（NativeInboundObserver.cs:91-106；NativeInboundSnapshot.cs:86-99），不提取消息正文。昵称/标题不是 BuyerId。可研究 Shadow，最多 Observed。 |
| D. 数据层 | Common/DbHelper.cs:23-31 指向 data/bot.db；BotConversationHistoryStore.cs:16-38,76-101 有问答状态行；ShopScopedPathProvider.cs:22-69 定义店铺 profile/config/state/cache；ProductDatabasePaths.cs:20-33 提供商品库只读 SQLite 打开示例。 | 可离线核对配置、知识和下游问答历史。历史异步写入且有限期/数量，不含原生 targetId、ccode、消息 ID；商品库不是入站流。活动库需只读连接、忙超时、一致快照。不能作为低延迟消息或 Verified 身份来源。 |

直接抓 Bot 与千牛间 WebSocket 字节或注入页面虽然可能取得原始正文，但高耦合于协议和连接归属，并且仍早于 qnbot 的业务过滤；本轮不推荐为低侵入路径。

## 3. 身份可信等级模型

等级表示证据强度，不表示文本识别置信度，也不是已实现的 Bridge 字段。Held 是处理状态，可与低等级并存；不以多份同源副本互相“投票”升级。

| 等级 | 最低证据 | 允许用途与边界 |
| --- | --- | --- |
| L0 未归属信号 | 仅见进程、窗口、连接、结构哈希或日志行。 | 健康/时序诊断；不构成买家消息。 |
| L1 Observed | 看到原始或 UI 候选消息，并记录采集源、时间、运行实例与可追查引用；方向/身份仍可能错误。 | 隔离 Shadow 或人工核对；冲突、缺失和旧消息置 Held。不代表 qnbot 已安全接受。 |
| L2 Mapped / 关联候选 | 同店铺昵称、窗口/会话线索或历史映射与原生候选字段一致，未发现冲突。 | 便于人工缩小范围，仍是只读。昵称、UI 当前选中、别名表、哈希和本地 SessionId 均非平台身份凭据。 |
| L3 Verified | 已认证的卖家/店铺作用域；可追溯且非空的原生买家 targetId；消息 ccode、稳定原生消息 ID、明确入站方向；同一运行实例的同刻或可证明无切换的会话快照；新鲜度、去重、系统/卖家/撤回/恢复重放排除结果；可信来源之间无冲突。上述证据须由独立核验链实际取得并可审计。 | 仅说明身份与该消息的关联已核验。商品绑定、AI、发送仍各有独立门槛；即使入聚合器也不自动是 L3。 |

原生事件触发次序见 QN.cs:853-881：先通知 QN.EvRecieveNewMessage，再解析 JSON 和执行 ProcessIncomingMessageAsync，因此最多 L1。业务去重 TryAccept 早于 IncomingMessageSafety.Evaluate（QN.cs:527-551），不能单独升级；订单路径可提前分流（:532-545）。恢复入口 QN.MessageRecovery.cs:391-464 可与普通事件重复。ChatResponse.cs:18-45,165-184 的原生字段只是核验候选，字段存在不等于已验证。UIA/OCR、日志、SQLite 行、Bridge 收据以及同一原事件派生的多个副本，不能互作独立权威来源。

本仓库 Extension/AgentBridge/AgentEventEnvelope.cs:6-29 的 v1 校验只允许 IdentityStatus 为 Observed；它缺原生消息 ID、店铺作用域、ccode 和身份来源证明。因此当前协议与上游证据最高只能表达 L1，不能凭 BuyerId 或 SessionId 字符串宣称 L3。ProductId 若缺当前咨询的独立证据应留空。

## 4. 可行链路与下一次评审门槛

千牛可见窗口 / qnbot 既有诊断与状态
→ 独立只读观察（目前仅健康诊断或 Observed 候选）
→ 独立身份与新鲜度核验（目前缺可信输入和实时同会话证明）
→ Agent Bridge（已有模拟 Observed 传输 PoC）
→ Agent Extension（本轮只分析；不接 AI/发送）

| 待验证问题 | 可接受的只读证据 | 停止条件 |
| --- | --- | --- |
| 实际进程、窗口、店铺和连接生命周期是否符合源码？ | 脱敏 PID/HWND、连接/切换时间线、多店铺与重连样本。 | 卖家/窗口归属含糊，或观察动作影响原流程。 |
| UIA 能否稳定读取当前会话及消息？ | 不含客户原文的字段可用率、延迟、漏读、切换和虚拟列表统计。 | 只能看到结构或无法锁定单一会话时，不宣称消息观察成功。 |
| 日志/数据库能否提供关联键？ | 只读字段目录、写入时机/延迟、轮转/清理行为。 | 缺原生消息 ID、targetId、ccode 或仅有下游摘要时，维持 Observed/Held。 |
| 是否存在安全处理后发布点？ | 单独评审证明普通、订单、恢复路径的覆盖和不可变同会话证据。 | 若需改 Core 或包装处理/发送 handler，另立授权与变更评审；本轮不实施。 |

当前架构决定：独立进程诊断与 UI Shadow 值得后续验证；现有 WebSocket、远端同步、日志、SQLite、原始事件及进程内别名表均不能当 Verified 发布源。未来若需安全处理后实时事件，应先单独评审新的 qnbot Core 边界。V1.32.1 的 AI calls = 0、SentMessages = 0 是前阶段模拟 Bridge 结果，本轮未运行通信或业务实验。

## 验证范围

只读检查了 qnbot 指定 HEAD 的启动、WebSocket/CDP、消息处理、UIA、日志、SQLite、Web 同步源码，以及本仓库 V1.31、V1.32.1、V1.32.2-A 文档和 Bridge 契约。未读取真实客户数据，未运行 qnbot/千牛，未测试实际 UIA/文件权限、数据库并发或观察成功率。源码搜索未发现通用插件加载入口仅是当前检索范围的判断，尚需部署包或运行时审计确认。
