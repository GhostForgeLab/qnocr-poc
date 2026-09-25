# V1.2 真实新消息捕获 Shadow 验证

日期：2026-09-25。基于V1.1 `41ede9999a8e0ef47f92361d948254726d5e8761`。

## 实现及边界

`dotnet run -- --shadow` 默认持续观察。首帧只建立历史基线，不生成Envelope。
变化后执行当前截图商品OCR和聊天OCR，再由 `Shadow/NewMessageTracker.cs` 选择新增消息，
经原OcrMessageSource和隔离qnbot Adapter留档。保持本轮测试身份映射，不连接运行中的qnbot。

`Models/MessageFingerprint.cs` 对Role/Text/Timestamp/ProductId做长度前缀编码和SHA256。
指纹在当前进程会话内保存；图像基线重置/截图失败不重置指纹。
额外不含ProductId的消息键防止同一消息因商品面板改变而重发，当前商品无法识别时保持空。
每个执行聊天OCR的事件都重新识别同帧商品区域，不复用商品缓存或历史ProductId。
未知身份仍拒绝适配，未确认的角色/时间/气泡只留作Review。

早于观察开始的未见历史消息不计为新增；未来超过5秒的时间留作Review。
显示时间按China Standard Time解释并保留源消息时间。机器时间、显示时区或OCR有误会影响新增判断。
精确指纹不能防止文字OCR抖动造成的不同哈希，不能区分同秒同角色同文本的两次真实发送；
当前也不自动验证买家身份，不承诺切换买家、跨进程重启、崩溃恢复或持久投递语义。
本次须由操作员保持一个会话。相同批次可包含多条新增消息，消息数与Envelope数分别统计。

日志新增 `frame_*/message-comparison.json`，每条记录原消息索引、Fingerprint、ConversationMessageKey和Disposition。
原图、裁剪、原始OCR、完整BuyerMessage批次、新增Envelope和Adapter回执保存在同一事件目录。
重复/基线有判断记录而没有Envelope；重复观察计数不是重复事件数量。
可通过Ctrl+C或本次会话目录的 `stop.request` 文件正常结束并写summary。

## 编译与确定性检查

- 主程序：`dotnet build --no-restore -warnaserror`，0警告、0错误；`Output/v12-build.log`。
- 回归：178项通过；`Output/v12-checks.log`。覆盖五条新增、静止/重复OCR、顺序改变、上下文改变、同文字不同时间、
  旧历史滚入、未来时间、同批次重复、失败/布局重置保留去重、空商品不继承、实际隔离Adapter、取消与捕获超时。
- 新消息测试使用合成图片和明确标注的OCR证据；这些自动检查不冒充真实买家发送验证。
- qnbot源码基线维持 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，工作区干净；宿主原样校验6份源码。

## 第一轮诊断（不计作通过）

本地日志：`Output/v12-live-diagnostic.log`。
目录：`Output/Shadow/shadow_20260925_110423_458_5f76d404082a4475b2e30ff64a18648e`。
用户确认发送五条指定消息。首条发送时间11:04:19早于本轮11:04:23基线，正确被基线抑制。
40帧中输出3条新增消息/3个Envelope，转换均成功；末条气泡受比例回退裁剪影响未输出。
这一轮不能宣称5/5：首条不在新增测试时间窗内，末条存在真实漏检。

依据截图修正两点：

1. 当前接待中心展开摘要栏，旧视觉搜索上限未覆盖摘要底部分隔线，整个聊天区退回比例布局，最底部气泡被截短。
   扩大自然分隔线搜索范围，保留覆盖率/布局一致性检查；增加展开摘要栏合成回归样本。
2. 商品ID复制图标被OCR识别为紧邻数字的空心方框，原提取器拒绝该行。
   加入这个明确分隔符；仍拒绝字母数字后缀，不修补数字。正反例均有检查。

## 第二轮真实验收

运行：

```powershell
dotnet run --no-build -- --shadow --identity Validation/QnbotCompatibility/identity_mapping.json
```

目录：`Output/Shadow/shadow_20260925_110747_799_bc6b8f813a3b4579aae38aa218c1007a`。
运行日志：`Output/v12-live-shadow.log`。先建立零事件基线，再提示用户从买家端重发五条，逐条间隔约8秒。
本轮基线截图1638×949，聊天区域648×409，视觉顶部246、底部655，完整保留底部气泡；商品区域使用UIA几何边界。
用户确认“第二轮5条已发送”，逐条核对原始截图、OCR、新增判断、Envelope和Adapter回执一致。

| 消息 | 截图显示时间（2026-09-25） | 新增帧 | 事件次数 | Adapter |
| --- | --- | ---: | ---: | --- |
| 多少钱 | 11:07:48 | 26 | 1 | Converted |
| 有现货吗 | 11:07:57 | 28 | 1 | Converted |
| 可以刻字吗 | 11:08:08 | 31 | 1 | Converted |
| 什么时候发货 | 11:08:18 | 34 | 1 | Converted |
| 有没有大容量 | 11:08:28 | 37 | 1 | Converted |

5条均带当前截图识别的ProductId `1051087533910`。原消息时间保持不变，不修改成观察时间。
计数审计文件：`Output/v12-live-audit.json`。

```text
Status=StoppedByRequest
Attempts=63; SuccessfulFrames=63; FailedFrames=0
ProductOcrCalls=6; ChatOcrCalls=6
BaselineMessages=1; NewMessages=5; UniqueNewFingerprints=5
Envelopes=5; ConvertedEnvelopes=5; ConvertedMessages=5; DeferredEnvelopes=0
DuplicateObservations=7; HistoricalObservations=0
```

本轮5条期望消息全部捕获，新增事件恰好5条、5个不同指纹，无额外/遗漏文本。
重复事件0/5（0%），误触发0条，漏检0/5；这只描述本轮小样本，不代表一般正确率。
7次重复观察被抑制，没有生成额外Envelope。最后一条新增之后继续观察26个静止帧，
这些静止帧跨度约45.88秒，新增仍为0；随后正常停止，退出码0，摘要已保存。

五份转换回执均为ShadowConversionOnly：SourceFilesVerified=6、CoordinatorCreated=false，
ForbiddenBusinessCalls=0、RuntimeInitializers=0、IsTestIdentity=true。
例如最后一条完整证据位于 `frame_000037/`，其中 `message-comparison.json` 含新增消息的完整SHA256，
`envelope_001.json` 与 `adapter_001/qnbot-messages.json` 分别为统一事件和原生对象；
`adapter_001/shadow-adapter-result.json` 是转换结果。

## 禁止项核验

沿用V1.1隔离转换分支：不创建Coordinator、不调用真实入口、不使用SalesAgent/AI回复/Send。
未修改qnbot核心、Knowledge V2、Prompt或Reply Chain；UIA仅用于几何定位。
测试身份标记IsTestIdentity=true。商品知识映射仍是本地测试目录；当前可见商品与每条消息的业务关联未经验证。
真实账号、原始截图和日志只存Git忽略的Output，不提交到版本库。
两轮真实观察进程均已停止。V1.2以最终第二轮5/5通过收尾，不继续扩展到真实业务消费或回复。
