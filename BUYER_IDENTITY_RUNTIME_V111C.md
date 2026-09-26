# V1.11-C qnbot运行时身份快照验证报告

分析日期：2026-09-26。只读对象为当前千牛进程、qnocr-poc与
`D:/CodexWork/qnagent/qnbot`（HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`）。
本轮没有启动Bot、修改代码、注入消息、调用AI或发送。

## 结论：情况B

**未取得真实qnbot运行时身份快照，OCR Buyer不能提升为Verified。**
在交互式用户会话只读检查：`Bot`实例0，千牛工作台进程2、可见窗口1。
既然qnbot运行时不存在，就没有同一运行实例的原生`QNChatMessage`或当前会话结果可读取；
不能把源码字段、旧日志、OCR昵称或Adapter合成标识当作现场平台证据。
身份维持Observed/Mapped/Test，真实发送资格为否；转向人工发送辅助模式，停止本轮真实发送探索。

## 候选字段与可靠等级

| 字段 | 源码位置/来源 | 本轮等级 | 原因 |
|---|---|---|---|
| `fromid.targetId`及`fromid.nick/display` | `D:/CodexWork/qnagent/qnbot/src/Bot/ChatRecord/ChatResponse.cs:29`；运行中原生`receiveNewMsg`解析为`QNChatMessage` | Candidate | 结构支持平台目标ID，但本机未取得原生消息对象或非空实值；方向和卖家归属也未作现场核对。 |
| `cid.ccode` | `ChatResponse.cs:24`；qnbot远端历史核验以当前会话`Ccode`查询 | Candidate | 可能是会话码，未与当前会话及Buyer目标ID作同刻匹配。 |
| `mcode.clientId/messageId` | `ChatResponse.cs:18` | Candidate（消息级） | 可提供原生消息证据，不单独证明Buyer或ConversationId。 |
| 当前会话`Nick/Display/Ccode/TargetId` | `DbEntity/Response/LocalUser.cs:40`；`CDPClient.cs:781`调用现有`GetCurrentConversationID` | Candidate | 运行时会话会切换；本轮无Bot/CDP实例，也无受支持的qnocr只读导出。 |
| WebSocket `SessionID` | `MyWebSocketServer.cs`、`CDPClient.cs` | Unknown（买家会话） | 是物理WebView连接标识，不是聊天ConversationId。 |
| Native UIA `ConversationFingerprint/IdentityStatus` | `NativeInboundMessage.cs`、`QnNativeUiaReader.cs:210` | Unknown/Unverified | UIA读取器明确设`IdentityStatus=Unverified`、指纹为空。 |
| qnocr `mcode.messageId`、`cid.ccode` | `Integration/QnocrMessageAdapter.cs:75-88` | LocalGenerated | 消息ID为本地哈希，ccode为`qnocr:<hash>`；当前测试`targetId`为空。 |
| qnocr `BuyerDisplayName`、`SessionId`、`buyer-test:*` | `Shadow/SessionRegistry.cs`、`Models/MessageFingerprint.cs` | Observed / Mapped-Test | 来自OCR标签与本地测试映射，不是平台原生BuyerId。 |

本轮**现场PlatformVerified证据数为0**。源码中的字段是否在具体千牛版本的真实入站消息里非空，仍需运行中同一卖家会话的只读证据。
`ConversationId`未在当前qnocr输入中取得；`Ccode`只能列为候选，不能用WebSocket SessionID或本地SessionId替代。

## 链路核对

qnbot源码路径：`receiveNewMsg` → `CDPClient` → `QN.Cdp_EvRecieveNewMessage` →
`ChatResponse.result`中的`QNChatMessage` → `QN.ProcessIncomingMessageAsync` →
`BuyerIdentityAliasService`及`BuyerMessageBurstCoordinator`。
当前会话的另一条只读候选路径为`GetCurrentConversationID()`，但它只存在于qnbot/CDP运行上下文。

qnocr现行路径：PrintWindow截图 → OCR买家头/气泡 → `SessionRegistry.Resolve` →
MessageEnvelope → `QnocrMessageAdapter`。UIA只用于布局几何，不读身份文本；
本项目没有原生消息、DOM或WebSocket身份输入。Adapter为离线兼容构造的同名`messageId/ccode`不能冒充平台字段。
由于没有qnbot实例与真实字段，本轮没有执行实时Adapter PoC，也没有尝试昵称猜测关联。

## 验证结果与安全状态

- .NET 8主项目及验证项目Build Success，0警告、0错误；隔离.NET Framework 4.8宿主Build Success，仅有原上游源码既有CS4014警告。
- 累计检查276/276、原qnbot入口17/17通过，禁止业务调用0。
- 离线Context兼容检查：协调器回调1、AI Calls0、SentMessages0；这不是运行时身份快照。
- 本轮未启动AI Shadow或真实发送：AI Calls0、SentMessages0。qnbot工作树未修改；qnocr除本报告外未改动。
- 本报告是唯一新增文件；MessageEnvelope、qnbot核心、Adapter与发送逻辑无架构变化。

本地测试记录（Git忽略）：`Output/v111c-checks.log`、`Output/v111c-original-entry.log`、
`Output/v111c-context-check.log`。字段详细溯源另见`BUYER_IDENTITY_SOURCE_ANALYSIS.md`。

## 缺失证据与下一步

要评估Verified，至少需要已运行的同一qnbot卖家实例提供受支持的只读身份快照：
非空原生Buyer targetId、当前会话Ccode/TargetId、来源消息ID和时间、登录卖家作用域，
并与OCR Session同刻精确核对及稳定复读。任何字段缺失或会话切换都必须Fail Closed。
目前没有此类快照或可调用导出，不能进入真实人工确认发送。
按任务要求停止真实发送探索，优先采用人工发送辅助模式；不为验收制造Verified身份。
