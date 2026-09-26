# V1.15 Agent Decision Layer基础架构验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`28c41413e2e4faf7b2408b642cb455aaceaa3b14`。本报告单独提交。

## 结论

已在qnocr-poc的Shadow调度外层建立 `Session Context → IIntentClassifier → Decision Sidecar/日志 → 原AI适配器 → Reply Preview`。分类结果只作审计和人工提示：不改原AI问题文本、Provider、Prompt或MessageEnvelope，不查询知识库，也不决定发送。规则分类器是可替换的首版实现，**未正式接入JEV**。

## 修改原因与架构影响

V1.14真实测试中，一条商品规格问题需人工补充商品信息；现有“Context直接进AI”无法显式记录问题是否需要商品上下文、知识信息或人工核对。V1.15增加可注入的`IIntentClassifier`和有限关键词`RuleBasedIntentClassifier`，在调用原AI适配器前生成并持久化`IntentResult`。`NeedKnowledge=true`是需求标志，绝不代表已查询资料或命中知识库。

- `MessageEnvelope`：未修改，原`envelope.json`结构不加决策字段。
- qnbot核心、原AI Provider/模型配置、Prompt和生成逻辑：未修改；qnbot工作树干净。
- Agent Pipeline外层：`DurableShadowDispatch`在读取`SessionContextView`后、调用原`_send`前分类；Outbox新增`decision.json`。`DECISION_CREATED`记录SessionId、MessageId、Intent、Confidence和RiskLevel。分类异常或非法结果使本条停在`DecisionFailed`，AI Calls=0、不自动重试，后续消息仍可处理。
- `ReplyPreview`兼容地增加可选`Decision`及只读顶层`Intent/RiskLevel`；人工辅助视图新增`DecisionIntent/DecisionRiskLevel`。原`RiskLevel`仍是买家身份风险，不与决策风险混用。旧预览缺决策字段仍可读取。
- 规则`Confidence=null`：关键词匹配未作概率校准，不能把1.0等数值伪装成识别准确率。规则不产生商品事实、FAQ或销售话术。

## 修改文件

新增：`Decision/IIntentClassifier.cs`、`Decision/RuleBasedIntentClassifier.cs`、
`Models/IntentResult.cs`、`Validation/ChangeDetectionChecks/DecisionChecks.cs`、本报告。
修改：`Models/AssistedReply.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/AgentPipelineTrace.cs`、`Shadow/AssistantReplyService.cs`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、
`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`及其`Program.cs`。删除：无。

## 编译、回归与Intent检查

.NET 8主项目与验证项目Build Success、0警告、0错误；.NET Framework 4.8离线兼容宿主Build Success，原上游源码完整构建时有既有CS4014警告，0错误。累计检查 **342/342**，高于327项基线；原qnbot入口 **17/17**，`ForbiddenBusinessCalls=0`。

| 输入 | 首版Intent | 附加判断 |
|---|---|---|
| 这款有200ml规格吗？ | `PRODUCT_SPEC_QUERY` | NeedProductContext=true、NeedKnowledge=true、NeedHumanReview=true、RiskLevel=Medium、Confidence=null |
| 多久发货？ | `SHIPPING_QUERY` | 仅分类/提示，不查物流或承诺时效 |
| 支持刻字吗？ | `CUSTOMIZATION_QUERY` | 仅分类/提示，不查商品定制能力 |
| 是什么材质？ | `MATERIAL_QUERY` | 仅分类/提示 |
| 这款多少钱？ | `PRICE_QUERY` | 仅分类/提示 |
| 刻字后不喜欢能退吗？ | `AFTER_SALES_QUERY` | 售后词优先于刻字词 |
| 你好 | `OTHER` | 明确兜底，不猜测商品事实 |

## AI链路、预览与安全

合成Agent链路验证：Context构建后`decision.json`先于未改动的AI适配器回调落盘；适配器调用1次、模拟AI Calls=1、Reply Preview=1，`DECISION_CREATED → AI_REQUEST → AI_RESPONSE → REPLY_PREVIEW_CREATED`顺序成立。决策元数据没有写入`context.json`或`envelope.json`；模型调用参数仍由原链路决定。本轮**没有新的外部AI请求**，V1.14已完成的3次真实OpenRouter调用不冒充V1.15实测。

人工CLI读取新增合成预览：`Intent=CUSTOMIZATION_QUERY`、`DecisionRiskLevel=Medium`、买家身份`RiskLevel=High`、`SendStatus=Shadow`、`SentMessages=0`；旧版V1.14真实预览继续读取，`Decision=null`。注入分类器异常/非法置信度或风险时，状态`DecisionFailed`、AI Calls=0、无预览；同一调度器下一条仍能完成。AI OFF消息不运行分类器，也不生成决策旁路文件。没有自动发送、Send Adapter调用、JEV调用、知识库或商品详情库。

本地证据（`Output/`被Git忽略）：`Output/v115-checks.log`、
`Output/ValidationV115/original-entry-final/run.log`、
`Output/ValidationV115-preview-cli.log`、`Output/ValidationV115-legacy-preview.log`，
以及`Output/ValidationV15/`最新合成运行的`decision.json`和`agent-pipeline.jsonl`。

## 当前状态与下一步

已达到基础架构层面的“Message → Decision Layer → 原AI适配器 → AI Reply Preview”。它只给人工提供问题类型与信息需求提示，**尚未让AI使用决策调整回复，也没有验证真实千牛新消息经本决策层的外部AI调用**。V1.14人工标记的商品规格缺信息问题说明下一步优先设计只读`Knowledge Interface`的需求与安全边界，再决定是否用JEV替换规则分类器；两者均需独立PoC和验收，本阶段不扩展。
