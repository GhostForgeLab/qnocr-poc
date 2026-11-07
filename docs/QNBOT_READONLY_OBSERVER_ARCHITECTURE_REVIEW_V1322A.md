# V1.32.2-A qnbot ReadOnly Observer 架构评审

分析日期：2026-09-28。只读源码仓库：`D:/CodexWork/qnagent/qnbot`，HEAD `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`；Agent 仓库分析前 HEAD `7f10eda6dddfa67e7e9c32894303e332b632220b`。本报告只依据源码和既有 V1.31、V1.32.1 验证记录；未连接运行中的 qnbot 或千牛，未实施 Observer。

## 结论

1. **现有 `QN.EvRecieveNewMessage` 是原始候选事件，不是安全处理后的买家消息。**它在反序列化、方向判断、业务去重、历史过滤之前同步通知订阅者。此处订阅最多标记 `Observed`。
2. **当前没有现成的、同时携带已接受消息、身份核验结果和商品绑定结果的只读订阅点。**普通文本进入聚合器之前经过部分业务过滤，但没有独立平台身份核验；商品解析在后续回复链中，订单路径又绕过普通安全判定。
3. **本阶段不挂载 Observer。**在尚无独立核验实现且禁止改 qnbot Core 时，原始事件只能供隔离诊断，不能作为 Agent 的可信买家输入。若以后允许单独评审极小的只读事件，普通入站安全决策完成后是概念上的位置；这不是本轮实现或授权。
4. V1.32.1 的 `AgentEventEnvelope` 只证明模拟 `Observed` 事件能经 Named Pipe 传输。其 v1 字段没有原生消息 ID、店铺/卖家作用域、会话码、身份来源或安全结论，不能据此提升 `Verified`。

## 1. 消息生命周期

```text
千牛页面 receiveNewMsg
  → MyWebSocketServer.OnRecieveMessage
  → CDPClient.DispatchInboundEvent / RecieveNewMessage
  → QN.Cdp_EvRecieveNewMessage
      → QN.EvRecieveNewMessage（原始 JSON，同步，安全处理前）
      → ChatResponse.result → QNChatMessage，按消息时间排序
      → 逐条 QN.ProcessIncomingMessageAsync
          → 非买家/系统来源拦截、卖家回显分流、买家方向判定
          → 买家业务去重登记
          → 订单特殊计划分流（可能在普通安全判定前结束）
          → IncomingMessageSafety.Evaluate（历史/撤回/类型等）
          → VisionMessageDecision、Robot 门控
          → BuyerMessageBurstCoordinator.Enqueue
              → BuyerSessionAgent 生成 generation、再次去重
              → 合并前固定规则 / 合并
              → 回复、AI 与可能的真实发送链
```

源码：`src/Bot/ChromeNs/MyWebSocketServer.cs:519-529`、`CDPClient.cs:264-301,512-521`、`QN.cs:853-891`。`CDPClient.RecieveNewMessage` 给事件参数 `Buyer` 赋空字符串，身份候选字段在 JSON 内。`QN.Cdp_EvRecieveNewMessage` 在 `_incomingMessageGate` 内先调用订阅者，再解析并逐条处理；回调阻塞或抛错可能占用该门、落入外层异常处理，影响原链。

普通入口顺序见 `QN.cs:487-593`：`NonBuyerConversationGuard.ShouldBlockMessage` → `BuyerIdentityAliasService.ObserveMessage` → 卖家回显/买家方向 → `_handledBuyerMessageDeduplicator.TryAccept` → `OrderPlacedAutoReplyService.TryCreatePlan` → `IncomingMessageSafety.Evaluate` → `VisionMessageDecision.Decide` → Robot/视觉门控 → `Enqueue`。因此“已进入 handled ledger”只表示业务键被占用，**不表示通过历史、类型或回复安全判断**。`IncomingMessageSafety.cs:297-307` 的 `ShouldCallAi=true` 对商品链接也可能只是复用本地预设回复/发送链，不证明实际 AI 调用或 Agent 可用。

恢复路径 `QN.MessageRecovery.cs:391` 与 `BusinessInboundLivenessRecoveryBridge.cs:325` 可重用普通入口。后台 `onShopRobotReceriveNewMsgs` 是通知，不等于当前可见会话切换，见 `QN.cs:444-465`。仅监听 `receiveNewMsg` 会漏掉恢复；把后台通知当正文又会误判。

## 2. Session 生命周期

| 层 | 创建/更新/结束 | Observer 含义 |
| --- | --- | --- |
| WebSocket/CDP 页面会话 | `MyWebSocketServer.cs:328-380` 识别卖家、认领权威页面通道并绑定 `QN.CDP`；`536-562` 断开释放。`CDPClient.cs:142-185` 使客户端失效。 | `WebSocketSession.SessionID` 是连接 ID，不是买家 SessionId；重复页面可能只提供入站证据。 |
| 当前可见会话 | `CDPClient.cs:524-551` 处理 `onConversationChange`；`QN.cs:903-919` 更新 `Buyer` 并发 `EvBuyerSwitched`。初始化/状态路径也调用 `SetActiveConversationByNick`，见 `MyWebSocketServer.cs:318,364-369`、`QN.cs:391-436`。 | `QN.Buyer` 是可变 UI 指针。后台或延迟消息须用消息自身买家字段；昵称切换不是平台身份凭据。 |
| 买家业务生命周期 | `QN.cs:580-591` 入聚合器；`BuyerMessageBurstCoordinator.cs:544-571` 调用 `BuyerSessionAgent.ObserveBuyerMessage`、分配 generation 并转 `Coalescing`；`BuyerSessionAgent.cs:124-215` 维护 seller+buyer 状态；聚合器 `:772` 清理完成后闲置状态。 | generation 是进程内工作代数，不是千牛会话码或 Agent `SessionId`；新消息可使旧草稿失效。 |

`BuyerSessionAgentRuntimeBridge.cs:217-355` 另订阅原始消息，记录买家、卖家、订单、撤回等事件。它发生在普通处理之前，可记录不进入回复链的事件，不能充当“安全已通过”的旁路证明。`BuyerSessionAgent.cs:116-121` 使用进程内静态状态；另造 Agent/协调器不等于独立生产 Session。

## 3. 身份确认位置

| 证据 | 当前可确认 | 不能推出 |
| --- | --- | --- |
| `QNChatMessage.fromid/toid/loginid` 的 `nick`、`targetId`、`cid.ccode` | `ChatResponse.cs:18-45,165-184` 保存原生字段；`QN.cs:379-388,519-523` 用昵称和当前卖家判方向。 | 昵称分类或字段非空不证明同店铺、同会话的唯一平台买家。 |
| 原生键 `mcode.clientId/messageId`、`ext.ww_msgid` | `IncomingMessageSafety.cs:359-380` 优先用它们去重，缺失时退回会话/昵称/时间/正文组合。 | 回退键不是平台消息 ID；被业务 ledger 接受也不是身份核验。 |
| 当前会话快照 | `CDPClient.cs:781-783` 调用 `im.uiutil.GetCurrentConversationID`；`src/DbEntity/Response/LocalUser.cs:30-49` 可含 `Nick/Display/Ccode/TargetId`。 | 普通入站代码没有把消息 `targetId/ccode` 与同刻快照做原子核对；切换和异步延迟可错位。 |
| `BuyerIdentityAliasService` | `BuyerIdentityAliasService.cs:29-109` 在店铺作用域记 internal nick、display、targetId 并判断别名等价。 | 观察累积的内存映射不是独立权威认证；`AreEquivalent` 不升级 `Verified`。 |
| qnocr/Bridge 本地字段 | `Extension/AgentBridge/AgentEventEnvelope.cs` 的 v1 只准 `Observed`；`Extension/IAgentExtension.cs` 有原生证据接口字段。 | 本地 `SessionId`、状态字符串或哈希不是平台凭据；目前没有实时同会话核验链。 |

后续核验至少需要：已登录卖家和店铺作用域、原生方向、来源可追溯且非空的买家 `targetId`、消息 `ccode`、稳定原生消息 ID、同一运行实例会话快照及采集时间、切换前后的稳定关系。任何一项缺失或冲突均保持 `Observed/Held`。卖家回显、平台系统消息、历史/重复/恢复重放不得升级为新买家消息。此为评审门槛，不是当前已实现的验证。

## 4. 商品上下文来源

`QNChatMessage.originalData.actionUrl/text/url` 可含商品链接；`OriginalData.itemId` 是从 URL 派生的只读属性，见 `ChatResponse.cs:58-87`。`QnProductIdentityAdapter.cs:23-36,118-135` 严格解析当前 burst 链接；无链接时，`:39-88` 仅在本 burst 的 `fromid.targetId` 恰有一个值时调用 `qn.GetItemRecords`，只取 `underInquiryItemList`。最近购买和足迹属于历史，不作为当前商品。

`ProductIdentityResolver.cs:11-51` 对当前消息链接、当前咨询单一商品、显式上下文依次决策，多商品冲突为 `Ambiguous`，无证据为 `Unknown`。`ProductBindingService.cs:17-67` 再按 `ShopKey` 和元数据映射内部 `ProductId`；外部 itemId、SKU、内部 ProductId 不可混用。运行时解析发生在 `BuyerStreamingReplyPipeline.cs:131-164` 后续回复链（部分只读 Shadow 观察还要求 Auto Reply 关闭），不在原始事件。故 V1.32.1 的 `ProductId` 应为空，不能从聊天名称、历史卡片、当前 UI 选中商品或别的 Session 借值。

## 5. 可安全插入的位置

| 候选 | 能获得什么 | 评审结果 |
| --- | --- | --- |
| `QN.EvRecieveNewMessage` / `CDPClient.EvRecieveNewMessage` | 原始 JSON、原生字段候选；现成订阅接口。 | **仅适合未核验诊断。**先于安全层；回调同步，必须短且隔离异常。不能称为已安全接受的买家消息或直接交给 Agent AI。 |
| `BuyerSessionAgentRuntimeBridge` / `BuyerIdentityAliasRuntimeBridge` | 原始事件分类、昵称别名。 | 仍是前置观察；其内存状态不是跨进程身份凭据。 |
| `QN.ProcessIncomingMessageAsync` 的安全判断之后、`Enqueue` 之前 | 普通文本已有方向、非买家、业务去重、订单分流和安全/视觉结论。 | **概念上首选后置缝隙，但当前无事件。**未来若获准新增只读事件，只传不可变快照，隔离异常/耗时；须说明订单、媒体、Robot OFF 覆盖。观察不得改变 `Enqueue`。 |
| `BuyerMessageBurstCoordinator.Enqueue`、合并前规则、最终 handler | 本地 generation、合并问题。 | 不作为 Observer 入口。调用或替换会影响规则、AI、发送与静态 Session；合并前固定规则可能已回复。 |
| `BuyerStreamingReplyPipeline` 商品解析/Shadow | 后续商品身份和回复上下文。 | 太晚且与 AI/回复链耦合，不能为原始事件补出历史时刻的可信商品。 |

**本阶段架构决定：**保留 V1.32.1 的独立 Named Pipe 模拟边界；不挂真实 qnbot，不把原始订阅标成 `Verified`，不改 `MessageEnvelope` 或 Message/AI/Send Pipeline。下一阶段先以匿名只读真实事件和同会话快照证实字段、时序，再单独评审后置事件和协议扩展。即使取得后置事件，自动发送仍冻结。

## 验证边界

本轮只读检查 qnbot 源码及 Git 状态；没有运行 qnbot、千牛、AI、IPC 或发送测试。新增物仅为本分析文档。V1.32.1 的通信成功、AI 调用 0、SentMessages 0 是前一阶段模拟验证，不能证明本阶段已观察真实安全买家消息。
