# V1.11-B qnbot 运行时 Buyer 身份来源分析

分析基线：`qnocr-poc` 当前代码；只读检查 `D:/CodexWork/qnagent/qnbot`，HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。
本阶段未修改 qnbot、MessageEnvelope 或身份状态机；AI OFF、Send OFF，真实发送 0 次。

## 结论

**qnbot 源码中存在原生身份候选字段，但当前 qnocr OCR 链路没有可访问、可核验的运行时平台身份来源。**
原生 `receiveNewMsg` 的 `QNChatMessage.fromid.targetId` 和当前会话结果中的 `TargetId`、`Ccode` 有潜力构成独立证据；
必须在同一个已登录卖家/qnbot运行实例中取得非空原生值，并核对当前会话、OCR显示名、时间和消息来源。
当前交互式会话中千牛在运行，`Bot` 进程为 0；没有运行时快照或受支持的只读导出供 qnocr Adapter 获取这些值。
因此**没有真实 BuyerIdentity 达到 Verified，不能进入真实发送阶段**。

## 1. 字段位置与可靠等级

| 字段/能力 | 代码位置与真实来源 | 当前可靠等级 | 判断 |
|---|---|---|---|
| `QNChatMessage.fromid.nick/display/targetId`、`toid`、`loginid` | `src/Bot/ChatRecord/ChatResponse.cs`；原生 `receiveNewMsg` 反序列化后进入 `QN.ProcessIncomingMessageAsync` | Verified 候选，仅限活的原生消息 | `targetId`可能提供平台目标ID，但必须非空、确认方向/卖家，并与同会话快照核对；代码存在不等于本轮取得真实值。 |
| `QNChatMessage.cid.ccode` | 同一原生消息对象；qnbot远端历史核验使用当前会话`Ccode`查询 | Verified 候选 | 是会话码候选，需与当前会话的`Ccode`及买家`TargetId`一致；不能由OCR推算。 |
| `mcode.clientId/messageId`、`ext.ww_msgid` | 原生消息字段；`IncomingMessageSafety`用于消息去重 | 消息级证据 | 可标识消息，不单独证明BuyerId或当前会话；qnocr合成同名字段不是平台消息ID。 |
| `ConversationResponse.Result.Nick/Display/Ccode/TargetId` | `DbEntity/Response/LocalUser.cs`；`CDPClient.GetCurrentConversationID()`调用现有`im.uiutil.GetCurrentConversationID` | Verified 候选，动态 | qnbot已有读取当前会话的能力；当前会话会切换，源码中有连续读取、非买家过滤和卖家/买家核对。未暴露给本项目的只读接口。 |
| `BuyerIdentityAliasService` | `src/Bot/ChromeNs/BuyerIdentityAliasService.cs`；运行时桥接观察原生消息的internal nick/display/targetId | Mapped/辅助核对 | 店铺作用域内存别名表，非对外身份凭据；仅用昵称等价或缓存别名不能证明平台唯一ID。 |
| `BuyerMessageBurstItem.SellerNick/BuyerNick/MessageKey/SessionGeneration` | `BuyerMessageBurstCoordinator.cs`；由入站处理与聚合器填入 | Mapped/本地状态 | `SessionGeneration`为qnbot本地代数，不是千牛ConversationId。 |
| `BuyerSessionAgent`会话键 | `BuyerSessionAgent.cs`；SellerNick+BuyerNick及消息Key | Mapped/本地状态 | 支持去重和聚合，不生成平台BuyerId。 |
| `WebSocketSession.SessionID` | `MyWebSocketServer.cs`、`CDPClient.cs`；物理WebView连接/页面会话 | Unknown（对买家身份） | 连接ID不是聊天ConversationId，不能与OCR SessionId互换。 |
| `NativeInboundMessage.IdentityStatus/ConversationFingerprint` | `src/Bot/NativeInbound` | Unverified | DTO虽定义Verified枚举，但当前UIA读取器设置`IdentityStatus=Unverified`、`ConversationFingerprint=null`；没有本轮可用的强身份。 |
| qnocr `BuyerDisplayName` | `SessionRegistry.ReadBuyer`，来源为聊天区域OCR头部 | Observed | 可能是昵称/别名并受OCR误读影响，不是平台主键。 |
| qnocr `BuyerId`、`SessionId` | 显式映射 + OCR标签的本地哈希；当前配置为`buyer-test:*` | Mapped/Test | 可稳定隔离测试会话，不能成为真实发送目标。 |
| qnocr UIA | `Layout/UiaGeometryProbe.cs` | Unknown（对身份） | 只读取ControlType、BoundingRectangle、IsOffscreen；不读Name、Value、TextPattern。 |
| qnocr DOM/CDP/WebSocket | 本项目 Capture/Layout/Shadow/Adapter 源码 | 不存在当前输入路径 | 本项目没有接入qnbot的原生事件或当前会话结果。 |

## 2. 消息链与字段溯源

qnbot原生路径：千牛页面/WebSocket `receiveNewMsg` → `CDPClient` → `QN.Cdp_EvRecieveNewMessage`
→ `JsonConvert.DeserializeObject<ChatResponse>` → `QNChatMessage` → `QN.ProcessIncomingMessageAsync`
→ `BuyerIdentityAliasService.ObserveMessage` → `BuyerMessageBurstCoordinator.Enqueue`。
在这条链上，`fromid.targetId`、`cid.ccode`和原生`mcode.messageId`可随消息到达运行中的qnbot实例。
`GetCurrentConversationID()`是另一条读当前会话的路径，结果带`Nick/Display/Ccode/TargetId`，但它并没有自动流入 qnocr 的截图/Envelope。

本项目路径：窗口PrintWindow截图 → OCR聊天头/气泡 → `SessionRegistry.Resolve` → `MessageEnvelope` →
`QnocrMessageAdapter` → 隔离qnbot协调器。
这里Envelope的SellerId/BuyerId来自调用方或测试映射，不是从原生事件恢复。
Adapter明确构造`mcode.clientId=qnocr-offline-v1`、哈希`messageId`、`cid.ccode=qnocr:<hash>`；
`fromid.targetId`仅取另行提供的映射，当前测试映射为空。
这些字段用于兼容性/去重，**绝不能与原生平台字段同名就视为Verified**。

## 3. 验证与缺口

- 只读源码核对已发现上述候选字段和内部运行时链；未进行MessageSDK逆向、DLL Hook或消息注入。
- 交互式用户会话只读检查：千牛工作台进程存在，运行中的`Bot`进程为0；没有可获取的实时原生消息/当前会话快照。
- qnocr编译成功；现有检查276/276和原qnbot入口17/17通过。原入口离线回执确认Adapter键是合成的，未知`targetId`不被猜测，发送/禁止业务调用均为0。
- 没有现场平台BuyerId、ConversationId或Ccode与OCR SessionId的同刻关联证据，无法调用V1.11-A的`ITrustedBuyerIdentitySource`生成真实证明。

现阶段不做“读取字段名即可Verified”的Adapter PoC。若未来存在**受支持的、只读的qnbot运行时身份导出**，最小PoC应由该运行时同一卖家会话输出：
非空平台BuyerId/TargetId、当前会话Ccode、内部nick/display、来源消息ID、捕获时间和卖家/窗口作用域；
Adapter仅做精确同刻关联，并要求切换前后稳定读数，再交给`SessionIdentityBindingService`。
缺失任何字段、来源不明或结果与OCR会话不一致时保持Mapped/Observed，不升级Verified。
不应为了提供导出而侵入qnbot核心或绕过当前身份安全门。

## 4. 当前决定

真实Buyer身份：未Verified。真实发送资格：否。
下一步需先获得当前测试Session的可信qnbot运行时身份快照/只读接口并验证同一卖家与ConversationId；
在此之前维持AI OFF、Send OFF，不进行真实发送。
