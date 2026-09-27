# V1.18 Feedback Learning Router反馈分类验证报告

日期：2026-09-27（Asia/Shanghai）。实现提交：`1882439bc7293c4fc34da2d8f964af694f402f01`。本报告单独提交。

## 结论

已建立`ReplyFeedback → IFeedbackRouter → FeedbackClassification → 审计与统计`。它只给人工指出后续调查位置：Knowledge、Prompt、Strategy、Decision或None；不训练AI、不修改知识内容或Prompt、不改变发送行为。

`ReplyFeedback.FeedbackType`仍表示人工结果Accepted/Edited/Rejected；`FeedbackClassification.FeedbackType`才表示KnowledgeGap/StyleIssue/RuleIssue/IntentIssue/Other。规则置信度为null，未经过概率校准。证据不足时明确归Other，不把文本差异冒充确定原因。

## 修改原因与架构影响

V1.14已保存AI建议和人工最终回复，但无法回答“为何人工修改”。V1.18新增`IFeedbackRouter`和可替换的`RuleBasedFeedbackRouter`。人工原因“缺少商品信息”“语气问题”优先；售后不准、明确意图误判及有限的新增容量/材质事实差异才触发对应路由。分类结果与新反馈一起原子保存到`assistant-feedback.json`，并追加`FEEDBACK_CLASSIFIED`审计（ReplyPreviewId、分类类型、建议目标、原因代码，不写回复正文）。旧反馈不会自动重写，可用`--assist-classify`显式补分类，保留原人工字段且幂等。

- `MessageEnvelope`、qnbot核心、Decision Layer、Knowledge Interface、AI Provider与Prompt：均未修改；qnbot工作树干净。
- 辅助扩展层：人工反馈记录增加可选DecisionIntent/Classification字段，兼容旧JSON；Batch报告和按运行批次仪表盘增加五类数量及未分类旧记录数。
- 任何分类只建议调查目标，不触发Knowledge、Prompt、Strategy或Decision文件改写；不接JEV、不调用AI模型、不自动训练或发送。

## 修改文件

新增：`Feedback/IFeedbackRouter.cs`、`Feedback/RuleBasedFeedbackRouter.cs`、
`Models/FeedbackClassification.cs`、`Validation/ChangeDetectionChecks/FeedbackRouterChecks.cs`及本报告。
修改：`Models/AssistantFeedback.cs`、`Models/AssistedReply.cs`、`Models/EvaluationBatch.cs`、
`Program.cs`、`README.md`、`Shadow/AssistantCommand.cs`、`Shadow/AssistantReplyService.cs`、
`Shadow/AssistantRuntimeDashboard.cs`、`Shadow/EvaluationBatchService.cs`、
`Validation/ChangeDetectionChecks/EvaluationBatchChecks.cs`及其`Program.cs`。删除：无。

## 编译、回归与分类案例

.NET 8主项目与验证项目Build Success、0警告、0错误；.NET Framework 4.8隔离宿主Build Success、0错误，完整构建保留上游既有CS4014警告。累计检查 **387/387**，高于374项基线；qnbot原入口 **17/17**，`ForbiddenBusinessCalls=0`。

| 测试差异 | 人工线索 | Router分类 | SuggestedTarget |
|---|---|---|---|
| AI“不知道200ml规格”，人工补充“有200ml规格” | 规格类、AI不确定而人工加入具体容量 | KnowledgeGap | Knowledge |
| AI语气过于官方，人工改为简短客服语气 | 人工原因“语气问题” | StyleIssue | Prompt |
| AI退款/退货规则错误，人工修正售后表述 | 售后类、人工原因“信息不准确” | RuleIssue | Strategy |
| 人工商品规格分类与原SHIPPING_QUERY决策不符 | 明确意图错配 | IntentIssue | Decision |
| AI建议获人工采用且未改写 | 没有纠错证据 | Other | None |

规则异常会降级Other并保留人工反馈。新记录持久化分类，审计字段齐全而不含回复正文；旧记录补分类保留人工字段、再次执行不新增审计。

## 真实历史反馈统计与安全

对V1.14已有人审、已关闭的测试Batch `32ad65ba0b1445cdb5807138f5c05cb4` 显式补分类：3条中**KnowledgeGap 1、Other 2**，StyleIssue/RuleIssue/IntentIssue各0，未分类旧记录0。KnowledgeGap来自人工标记的“缺少商品信息”，表示未来应调查商品资料，并不验证人工新增的“200ml”商品事实。两条Accepted保留Other/None，不计为需优化的错误。批次报告和运行仪表盘计数一致。

本轮Router没有AI网络调用；原Agent Pipeline合成回归仍记录1次模拟AI适配器调用、Reply Preview正常、`SentMessages=0`。V1.14真实AI Calls=3是既有历史证据，不能算作V1.18新调用。历史状态目录`FEEDBACK_CLASSIFIED`审计3条，均具ReplyPreviewId、FeedbackType、SuggestedTarget、Reason；不含买家问题正文，发送请求文件0。知识库、Prompt、模型配置与真实千牛发送均未被Router修改。

## 当前状态与下一步

已达到“AI回复 → 人工反馈 → 反馈分类 → 优化目标定位”的基础架构。当前真实样本只有一条KnowledgeGap，不能据此自动更新单商品测试数据或确定全量知识库优先级。应继续积累多批次人工标注，核实商品事实来源后再评估多商品Knowledge Provider设计；若IntentIssue/RuleIssue显著增多，再分别考虑JEV Intent Provider或Agent Strategy Layer。所有后续优化须单独设计、审批与验证。

本地证据（`Output/`被Git忽略）：`Output/v118-checks.log`、
`Output/ValidationV118/original-entry-final/run.log`、
`Output/ValidationV118/classify_*.log`、`Output/ValidationV118/v114-routed-batch.log`，
以及V1.14状态目录内的分类后`assistant-feedback.json`和`assistant-actions.jsonl`。
