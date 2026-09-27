# V1.26 JEV Intent Provider PoC 验证报告

## 结果

已实现可配置、默认关闭的 `JEVIntentClassifier : IIntentClassifier`，并提供独立的 `--compare-intents` 命令。同一条合成问题分别交给 RuleBased 和 JEV；结果只写本地对比记录，不改变现有 Shadow Runner 的规则分类、Agent 请求、知识读取、回复或发送流程。

## 调用方式与架构

按 [TypeSafe HTTP API](https://docs.typesafe.ai/api)、[Choice](https://docs.typesafe.ai/primitives/choice) 与[模型列表](https://docs.typesafe.ai/models)实现：`POST https://api.typesafe.ai/v1/systemone`，请求包含 `state`、配置中的 `model` 和 9 选项的 Choice 问题，返回 `answers.intent.choice`、`probabilities`、`confidence` 与实际模型 ID。API Key 只从 `TYPESAFE_API_KEY` 环境变量读取，放在 Bearer 请求头；不进入配置、Git 或日志。JEV调用与现有回复生成 AI Provider 完全分离。配置 `Config/jev-provider.json` 含 Endpoint、Model、TimeoutSeconds、Enabled，默认 `Enabled=false`。本轮真实验证用 `Output/ValidationV126/jev-enabled.json` 明确开启。

`RuleBasedIntentClassifier` 与 `IIntentClassifier` 接口没有修改。`JEVIntentClassifier` 仅在显式对比命令中创建，绝不自动选用其结果。Choice 新增 `GIFT_QUERY`、`COLLECTION_QUERY` 供 PoC 比较；现有规则没有这两个专门标签，因此一部分差异属于标签集不同，不是可直接宣称的识别错误。`IntentComparisonRunner` 保留两侧结果、差异和模型置信度，逐条写 `INTENT_COMPARISON`（Input、RuleResult、JEVResult、Timestamp）；同目录重复运行会被拒绝，避免覆盖审计。

## 文件与提交

实现提交：`e1a1ef4bc6121b03643871dd8f00b79dc92516a7`。新增 `Decision/JEVIntentClassifier.cs`、`Decision/JEVProviderConfig.cs`、`Decision/IntentComparisonRunner.cs`、`Decision/intent-samples.v126.json`、`Config/jev-provider.json`、`Models/IntentComparison.cs`、`Validation/ChangeDetectionChecks/JevIntentChecks.cs`；修改主 `Program.cs`、`Shadow/QnocrShadowRunner.cs` 的版本标识和检查入口；删除文件：无。MessageEnvelope、qnbot核心、Knowledge系统、回复 AI Provider和发送逻辑均未修改。

## 测试

- 编译：主项目、检查项目与 Knowledge Console 均成功，0 警告、0 错误。
- 回归：518/518（V1.25 基线 508，新增 10）；原入口离线回归 17/17，ForbiddenBusinessCalls=0。日志：`Output/ValidationV126/regression.log`。
- PoC：20/20 条合成中文客服问题完成真实 TypeSafe 请求，无 `JevUnavailable`；服务返回实际模型 `jev-1.13.0`。与规则一致 8 条、不同 12 条；相对于本轮人为编写的合成期望标签，Rule 8/20，JEV 20/20。JEV Choice confidence 最低 0.96，但它是选项分布集中度，**不是**真实客服准确率。
- 差异：8 条是现有关键词规则未覆盖的规格、材质、发货、售后、定制、价格表达；4 条是送礼／收藏专门标签对规则 `OTHER`。需要更大、真实、人工标注的数据才能判断是否值得正式接入。
- AI与安全：本轮 JEV 外部分类请求 20 次；既有隔离回归的模拟回复 AI 回调和 Shadow Reply Preview 继续通过，本轮没有额外调用回复生成端点。SentMessages=0，无自动发送。对比日志不含 API Key；发送给 TypeSafe 的仅是这份合成样本问题，没有真实买家会话。

## 20 条合成样本结果

| ID | 问题 | 人工期望 | RuleBased | JEV | 对比 |
| --- | --- | --- | --- | --- | --- |
| S01 | 这款有200ml规格吗？ | PRODUCT_SPEC_QUERY | PRODUCT_SPEC_QUERY | PRODUCT_SPEC_QUERY | Same |
| S02 | 杯子多大，能装多少水？ | PRODUCT_SPEC_QUERY | PRODUCT_SPEC_QUERY | PRODUCT_SPEC_QUERY | Same |
| S03 | 同系列有更小号的吗？ | PRODUCT_SPEC_QUERY | OTHER | PRODUCT_SPEC_QUERY | Different |
| M01 | 是TA1纯钛做的吗？ | MATERIAL_QUERY | OTHER | MATERIAL_QUERY | Different |
| M02 | 内胆用的什么材料？ | MATERIAL_QUERY | MATERIAL_QUERY | MATERIAL_QUERY | Same |
| H01 | 今天下单何时寄出？ | SHIPPING_QUERY | OTHER | SHIPPING_QUERY | Different |
| H02 | 寄到上海一般要几天？ | SHIPPING_QUERY | OTHER | SHIPPING_QUERY | Different |
| A01 | 刻字后还能退吗？ | AFTER_SALES_QUERY | AFTER_SALES_QUERY | AFTER_SALES_QUERY | Same |
| A02 | 收到后发现划痕怎么处理？ | AFTER_SALES_QUERY | OTHER | AFTER_SALES_QUERY | Different |
| A03 | 不合适能换吗？ | AFTER_SALES_QUERY | OTHER | AFTER_SALES_QUERY | Different |
| C01 | 可以刻名字吗？ | CUSTOMIZATION_QUERY | OTHER | CUSTOMIZATION_QUERY | Different |
| C02 | 杯盖能印公司logo吗？ | CUSTOMIZATION_QUERY | CUSTOMIZATION_QUERY | CUSTOMIZATION_QUERY | Same |
| P01 | 现在买需要付多少？ | PRICE_QUERY | OTHER | PRICE_QUERY | Different |
| P02 | 有优惠券吗？ | PRICE_QUERY | PRICE_QUERY | PRICE_QUERY | Same |
| G01 | 送朋友合适吗？ | GIFT_QUERY | OTHER | GIFT_QUERY | Different |
| G02 | 想买给老师当礼物，会不会太随意？ | GIFT_QUERY | OTHER | GIFT_QUERY | Different |
| K01 | 这款适合收藏展示吗？ | COLLECTION_QUERY | OTHER | COLLECTION_QUERY | Different |
| K02 | 限量款值得收着不拆吗？ | COLLECTION_QUERY | OTHER | COLLECTION_QUERY | Different |
| O01 | 这东西怎么样？ | OTHER | OTHER | OTHER | Same |
| O02 | 我再看看。 | OTHER | OTHER | OTHER | Same |

逐条机器可读结果及审计见 `Output/ValidationV126/live_final/intent-comparison.json`、`intent-comparison.jsonl`（本机验证产物，不提交 Git）。

## 当前状态与下一步

已达到 `IIntentClassifier → RuleBased / JEV 双实现与独立对比`，尚未达到 JEV 驱动 Agent 决策。下一步建议先收集人工标注的真实客服问题，并评估置信度、费用、延迟与错误类型，再决定是否做混合 Intent 策略；不要凭这 20 条合成样本替换 RuleBased。
