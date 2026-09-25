# qnocr → qnbot V1.0 消息契约兼容性分析

分析日期：2026-09-25。qnocr输入基线为 `2572529fdc77687720dd460ec40c23108bb663aa`。
只读分析仓库：`D:/CodexWork/qnagent/qnbot`；实际HEAD为
`4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，开始及结束时工作区均干净。
以本次代码和HEAD为准，不把交接文档中的旧发布基线当作当前代码版本。

## 结论

**文本模型及聚合器入口有条件兼容，不能把现有Envelope原样接入正在运行的qnbot。**
已在独立.NET Framework 4.8进程中，将Envelope转换成真实qnbot类型，执行未经改动的
`BuyerMessageBurstCoordinator.Enqueue`，由真实会话状态机接收、合并并派发给只记录数据的回调。
两条输入产生两个generation，重复重放没有第二次派发，禁止的业务/发送调用为0。

这证明的是类型转换及隔离聚合器入口，不是生产Bot、SalesAgent、Knowledge V2、Prompt或Reply Chain联调。
空身份、昵称映射、时间/历史策略、可靠消息身份和商品上下文仍有缺口；本版没有自动补造或绕过这些缺口。

## 1. 当前qnbot消息模型及入口

### 实际类型

在本次 `src` C#代码中未找到名为 `BuyerMessage` 的独立类/记录/接口。
任务中的“qnbot BuyerMessage”实际对应两层：

1. [QNChatMessage](D:/CodexWork/qnagent/qnbot/src/Bot/ChatRecord/ChatResponse.cs:165)：传输/聊天记录模型。
2. [BuyerMessageBurstItem](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs:14)：聚合器内部入队对象。

`QNChatMessage`重要字段：

| 字段 | 当前作用 |
| --- | --- |
| fromid / toid / loginid | 含nick、targetId等；买家方向由发送/接收昵称与当前卖家昵称判定 |
| originalData.text / summary | 正文；MessageText还可能拼接header.summary |
| sendTime / sortTimeMicrosecond | 字符串时间值，安全层解析Unix秒/毫秒/微秒或日期格式 |
| mcode.clientId / messageId、ext.ww_msgid | 优先用于去重身份 |
| cid.ccode | 原生会话标识；无原生ID时不能伪称真实会话码 |
| templateId、fileId、url、actionUrl | 平台模板/媒体/卡片信息，OCR文本不具备完整数据 |
| originalData.itemId | **只读派生属性**，从actionUrl/text中的商品URL提取，不是可赋值字段 |

`BuyerMessageBurstItem`重要字段：SellerNick、BuyerNick、MessageKey、DisplayText、Message、
SafetyDecision、VisionDecision、SortValue、ReceivedAt、SessionGeneration。
SessionGeneration由协调器的会话状态机填写，Adapter不自行分配。

### 输入参数与可见性

```csharp
// 当前qnbot内部API（这些类型不是对外公共接口）
BuyerMessageBurstCoordinator(Func<BuyerMessageBurstLease, Task> handler)
void Enqueue(BuyerMessageBurstItem item)
```

参见 [构造器和Enqueue](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs:535)。
Enqueue对空item或空SellerNick/BuyerNick直接返回；随后进入ObserveBuyerMessage，去重、生成generation和入队。
源码通过 [Directory.Build.targets](D:/CodexWork/qnagent/qnbot/src/Directory.Build.targets:121) 纳入项目，
不能仅凭Bot.csproj显式Compile列表判断是否使用了它。

### 代码层消息来源

```text
千牛/IMSDK相关浏览器事件 → WebSocket消息
  → CDPClient.DispatchInboundEvent("receiveNewMsg", response)
  → EvRecieveNewMessage(RecieveNewMessageEventArgs.Message = JSON字符串)
  → QN.Cdp_EvRecieveNewMessage
  → JsonConvert.DeserializeObject<ChatResponse>，其中result是QNChatMessage列表
  → 按GetSortValue排序 → QN.ProcessIncomingMessageAsync
  → BuyerMessageBurstCoordinator.Enqueue
  → QN.ProcessBuyerBurstAsync / 后续处理链
```

入口证据：[WebSocket转发](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/MyWebSocketServer.cs:519)、
[CDP事件分发](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/CDPClient.cs:284)、
[事件参数](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/CDPEventArgs.cs:9)、
[JSON解析及逐条处理](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:853)。

另有 [消息恢复入口](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.MessageRecovery.cs:391)，
复用普通处理入口/已处理去重账本及聚合器。
[NativeInboundMessage](D:/CodexWork/qnagent/qnbot/src/Bot/NativeInbound/NativeInboundMessage.cs:12)
是Shadow观察DTO，保留指纹/身份状态而不保留聊天正文，并不是现成的文本BuyerMessage入口。
这些是代码支持的路径，本次没有连接运行实例验证其当前启用状态。

### 入口不是无副作用函数

[ProcessIncomingMessageAsync](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:487)
包含非买家过滤、身份别名观察、去重、订单回复计划、安全/视觉判定、Robot启用门控等。
事件订阅者还在普通循环前得到事件。因此不能为了“测试兼容”直接向真实receiveNewMsg入口注入历史OCR数据。

聚合器内部也有[合并前固定回复策略](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs:243)，
可能在最终handler之前执行固定回复。只替换最终handler并不能保证安全。
本次隔离宿主同时关闭Robot策略、设置非回复判定，并从程序集层面排除真实发送/AI实现。

## 2. MessageEnvelope与qnbot差异

| OCR契约 | qnbot要求/行为 | V1.0处理 |
| --- | --- | --- |
| Source=Qianniu.OCR | 原生模型没有相同来源字段 | 使用显式qnocr离线mcode命名空间，旁路保留来源语义 |
| SellerId / BuyerId可能为空 | 聚合器必须有SellerNick / BuyerNick，普通入口还需匹配当前卖家 | 要求显式ID→昵称映射，空/不匹配ID拒绝 |
| ID是调用方定义的字符串 | nick、display、平台targetId是不同概念 | 不把ID当昵称；targetId仅取映射中另行提供的值，未知为空 |
| Messages[] | 普通入口逐条处理QNChatMessage，聚合器收单条BurstItem | 一个Envelope拆成多个真实QNChatMessage及BurstItem |
| Role=buyer | 普通入口根据fromid/toid与当前卖家判方向 | 按显式映射填fromid/toid/loginid；只接受buyer文本 |
| Text | originalData.text、summary、DisplayText | 原样赋值，不注入商品链接或提示文本 |
| Timestamp无时区时为显示时间 | sendTime供历史过滤，SortValue通常是解析后的本地Ticks | 显式传入显示时区；转换epoch字符串，再调用实际GetSortValue |
| 没有接收时间 | ReceivedAt是发现/接收时刻，参与合并 | 单独传入ReceivedAt，不覆盖源发送时间 |
| 没有平台消息ID | mcode/ww_msgid优先去重 | 生成明确标记的合成键；只能验证相同重放，不声称原生跨入口去重 |
| ProductId / ProductBinding是上下文 | itemId只读；商品身份桥主要解析实际URL/查询记录 | 保留在Adapter旁路结果，不拼造actionUrl，不写只读itemId |
| 快照包含历史消息 | 启动前8秒之外的旧消息会被正常安全层拦截 | 保留历史时间并验证其被拦截，不把时间改成现在 |
| 文本及顺序原样保存 | 聚合器会排序、空白归一化、合并近似连续文本，并限制合并长度 | 记录语义差异，不改核心合并规则 |
| .NET8、现代C# | Bot目标.NET Framework4.8、C#7.3，聚合器是internal | 使用JSON边界及C#7.3 DTO；独立net48宿主验证，不ProjectReference .NET8程序 |

依据：[方向判定](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/QN.cs:379)、
[去重键](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/IncomingMessageSafety.cs:359)、
[排序时间](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/IncomingMessageSafety.cs:383)、
[历史过滤](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/IncomingMessageSafety.cs:278)、
[商品只读属性](D:/CodexWork/qnagent/qnbot/src/Bot/ChatRecord/ChatResponse.cs:72)、
[现有商品身份桥](D:/CodexWork/qnagent/qnbot/src/Bot/SalesAgent/Products/QnProductIdentityAdapter.cs:23)。

合并细节见 [BuildCombinedQuestion](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs:99)：
相邻文本可能被去重/补全，结果保留末10段、最多1600字符；队列缓冲另有12项限制。
本次两条短文本没有触及这些限制，不能把结果推广成任意批次无损兼容。

## 3. 最小Adapter方案

`Integration/QnocrMessageAdapter.cs`只做转换，不主动查找会话、启动Bot、接收网络事件或发送消息。
`Integration/MessageEnvelopeWire.cs`是同一v1 JSON契约的.NET4.8/C#7.3解码DTO，
不是伪造的qnbot消息类型；输出的QNChatMessage/BurstItem来自原始qnbot源码。

转换需要：Envelope、显式身份映射、显示时区、接收时间、稳定的离线重放scope。
本次使用China Standard Time作为明确的测试时区；没有时区的显示时间不擅自视为UTC。
夏令时无效/歧义时间拒绝转换；有偏移或UTC的时间按原时刻处理。

消息key由scope、Source、源身份、源UTC时间、原文及同批次相同事件出现序号生成。
ProductId不参与key，避免只因可见商品切换而制造“新消息”。
这种合成key不是平台messageId：OCR修订、窗口裁剪、同秒同文等仍可能造成重复或碰撞，
且与CDP原生key不同，真实跨入口去重未解决。

`templateId=0`表示未知；不伪造平台模板、媒体URL、真实会话码或原生消息ID。
`ProductIdContext`及ProductBinding作为转换结果旁路字段保存，当前**没有自动进入SalesAgent/Knowledge V2商品解析**。

本PoC仅返回OfflineCompatibilityOnly对象，ShouldCallAi=false、VisionDecision=Skip。
未来进入实际业务链时必须使用真实入口的安全判定，不能把这些离线标记当成生产授权。

## 4. 模拟入口及实际证明

验证项目：`Validation/QnbotCompatibility/QnbotCompatibility.csproj`，目标net48，语言版本7.3。
准备脚本只读qnbot，要求工作区干净、HEAD及6个源码SHA256与已分析基线一致，
将逐字节相同的源码复制到本项目`.tools/qnbot-compat-source`；源码副本不提交。
基线清单为 `Validation/QnbotCompatibility/qnbot-source-baseline.json`，构建嵌入清单并在运行时核对副本。

**原样编译**：ChatResponse、BuyerMessageBurstCoordinator（含其前置策略代码）、BuyerSessionAgent、
IncomingMessageSafety、VisionMessageDecision、RegexEx。
**隔离替身**：运行时UI初始化、日志/上下文设施、店铺配置及外部业务依赖，详见OfflineRuntime.cs。
Robot始终关闭；可能触及AI/发送/知识库/业务的替身方法抛错并计数。没有引用或启动Bot.exe。
handler只记录Burst，不是原来的QN.ProcessBuyerBurstAsync。因而本测试没有执行完整正常入站路径。

```powershell
./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot
```

默认输入为提交的匿名合成 `message_envelope.json` 和 `identity_mapping.json`。
也实测了已有V0.9、带显式测试身份的Envelope；未连接真实千牛。

| 验证项 | 结果 |
| --- | --- |
| 宿主框架 | .NETFramework,Version=v4.8 |
| 实际目标消息类型 | Bot.ChatRecord.QNChatMessage |
| 实际协调器类型 | Bot.ChromeNs.BuyerMessageBurstCoordinator |
| 入队并到达记录回调 | 2条 |
| 实际会话generation | 1、2 |
| 同一Envelope重复重放后的回调总数 | 1 |
| 禁止的业务/AI/发送调用 | 0 |
| 实际历史安全检查ShouldCallAi | false、false |
| 原生派生商品ID | 空；没有为了匹配而拼造商品URL |
| 原始空身份Envelope | 拒绝，未创建协调器/进入队列 |

转换对象：`Output/Integration/run_20260925_101110_864/converted-messages.json`；
同目录 `qnbot-messages.json` 单独保存真实QNChatMessage对象数组。
结果：同目录 `compatibility-result.json`，其中包含基线commit、6份源码验证、17项Adapter检查结果。
已有V0.9样例结果：`Output/Integration/v09-envelope/compatibility-result.json`。
空身份失败日志：`Output/v10-empty-identity.log`。

qnocr主项目及原回归：0警告、0错误，148项通过。
net48兼容宿主：0错误、1个原有源码警告CS4014（Coordinator第744行的未等待continuation）。
该警告没有通过修改或屏蔽原始源码消除；新代码其余警告按错误处理。

## 5. 边界及下一阶段前置条件

没有修改SalesAgent、Knowledge V2、Prompt、Reply Chain或任何qnbot工作区文件。
结束时qnbot仍处于相同HEAD且工作区干净，6份引用源码哈希一致；证据在 `Output/v10-qnbot-integrity.json`。

下一阶段之前仍需明确：真实身份映射/当前会话验证、历史快照与新消息的区分、原生/OCR跨源去重、
商品上下文的合法接入位置、Shadow策略和生命周期隔离。
[BuyerSessionAgent使用进程内静态状态](D:/CodexWork/qnagent/qnbot/src/Bot/ChromeNs/BuyerSessionAgent.cs:116)，
因此不能假设“在运行Bot内再new一个记录型协调器”就与生产会话完全隔离。
普通入口及合并前策略可能发送固定回复，也不能仅替换最终AI handler作为Shadow保护。

本次结论到**独立进程、受控依赖下的真实聚合器入口兼容**为止。
未安装实际入口桥、未启用真实Shadow、未验证AI回复或自动发送，完成V1.0后停止。
