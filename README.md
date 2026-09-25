# qnocr-poc V1.0

独立的千牛视觉采集实验：窗口捕获 → 布局裁剪 → OCR → BuyerMessage → 统一MessageEnvelope。
主程序使用 C# / .NET 8 / Windows；V1.0增加独立.NET Framework 4.8离线兼容性宿主，不修改qnbot。

## V1.0 qnbot兼容性验证

接口分析见 [docs/QNBOT_MESSAGE_CONTRACT_ANALYSIS.md](docs/QNBOT_MESSAGE_CONTRACT_ANALYSIS.md)。
实际qnbot类型是 `QNChatMessage` 与内部的 `BuyerMessageBurstItem`，并非公开的BuyerMessage类。
`Integration/QnocrMessageAdapter.cs` 将统一Envelope转换成这些真实类型；
`Integration/MessageEnvelopeWire.cs` 是v1 JSON的C#7.3解码DTO，解决.NET8与.NET4.8之间的程序集边界。
Integration源码仅编入独立验证宿主，不注入主POC或真实Bot进程。

```powershell
# 需Windows上的.NET Framework4.8运行时及引用程序集/Developer Pack
./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot

# 可替换为已有V0.9 Envelope；身份必须与显式映射相符
./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot -EnvelopePath 输入.json -IdentityMapping 映射.json
```

默认输入 `Validation/QnbotCompatibility/message_envelope.json` 与身份映射均为匿名合成样例。
准备脚本校验qnbot干净工作区、固定commit及源码SHA256，复制6份未修改源码到本项目`.tools`并原样编译。
遇到不同qnbot版本会停止，需重新分析后显式更新基线清单。
宿主执行真实协调器/会话状态机，外围UI/配置/业务依赖替换为受控测试实现，潜在AI/发送调用会抛错并计数。
不得把 `OfflineRuntime.cs` 放入真实qnbot，它只用于隔离测试。

已验证2条消息经Enqueue进入记录回调，generation为1、2，重复重放未再次派发，禁止业务调用为0。
输出位于脚本打印的 `Output/Integration/run_*`：`qnbot-messages.json` 是原生对象，
`converted-messages.json` 含BurstItem和上下文旁路信息，`compatibility-result.json` 是验收摘要。

必须明确的限制：

- SellerId/BuyerId不能猜成昵称或平台targetId；要求显式映射。原始空身份Envelope会被拒绝。
- 无偏移的源时间需显式显示时区。本宿主测试时区为China Standard Time，不改旧消息时间来绕过历史过滤。
- ProductId保留为旁路上下文，不伪造商品URL，不直接写入只读originalData.itemId，未贯通商品知识链。
- 合成消息键只证明相同离线重放去重，不等于解决原生/OCR跨入口去重或新消息确认。
- 输出固定为OfflineCompatibilityOnly，ShouldCallAi=false、VisionDecision=Skip。
- 兼容宿主没有运行完整普通入口、SalesAgent、Knowledge V2、Prompt或Reply Chain，也未连接真实千牛。

因此结论是**离线聚合器入口有条件兼容**，不是已经部署的Shadow或真实业务集成。

## V0.9 Message Adapter

新增 `Adapter/IMessageSource.cs`、`Adapter/OcrMessageSource.cs` 和 `Models/MessageEnvelope.cs`。
统一接口只依赖消息契约和.NET基础类型，不向消费方暴露OCR、截图或UIA对象：

```csharp
public interface IMessageSource
{
    string Source { get; }
    IAsyncEnumerable<MessageEnvelope> ReadAsync(CancellationToken cancellationToken = default);
}

IMessageSource source = new OcrMessageSource(
    cancellationToken => Task.FromResult(confirmedBatch), sellerId: "", buyerId: "");
await foreach (var envelope in source.ReadAsync(cancellationToken))
{
    string json = JsonSerializer.Serialize(envelope); // 本阶段仅本地验证/保存
}
```

OcrMessageSource每次枚举调用一次批次提供方，只转换`BuyerMessageBatch.Messages`，不转换ReviewItems。
它不启动OCR或持续采集；默认流程与`--messages`在结构化后自动调用Adapter。
已保存的V0.8结果可以离线转换，无需打开千牛或再次运行OCR：

```powershell
dotnet run -- --adapt Output/Messages/buyer_messages_20260925_091445_498.json
dotnet run -- --adapt 消息批次.json --seller-id seller-test --buyer-id buyer-test
```

第二条的身份仅为示例。真实ID只能由调用方提供，缺失时为空，不从昵称、窗口标题、OCR或历史批次猜测。
空ID表示未知，不是可用于合并会话的匿名ID。

契约v1（与应用版本V0.9不同）：

```json
{
  "Source": "Qianniu.OCR",
  "SellerId": "",
  "BuyerId": "",
  "ProductId": "1051087533910",
  "Messages": [
    { "Role": "buyer", "Text": "这个可以刻字吗", "Timestamp": "2026-09-24T19:51:45" }
  ],
  "SchemaVersion": 1,
  "ProductBinding": "ExplicitCallerContextNotVerifiedMessageAssociation"
}
```

保留Timestamp以免丢失V0.8显示时间，增加SchemaVersion和ProductBinding说明版本及商品上下文来源。
Unspecified时间不附加Z或推断时区；ProductId只代表上下文，不证明历史消息的商品归属。
供评审使用的JSON Schema在 `Adapter/message-envelope.schema.json`。

- 同一连续商品ID放入一个Envelope，商品ID改变就分段，包括空ID；A→未知→B→A输出四个事件，保持顺序。
- 文本、空格、换行、时间、重复消息均保留，不做纠错、重新排序、去重或新消息断言。
- 整批校验并复制快照后才输出；非法角色/来源、空正文、默认时间、非法OCR商品ID使整批失败。
  输入应来自可信qnocr批次；字段校验不是来源认证，也不会重新核验图像证据。
- 取消在提供方调用前/后及每个事件输出前检查；提供方须配合CancellationToken。
  已输出事件不会因取消而撤回，不承诺原子投递。
- 每次重新枚举会重新调用提供方，没有持久游标、重试、去重、ACK或消息队列。
- 空批次不产生空事件；ReviewItems、图像路径、OCR原始结果和定位证据不进入统一消息体。

每个Envelope单独写入 `Output/Adapter/message_envelope_<批次编号>_<序号>.json`，
先写同目录临时文件再改名；这些是验收产物，不是已部署的队列或qnbot收件箱。
契约代码仍在POC程序集内，未来可独立抽出。V0.9阶段仅验证IMessageSource与独立JSON消费DTO；
V1.0补充了上述只读代码分析和独立进程兼容验证，仍未修改或调用运行中的qnbot，
**没有证明可直接接通其完整生产入口**。
详见 [VALIDATION_V09.md](VALIDATION_V09.md)。

## V0.8 买家消息结构化

新增 `Models/BuyerMessage.cs`，消息对象包含五个字段：

```csharp
string Role;       // 当前仅输出 "buyer"
string Text;       // 气泡内OCR文本，多框按位置排序并用换行连接
DateTime Timestamp;
string Source;     // "Qianniu.PaddleOCR"
string ProductId;  // 未知时为 ""
```

`Messages/BuyerMessageStructurer.cs` 使用用户确认的保守规则：
只输出具有买家气泡、可靠正文及完整日期时间证据的消息，其他内容保留为待确认记录。

```powershell
# 单张完整聊天区域：OCR并结构化，不读取历史商品上下文
dotnet run -- --messages Output/Regions/chat_region.png

# 调用者显式提供商品上下文（不等于确认消息真实涉及该商品）
dotnet run -- --messages Output/Regions/chat_region.png --product-id 1051087533910

# 采集同一完整窗口的聊天/商品区域后生成消息
dotnet run
```

- 面向当前浅色千牛布局：左侧白色气泡作为买家候选，右侧/蓝色气泡不会直接标为买家。
  以背景色、轮廓、相对位置和OCR框关系筛选，属于可核验的启发式规则，不是账户身份认证。
- 同一气泡内按OCR框位置合并文字，不跨气泡合并、不按文本内容去重，不自动修复OCR错误。
- 要求正文和时间/昵称支持行置信度≥0.80；正文中任一已识别行低分，则整个气泡进入待确认。
- 时间必须来自上方邻近灰色昵称/时间行，可合并框或左右分开的框。
  仅接受有效的完整年月日时分秒；缺失、仅时分、相对日期、冲突时间均不使用采集时间代替。
- `Timestamp.Kind=Unspecified`，保留截图显示时间，不推断时区；即使截图是昨天的消息也不会改成今天。
- 截断气泡、表情/标点单独成文、无法确定角色或头部的内容进入ReviewItems。
  未使用的OCR行（可能是昵称、状态、客服文字或图形误识别）也保留为待确认，不混进BuyerMessage。
- 默认流程的商品ID来自同一截图右侧的可靠识别结果；缺失/低分/多ID时为空，不从以前的结果文件补值。
  该字段表示采集时可见商品上下文，**不证明历史消息属于这个商品**。独立命令只接受显式传入的ID。
- 每次输出到 `Output/Messages/buyer_messages_<时间>.json`，包含Messages、逐条Evidence、ReviewItems、
  输入图路径/SHA256、时间含义、ProductBinding。证据记录气泡坐标、OCR行号和最低支持分数。
- 现有 `--watch` / `--compare` 仍做变化区域OCR，未接入结构化；裁剪变化横带可能不包含完整消息头。
  本版不做跨帧拼接、会话记忆、新消息确认、自动回复或发送。

默认采集现在仅选择可见、非最小化的接待中心，防止其最小化时退回工作台并把经营数据当聊天输入。
离线图片命令仍由调用者保证传入正确的聊天区域；其他主题/布局、图片消息和富卡片尚未全面验证。
完整验证见 [VALIDATION_V08.md](VALIDATION_V08.md)。

## V0.7 商品上下文

新增 `Product/ProductIdExtractor.cs`、`Product/ProductContextResolver.cs`、
`Models/ProductContext.cs` 和 `Models/ProductProfile.cs`。
用户已选择本地测试映射；默认数据为 `Product/product-profiles.sample.json`，
Catalog与Profile均显式标记测试数据，不包含真实规格、库存或价格，也不连接qnbot/Knowledge V2。

```powershell
# 对已有右侧商品区域执行OCR、提取ID、查询随程序提供的测试映射
dotnet run -- --product Output/Regions/product_region.png

# 显式指定本地JSON映射表
dotnet run -- --product Output/Regions/product_region.png --profiles Product/product-profiles.sample.json

# 默认完整流程还会在商品OCR后执行V0.8买家消息结构化
dotnet run
```

原 `--ocr` 仍只识别文字，`--layout` / `--extract` 不进行商品匹配，`--watch` 仍仅监测聊天变化，
没有新增循环商品OCR。默认映射文件从程序输出目录读取，构建/发布时自动复制，可独立部署。

识别规则：

- 必须有ID类标签，支持 ID / 商品ID / 宝贝ID / 产品ID、相应“编号”标签及item_id/itemid。
- 本POC接受连续10–16位ASCII数字，保持字符串（保留前导零）；此范围是可调整的POC策略，不是完整平台ID规范。
- 只归一化全角ASCII/空格；不把O猜成0、不把I猜成1，不拼接数字内部的空格。
- 支持标签和纯数字分为两个OCR框，但必须同一行、右侧相邻；不跨行关联搜索框与号码。
- 忽略订单/交易/用户/店铺/SKU等其他编号上下文、未带标签的长数字、搜索框占位提示、价格/库存。
  这是保守规则，不能识别任意文案的业务语义。
- 默认最低OCR证据分数0.80。完整行使用该行分数，分框标签和值取较低分数；无分数/无效分数不匹配。
- 相同ID去重，采用最高分的支持证据，不累加分数。多个不同ID返回Ambiguous，即使只有其中一个在目录中也不自动选它。
- 只做精确ID查表，不模糊匹配、不用商品标题猜ID，不把Profile命中当作OCR正确性证明。

状态：Matched、Unmapped、NotFound、LowConfidence、Ambiguous。
后面三种状态的ProductId不作为确定结果输出，候选及证据保留在Extraction中；
Unmapped保留已识别ID但Profile=null。Confidence是支持该ID的OCR行分数，不是准确率或知识库匹配概率。

JSON映射格式如下，字段严格校验，重复ID、错误版本、非法ID和不一致的测试标记会报错：

```json
{
  "SchemaVersion": 1,
  "IsTestCatalog": true,
  "Profiles": [
    {
      "ProductId": "1051087533910",
      "ProfileKey": "poc-sample-1051087533910",
      "DisplayName": "POC测试映射，非真实商品知识",
      "IsTestData": true,
      "Attributes": { "Purpose": "仅验证精确ID映射" }
    }
  ]
}
```

结果保存到 `Output/Product/product_context_<时间>.json`，包含输入路径、Context和原始OCR数据；
原OCR TXT/JSON继续写入 `Output/OCR`。详细实测见 [VALIDATION_V07.md](VALIDATION_V07.md)。
识别到唯一可见ID不代表确认了买家正在咨询的商品；未展示的卡片、分页或遮挡内容不会被自动读取。

## V0.6 布局解析与诊断

新增 `Layout/QianniuLayoutResolver.cs` 和 `Models/ResolvedLayout.cs`。
分别输出 BuyerList / Chat / Product / Input 的 Bounds、Source、Reason；
每个区域按 **UIA → Visual → RatioFallback** 选择。结果用于默认流程、`--extract` 和 `--watch`。

```powershell
# 当前窗口：截图、解析布局、输出四个区域，不执行OCR
dotnet run -- --layout

# 仅查看当前接待中心的UIA几何诊断，不截图、不执行OCR
dotnet run -- --uia-geometry

# 现有图片没有可核验的实时HWND：视觉定位 → 比例回退
dotnet run -- --extract Output/qianniu_20260924_225736.png
```

UIA实现限制在选定千牛HWND的树中，仅请求 `ControlType`、`BoundingRectangle` 和 `IsOffscreen`。
只保留 Pane / Document / Window / Group 的可见有效边界，不读取 Name、Value、TextPattern、
AutomationId或聊天文本，不请求控件模式、不聚焦、不点击。非布局控件（包括Text/Edit）的子树不继续遍历。
屏幕物理坐标减去同一窗口的GetWindowRect原点，转换为截图坐标；探测期间窗口移动、大小或所有者变化时拒绝该次几何数据。
UIA以几何相似性推测区域，不依靠聊天内容或语义标签；比例模型用于限定候选搜索范围。

探测在独立MTA后台线程进行，最多512个已访问节点、10层、约1200ms遍历预算，
调用方最多等待1800ms。原生调用卡住时不会强行终止线程；同一进程最多保留一个未完成探测，
后续调用先回退，避免无限创建卡住的线程。预算截断时保留已取得的几何节点，诊断标记Complete=false。
已取得的节点仍须通过坐标/相似度/歧义检查才能使用，不将有限遍历描述为完整UIA树。
近似重复的CEF宿主矩形（边缘差不超过3px）去重，明显不同且得分接近的候选不强行选中。

视觉定位不读取/识别文字：在比例先验附近寻找长垂直背景边界、标题分隔线、消息/输入分隔线和右侧底栏边界。
仅针对当前浅色主题；缺乏长边界或布局关系不合理时回退。右侧底栏缺失不会取消已可靠定位的聊天区，
该区域单独尝试UIA或比例回退。最终校验区域在图内、相互位置一致；冲突时降级并记录原因。

布局日志输出至Console及 `Output/Layout/layout_<时间>_<唯一编号>.json`，
包含截图路径/尺寸、每个区域来源与坐标、UIA几何节点及完整性状态、视觉边界覆盖率、回退原因、总耗时。
监测中的完整截图是临时文件，诊断所引用的该源图会在裁剪后删除；离线提取及默认单次截图仍保留原图。
`--uia-geometry` 写 `Output/Layout/uia_geometry.json`，即使UIA不可用也正常输出诊断状态。

实测示例（1638×949客服页）：

```text
[Layout] BuyerList: Visual Bounds={X=80,Y=120,Width=300,Height=829}
[Layout] Chat: Visual Bounds={X=380,Y=181,Width=648,Height=447}
[Layout] Product: UIA Bounds={X=1035,Y=120,Width=600,Height=827}
[Layout] Input: Visual Bounds={X=380,Y=628,Width=648,Height=321}
```

此样本聊天图中的标题栏残留已消除；不是对所有千牛版本、皮肤、DPI、面板宽度和遮挡状态的保证。
完整报告见 [VALIDATION_V06.md](VALIDATION_V06.md)。
实现依据：[Microsoft UIA客户端概述](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-clientsoverview)、
[Microsoft UIA线程说明](https://learn.microsoft.com/uk-ua/windows/win32/winauto/uiauto-threading)。

## V0.4 变化检测模式

新增 `Detection/ChatRegionChangeDetector.cs` 和 `Models/ChangeDetectionResult.cs`。
新的监测模式只在聊天图发生显著变化时提取变化区域并 OCR；默认不带参数的命令保留 V0.3 单次完整流程。

```powershell
# 持续监测，Ctrl+C 停止
dotnet run -- --watch

# 有限采样，含首帧基线；本次实测采用此模式，不遗留后台监测
dotnet run -- --watch --frames 3

# 两张聊天区域截图离线比较：有变化则裁剪并 OCR，无变化则跳过
dotnet run -- --compare previous_chat.png current_chat.png
```

监测绑定启动时找到的可见接待中心 HWND，每帧解析UIA/视觉/比例混合布局。
每次处理完成后等待 1000ms 再取下一帧，不并行运行 OCR，不承诺固定每秒帧数。
首帧只建基线，不识别历史；随后比较相邻成功帧，每帧处理后更新上一帧。
窗口关闭、最小化、捕获失败、无效布局或 OCR 失败时停止并报错，不自动切换到其他窗口。
窗口尺寸或解析出的聊天矩形位置/尺寸变化时重建基线；即使聊天裁剪图尺寸不变，也不比较不同位置的区域。
重建基线的那一帧不执行OCR，因此同帧到达的消息也不会被当作变化输出。
Ctrl+C 在当前不可取消的捕获/OCR 操作结束后停止；`--frames N` 按成功捕获帧数计数，N 必须大于 0。

像素差定义：对每个像素取 B/G/R 三通道绝对差的最大值，大于 **24** 的像素计入差异。
`DifferenceRatio = 差异像素数 / 全图像素数`，必须同时满足 **至少20像素** 和 **比例≥0.001** 才返回 Changed=true。
透明通道不参与比较，不缩放、不降采样。阈值可通过检测器构造参数调整，当前 CLI 使用上述默认值。
检测器返回 Changed、DifferenceRatio、ChangedRectangle、像素数、尺寸和耗时；
无显著变化时 ChangedRectangle=null，但仍报告实际差异比例。不同尺寸或无效图会抛错。

ChangedRectangle 为差异像素的最小包围框，相对聊天截图左上角。
实际 OCR 裁剪为包围框 Y 范围上下各扩12px、宽度覆盖整个聊天区域的横带，边界裁到图内，
以尽量保留完整文字行。精确差异框与 OCR 上下文框分开记录，不混淆两者。
OCR 文本框坐标相对裁剪图；加上 change.json 中 OcrBoundsOriginInChat 即可映射回聊天图。

变化样本保存到 `Output/Changes/change_*`（离线）或 `Output/Changes/session_*/change_*`（监测）：
`previous.png`、`current.png`、`changed_region.png`、`change.json`。
JSON 状态区分 PendingOcr / OcrCompleted / OcrFailed；OCR TXT/JSON 仍保存到 `Output/OCR`，路径打印在 Console。
无变化不保存事件图、不调用 OCR；监测只循环覆盖两张临时聊天帧，成功裁剪后删除临时完整窗口截图。
变化事件保留用于验收，长期运行时会占用磁盘；本版没有事件保留期限或后台服务。
每个监测会话还保存 summary.json，记录帧数、尺寸/定位变化导致的 BaselineResets、变化次数、OCR 调用次数及退出状态。

**像素变化不等于新消息。** 滚动、切换会话、头像/表情动画、已读状态都会触发；
同一横带的未变文字可能再次进入 OCR，多处分散变化会被一个包围框合并。
全局变化可能使横带覆盖整张聊天图，本版不尝试猜测哪部分属于新消息。
低于阈值的微小修改、逐帧渐变可能漏检；没有稳定帧去抖、滚动补偿、消息身份识别或文本去重。

验证命令及报告：

```powershell
dotnet run --project Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj
dotnet run -- --compare Output/ValidationV04/previous_message.png Output/ValidationV04/current_message.png
```

检查程序无额外测试框架依赖，生成确定性帧对并验证差异框、阈值、边界和 OCR 调用门控。
历史记录见 [VALIDATION_V04.md](VALIDATION_V04.md)；V0.5检查扩展了比例布局、像素一致性和尺寸切换回放。

## 运行

在 Windows 交互桌面安装 .NET 8 SDK，展开千牛接待中心，在项目根目录运行：

```powershell
dotnet run
```

本次验证机器原先只有 .NET 10 SDK，因此另下载了微软官方 .NET 8.0.425 SDK 到
本项目 `.tools/dotnet`，并按微软发布元数据核对 SHA512。使用本地 SDK 的终端先执行：

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\cli-home'
dotnet run
```

`.tools` 不进入 Git；其他机器需要自行安装 .NET 8 SDK。
必须在千牛所在用户的交互桌面运行；受限沙箱或服务会话可能无法枚举窗口。
本次沙箱内 EnumWindows 失败，在允许访问用户桌面的运行环境中成功。

默认 `dotnet run` 在捕获、裁剪成功后执行聊天OCR、商品上下文和V0.8买家消息结构化。
需要仅识别变化区域时使用上面的 `--watch` 或 `--compare`。
直接识别图片，无需千牛运行，也不要求固定尺寸：

```powershell
dotnet run -- --ocr Output/Regions/chat_region.png
```

首次运行需联网还原 NuGet 依赖；依赖及本地模型缓存到项目 `.tools/nuget`，
不使用 Python，不上传图片，也不调用外部 OCR/AI 接口。提交的 `packages.lock.json` 锁定依赖，
可用 `dotnet restore --locked-mode` 重建。平台为 Windows x64；原生运行库需要 Visual C++ x64 运行时。

也可以仅裁剪已有完整截图（该模式不执行 OCR）：

```powershell
dotnet run -- --extract Output/qianniu_20260924_221542.png
```

## PaddleOCR 单图识别

`OCR/PaddleOcrService.cs` 使用 PaddleSharp（社区 C# 封装）的本地 PP-OCRv5 中文模型，
调用 Paddle Inference CPU MKL。`Models/OcrResult.cs` 返回文本行、置信度、边界框及性能数据。
固定依赖：Sdcb.PaddleOCR / Models.Local 3.3.1，PaddleInference 3.3.1，
Windows MKL runtime 3.3.1.70，OpenCvSharp 托管/原生包 4.11.0.20250507。

- 每次 OCR 调用新建引擎，处理一张图后释放；V0.4 通过外层检测避免无变化时调用，引擎暂不复用。
- 截图中的水平文字关闭旋转/180°分类；其他方向和复杂文档布局不在本版验收范围。
- 输出按检测框中心 Y、X 排序，不推断买家/客服角色，不补全或修正模型输出。
- 舍弃模型返回的空文本框；无效分数转为 null，平均置信度仅计算有效分数。
- 空白图正常返回 `No text detected`、Confidence `N/A`，仍表示推理成功；不是识别出了文本。
- Confidence 是模型分数，不是准确率；昵称、头像/表情附近、标点和空格仍可能识别错误。
- Console 输出结果，另存 `Output/OCR/<图片名>_<时间>.txt` 与 `.json`；JSON 保存逐行分数、
  边界框、丢弃空框数和原始毫秒/字节指标。
- 时间区分模型初始化、检测+识别推理、总耗时（读图/解码+初始化+推理+结果整理），
  不含构建、模型下载、Console/结果文件写入和资源释放。
- 内存为当前进程的 OCR 前/后工作集和进程生命周期峰值（MiB），包含 .NET/原生库；
  不是托管堆大小，也不是 OCR 独占内存。测量“后”在引擎释放前。

可复现的中英数字/型号/容量/符号测试图：

```powershell
./Validation/Create-OcrFixtures.ps1
dotnet run -- --ocr Output/OCR/Fixtures/mixed_characters_18px.png
dotnet run -- --ocr Output/OCR/Fixtures/mixed_characters_28px.png
./Validation/Measure-OcrAccuracy.ps1 -OcrJson Output/OCR/实际结果.json -GroundTruthJson Output/OCR/Fixtures/mixed_characters_18px.ground-truth.json
```

夹具使用 Microsoft YaHei 字体，18px/28px；脚本同时保存准确的参考文本。
字符错误率使用 Levenshtein 编辑距离，分别报告保留空格和仅去除空白的口径，
保留大小写及标点差异。实测、错误样例与性能详见 [VALIDATION_V03.md](VALIDATION_V03.md)。

技术来源：[PaddleSharp OCR](https://github.com/sdcb/PaddleSharp/blob/master/docs/ocr.md)、
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)。

## 比例回退布局

`Models/QianniuLayoutRatio.cs` 定义 BuyerList / Chat / Product / Input 的 Left、Top、Width、Height 比例属性。
所有配置均在0..1范围内，生产布局不再包含固定像素矩形或硬编码截图尺寸。
比例模型的 `Resolve(Size)` 返回 `QianniuLayout`，后者承载像素矩形，没有默认固定坐标。
V0.6优先采用混合解析器的结果；比例配置继续作为搜索先验和最终Fallback，保留旧接口用于显式比例提取及回归检查。
`Capture/QianniuRegionExtractor.cs` 按解析结果裁剪原始像素，不拉伸或重采样原图。

默认比例由V0.2参考布局归一化得到；下表仅展示六位小数，源码保留更多有效位。

| 区域 | Left | Top | Width | Height | 输出 |
| --- | ---: | ---: | ---: | ---: | --- |
| BuyerList | 0.048840 | 0.109091 | 0.183150 | 0.890909 | buyer_list_region.png |
| Chat | 0.231990 | 0.164545 | 0.399267 | 0.488182 | chat_region.png |
| Product | 0.631258 | 0.109091 | 0.368742 | 0.840000 | product_region.png |
| Input | 0.231990 | 0.652727 | 0.399267 | 0.347273 | input_region.png |

换算分别对四条边取整：left=round(Left×W)、right=round((Left+Width)×W)，
top/bottom同理；.5向远离零方向取整，宽高由边界相减，避免独立取整宽高造成默认共享边界缝隙。
右/下边界不包含在区域内，坐标相对完整 PNG 左上角。
只对浮点舍入引起的1e-12以内归一化边界误差作容差；非法、负值、NaN/Infinity、越界或取整后为空的区域会报错。

目标区域仍是买家列表、消息历史、右侧商品/订单面板和输入区。
**比例回退不保证精确定位**：千牛控件并非等比缩放。V0.5曾出现标题栏残留等问题，
当V0.6退回比例布局时这些问题仍可能出现，Source=RatioFallback必须作为降级结果看待。
当前不接入AI接口、不自动调整窗口；实际回退比例可在模型属性中调整。

四张 PNG 写入当前目录的 `Output/Regions/`，再次运行会替换同名区域图，原图不变。
提取前检查 PNG 格式、有效尺寸、全部比例/区域边界以及源文件/输出路径冲突。
全部区域验证通过后才写入；失败时上一次区域图可能仍在目录中，应以本次成功日志判断输出是否更新。
比例计算不验证截图的页面身份；离线提取须提供接待中心截图，不能把工作台截图视为同一布局。

## 窗口发现与选择

- 按 `AliWorkbench` / `AliRender` 进程名查找，不硬编码 PID 或 HWND。
- 记录进程名、PID、Process.MainWindowHandle；使用 EnumWindows 枚举其顶层窗口。
- 区分进程 MainWindowHandle 与实际捕获 HWND，因为一个进程可能有多个窗口。
- 记录窗口标题、物理像素边界、可见/最小化/DWM cloaked 状态。
- 默认只选择可见、非最小化、非 cloaked 且标题包含“接待中心”的窗口，再按面积排序。
- 默认显示可见窗口；`dotnet run -- --list` 显示全部匹配窗口，不截图。
- 多店铺/多窗口时，可用枚举结果指定：`dotnet run -- --hwnd 0x实际句柄`。
  句柄仍须属于本次发现的千牛进程，且是可见的接待中心。

## 捕获方案与局限

本版采用用户允许的 Windows API 方案：`PrintWindow(hwnd, hdc, 0x2)`
（PW_RENDERFULLCONTENT），保存整个窗口，无桌面区域截取降级。
使用 PerMonitorV2 DPI 感知，Bitmap/Graphics/HDC 均有对应释放操作。
拒绝最小化窗口，不自动恢复、不置前、不点击；PrintWindow 超过 15 秒则返回失败并退出程序。

WGC 已评估，本版未实现或实测：其接入还需 WinRT/COM、Direct3D 设备、帧池和 GPU 帧读回，
为控制 V0.1 范围先验证 PrintWindow。官方资料：
[WGC CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)、
[Direct3D11CaptureFramePool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool)、
[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)。

PrintWindow 是同步调用，依赖目标应用绘制；API 成功不等于 CEF/GPU 内容完整。
程序对采样像素进行近黑、未绘制洋红底色和单色检查；明显异常时保存诊断 PNG 但返回失败。
这些检查仅发现明显空白，不能识别局部黑屏、缺失聊天或缺失商品卡片；仍须目视检查。
`Capture Success` 仅表示 PNG 已保存且通过基础像素检查。
程序不会把像素检查冒充 CEF/聊天语义验证。

## 输出与验收

截图：当前工作目录的 `Output/qianniu_yyyyMMdd_HHmmss.png`。
同一秒文件名冲突时增加毫秒和唯一后缀，避免覆盖已有样本。
退出码：0 成功（默认及消息模式表示至少一条确认消息；适配模式表示至少一个Envelope；商品模式表示Matched；其他独立命令保持原成功含义）；
1 捕获/裁剪/OCR/目录、文件或Adapter输入失败；2 参数错误；3 无可确认消息/无Envelope，或商品模式未可靠匹配。
默认消息流程中商品ID未知是允许的，不使已确认消息失败；具体商品状态仍单独保存。

保留运行日志：

```powershell
dotnet run 2>&1 | Tee-Object -FilePath Output/run.log
```

逐张人工确认：聊天区域边界、买家消息文字、商品卡片、CEF 页面内容、黑屏/空白。
当当前页面无聊天/商品卡片时，必须记为未验证。
截图和原始日志保留在本地 Output，不进入 Git。
历史实测：V0.1 [VALIDATION.md](VALIDATION.md)，V0.2 [VALIDATION_V02.md](VALIDATION_V02.md)。

范围止于 V1.0 qnbot离线兼容性PoC。UIA仍仅用于几何定位，不读取消息或商品ID文本。
未修改qnbot核心业务，未启用真实Shadow、AI回复或发送，也未接入真实消息传输。
