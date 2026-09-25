# V0.9 BuyerMessage Adapter接口验证

日期：2026-09-25。基于V0.8 `025e16bcf8333ac9ecf18d3ab8b80693e130034c`。
仅在qnocr-poc内定义、转换和本地消费契约；没有读取、引用、调用或修改qnbot项目。

## 接口与契约

- IMessageSource：Source属性及可取消的IAsyncEnumerable<MessageEnvelope> ReadAsync。
- OcrMessageSource：通过异步批次提供方取得一次BuyerMessageBatch，只适配已确认Messages。
- MessageEnvelope：Source、SellerId、BuyerId、ProductId、Messages；内层Role、Text、Timestamp。
- 外层SchemaVersion=1及ProductBinding保留版本和商品上下文来源语义。
- `Adapter/message-envelope.schema.json` 为本项目契约v1的结构说明。
- 卖家/买家ID只能由调用方提供，默认空字符串，不把OCR昵称当身份ID。
- Timestamp原样保留，商品上下文不等于已验证的消息商品关联。
- 消费方不接收图像、UIA、原始OCR或待确认条目。

这不是从qnbot现有接口获取的规范，也未声称已联调兼容。
契约仍在POC程序集内，尚未发布独立契约包或传输服务。

## 构建与检查

.NET SDK8.0.425 / runtime8.0.31，Windows x64。
主程序及验证项目严格构建：**0警告、0错误**。
**148项检查通过**，包含此前124项及本版24项。
日志：`Output/v09-build.log`、`Output/v09-checks.log`。

本版通过IMessageSource消费，并用独立消费DTO反序列化JSON，验证字段名、Unicode/换行/空格、
时间、版本、身份留空、显式身份、商品来源、连续商品分段、重复文本、空批次、待确认信息不泄漏、
非法输入整批拒绝、取消、上游异常及快照隔离。
JSON根字段集合与模式文件一致；**未引入或宣称运行完整JSON Schema验证引擎**。

A→空ID→B→A输出四个Envelope，不全局分组重排，也不补填空ID。
整批校验后才开始yield；后续取消不会收回已经输出的数据，不宣称原子投递。

## V0.8实际输出适配

```powershell
dotnet run --no-build -- --adapt Output/Messages/buyer_messages_20260925_091445_498.json
```

退出码0，一次批次读取生成一个Envelope，包含两条buyer消息：

```json
{
  "Source": "Qianniu.OCR",
  "SellerId": "",
  "BuyerId": "",
  "ProductId": "",
  "Messages": [
    { "Role": "buyer", "Text": "上海的", "Timestamp": "2026-09-24T19:51:32" },
    { "Role": "buyer", "Text": "ok", "Timestamp": "2026-09-24T19:51:45" }
  ],
  "SchemaVersion": 1,
  "ProductBinding": "None"
}
```

输入的7个ReviewItems没有进入事件，身份未猜测补值，时间未改成采集日期。
日志 `Output/v09-adapt.log`；结果
`Output/Adapter/message_envelope_20260925_093046_160_a1e5aeb7c4ef409b9ba3847324345888_001.json`。

另用V0.8显式商品上下文样本及`--seller-id seller-test --buyer-id buyer-test`验证元数据传递。
ProductId保留1051087533910，ProductBinding保留ExplicitCallerContextNotVerifiedMessageAssociation。
seller-test/buyer-test仅为测试值，不是实际账号身份。
日志 `Output/v09-explicit-identities.log`；结果
`Output/Adapter/message_envelope_20260925_093046_507_66798db2d1ff4197b3f37689e1ac5b9a_001.json`。

## OCR到Adapter的本地链路

```powershell
dotnet run --no-build -- --messages Output/ValidationV08/live/chat_region.png
```

实际执行PaddleOCR → BuyerMessage结构化 → IMessageSource → MessageEnvelope JSON，退出码0。
仍得到上述两条消息、一个Envelope，没有启动实时监测或调用外部消费者。
日志 `Output/v09-ocr-adapter.log`；结果
`Output/Adapter/message_envelope_20260925_093050_312_5d0aa7a037e84f9d88ea0aa8828b21f1_001.json`。

默认单次采集流程已接入同一个SaveEnvelopes调用；本版端到端实测使用已有真实聊天截图，
未另行进行桌面采集或qnbot联调。

## CLI边界

| 输入 | 结果 | 退出码 |
| --- | --- | ---: |
| 已保存的真实BuyerMessageBatch | 一个Envelope | 0 |
| 显式测试身份与商品上下文 | 原样传递 | 0 |
| 空Messages，包括仅待确认内容 | 0个Envelope，不生成空事件 | 3 |
| 重复--buyer-id | 参数错误 | 2 |
| 非法JSON | 明确失败，无伪造事件 | 1 |

日志：`Output/v09-empty.log`、`v09-invalid-args.log`、`v09-invalid-json.log`，
汇总 `Output/v09-cli-checks.log`。最终JSON先写临时文件后改名，是本地验收产物。

## 尚未承诺的行为

每次枚举仅调用一次提供方，没有自动轮询、跨批次合并、去重、游标、事件ID、ACK、重试或可靠投递。
输出是消息快照，不表示本次新收到的消息；重复读取相同批次会再次产生事件。
空SellerId/BuyerId是未知身份，不能用于跨会话路由或合并。
ProductId不是历史消息商品归属证明，Timestamp不自动转换时区。
输入须为可信qnocr批次；格式校验不是身份认证或图像真实性校验。
未来接入qnbot还需确认实际契约、身份来源、会话归属和去重/投递规则。
本版未修改qnbot、调用回复链路、写入外部服务或发送消息，停止在V0.9。
