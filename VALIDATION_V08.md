# V0.8 买家消息结构化验证

日期：2026-09-25。基于 V0.7 `529455690b6dfefff7598a0abf4f6af6a2492ac2`。
用户确认采用保守规则：只有能定位买家气泡、且取得完整日期时间的内容才输出消息；
其他情况待确认，商品ID未知时留空。

## 交付与字段语义

- `Models/BuyerMessage.cs`：Role、Text、Timestamp、Source、ProductId五个字段。
- `Messages/BuyerMessageStructurer.cs`：结合OCR文本/边界框和聊天图像的浅色气泡、灰色消息头生成对象。
- Role当前只输出buyer；Text保留OCR原文，同一气泡多行用换行连接，不跨气泡合并。
- Timestamp取显示的完整日期时间，DateTimeKind.Unspecified，不推断时区，不用当前日期/采集时间替代。
- Source固定为Qianniu.PaddleOCR，表示转换链路，不表示UIA读取或已完成身份认证。
- ProductId缺失为空；默认流程仅使用同一采集帧的商品识别结果，独立命令可显式提供。
  这是可见商品上下文，不是历史消息与商品关联的业务证明。
- JSON外层保存Evidence、ReviewItems、输入SHA256及时间/商品上下文来源说明。

## 构建与自动检查

Windows x64，.NET SDK8.0.425 / runtime8.0.31。
主程序及检查项目严格构建：**0警告、0错误**。
**124项检查通过**（此前102项及本版22项），日志 `Output/v08-build.log`、`Output/v08-checks.log`。

覆盖多行合并、完整时间来源、前一天时间保持不变、未知商品留空、显式商品字段传播、
缺失/非法/低分/冲突时间、未分配灰色文字保留、截断气泡、低分正文整条待确认、NaN分数、
正文中的日期不被当成消息时间、分框昵称/时间头、蓝色及右侧内容不标为买家、
黑色正文不冒充灰色时间头、同文本不同气泡仍是两条消息、表情单独成文及非法商品ID。
检查使用程序绘制的气泡和构造OCR证据；真实PaddleOCR结果另列如下。

## 历史截图端到端

输入 `Output/OCR/Inputs/chat_region.png`，执行：

```powershell
dotnet run --no-build -- --messages Output/OCR/Inputs/chat_region.png
```

生成两条buyer消息：

| Text | Timestamp | ProductId |
| --- | --- | --- |
| 上海的 | 2026-09-24T19:51:32 | 空字符串 |
| ok | 2026-09-24T19:51:45 | 空字符串 |

右侧客服回复未被列入买家消息。昵称、时间头、状态等没有作为消息正文输出。
本次实际运行日期是9月25日，消息日期仍保留截图中的9月24日。
退出码0，7个未分配OCR项保留为ReviewItems，并不代表7条未读消息。

日志：`Output/v08-historical.log`。
结果：`Output/Messages/buyer_messages_20260925_091029_098.json`。

显式加 `--product-id 1051087533910` 后，两条消息均带该ID，
外层ProductBinding标注ExplicitCallerContextNotVerifiedMessageAssociation；
仅验证字段传播，不宣称历史消息与该商品真实关联。
日志 `Output/v08-explicit-context.log`，结果 `Output/Messages/buyer_messages_20260925_091155_175.json`。

## 最终真实窗口流程

用户展开接待中心后，最终 `dotnet run --no-build` 完成捕获、定位、聊天OCR、商品OCR和结构化，退出码0。
原图 `Output/qianniu_20260925_091439.png`；Chat Bounds=(380,181,655,459)。
验收聊天图保留为 `Output/ValidationV08/live/chat_region.png`，逐张人工确认两条白色买家气泡及其灰色时间头。
输入PNG SHA256：`16EEC506D602033F12758F4EB8729C2AE3073ACB0DC21B6DE75C2E27AEFF30CF`。

最终消息示例：

```json
{
  "Role": "buyer",
  "Text": "上海的",
  "Timestamp": "2026-09-24T19:51:32",
  "Source": "Qianniu.PaddleOCR",
  "ProductId": ""
}
```

另一条为ok，时间19:51:45。两条正文及时间均与可见截图一致。
当前右侧商品区域无可靠ID，V0.7返回NotFound；消息ProductId留空，没有套用前次1051087533910。
“打开摘要”、客服文字、已读状态和图形误识别等7项保留为待确认，未输出为buyer消息。

- 日志：`Output/v08-pipeline.log`。
- 最终消息：`Output/Messages/buyer_messages_20260925_091445_498.json`。
- 同帧商品结果：`Output/Product/product_context_20260925_091445_472.json`。
- 本次聊天/商品图副本：`Output/ValidationV08/live/`。

期间发现接待中心最小化时，旧选择逻辑会退回工作台。该页未产生买家消息，但来源不合适；
本版已限制默认采集和指定HWND采集必须是可见接待中心，避免继续对工作台套用聊天布局。
不自动还原窗口或执行任何点击。

## 保守处理和边界

截断测试图经实际OCR后无可确认消息，返回3并保留待确认项（`Output/v08-clipped.log`）；
非法显式ProductId返回2（`Output/v08-invalid-args.log`）。
其中截断图是程序生成测试图，不是新收到的真实消息。

规则面向当前浅色、左买家白色气泡布局：色阈值、相对位置、完整灰色昵称/日期头和≥0.80的OCR证据共同筛选。
它仍是POC启发式，不保证任意皮肤、缩放、系统卡片、转发内容或复杂消息都能准确识别角色。
只含时分、相对日期、缺少昵称头、头部被裁掉、分数过低等会降低召回，不补造数据。
即使已识别行高分，OCR仍可能漏字；本次两条消息正确不能推出总体准确率。
ReviewItems是未确认OCR证据集合，可能包含非消息UI文字，不能当作待处理买家消息队列。

默认流程有确认消息即可成功，商品ID为空是允许状态；配置或OCR执行错误仍报错。
没有把变化横带自动拼成消息；`--watch`和`--compare`仍只做原有变化检测/OCR。
没有跨帧记忆、重复消息过滤、新消息断言、时间推断、真实商品知识库接入或业务系统写入。
UIA仍只做几何定位，不读取聊天文本；没有自动回复、点击或发送。工作停止在V0.8。
