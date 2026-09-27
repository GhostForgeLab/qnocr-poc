# V1.22 Knowledge Authoring 验证报告

## 结果

已建立本地测试商品的结构化录入辅助流程：显式 JSON 输入 → `ProductKnowledgeProfile` → `KnowledgeDraft` → V1.21 `KnowledgeReviewService` 逐条人工审核 → 明确发布并允许 AI 使用 → `ProductKnowledgeProvider` 只读。没有自动确认事实、自动发布或发送消息。

## 修改原因与架构影响

V1.21 已能审核单条知识，但录入一个商品的规格、材质和卖点仍需人工逐字段调用。本轮增加单商品 Authoring Service，把输入值原样映射为 Schema 与关联的 Draft 知识项，并在创建时对照同店铺、同商品、同字段的已发布值。不同值形成 `Conflict` 草稿，候选字段不进入审核或 Provider；相同值跳过，避免重复。发生冲突须由人工修订来源资料后重新录入，本轮不提供自动覆盖。

`MessageEnvelope`、qnbot 核心、Decision Layer、AI Provider 和发送链均未修改。Schema 的 `ProductKnowledgeIdentity` 增加可选 `ProductName`；`KnowledgeSourceType` 增加 `Import` 与 `AssistantGenerated`，这两种来源仍只能从 Draft 开始。V1.21 审核服务增加同批次创建多个 Draft 项的单次状态写入。显式审核模式下，Repository 现在也可为旧商品清单中不存在的新 ProductId 建立只读记录；未发布时不暴露事实。旧本地测试格式仍保持兼容。

本轮仍限一个本地测试商品文件一次录入，要求 `IsTestData=true`；未知字段、凭证式额外字段与真实商品输入均拒绝。操作者字符串是本地审计标签，不构成生产身份鉴权；没有批量导入或后台。

## 文件

实现提交：`a4643dbbf067b0b8670282c362cf9e7b887ef988`。

- 新增：`Models/KnowledgeDraft.cs`、`Knowledge/KnowledgeAuthoringService.cs`、`Knowledge/KnowledgeAuthoringCommand.cs`、`Knowledge/product-authoring.sample.json`、`Validation/ChangeDetectionChecks/KnowledgeAuthoringChecks.cs`。
- 修改：`Models/ProductKnowledgeProfile.cs`、`Models/KnowledgeItem.cs`、`Knowledge/ProductKnowledgeSchemaValidator.cs`、`Knowledge/KnowledgeReviewService.cs`、`Knowledge/JsonProductKnowledgeRepository.cs`、`Program.cs`、`Shadow/QnocrShadowRunner.cs`、`Validation/ChangeDetectionChecks/Program.cs`。
- 删除：无。

## 使用入口

测试草稿：`dotnet run -- --knowledge-draft Knowledge/product-authoring.sample.json Output/AuthoringTest operator-test`。命令返回 DraftId 和逐字段 KnowledgeItemIds。可用 `--knowledge-show <state-dir> <draft-id>` 查询草稿聚合状态。逐条人工操作为 `--knowledge-review submit|approve|reject|publish <state-dir> <knowledge-id> <operator>`；`reject` 需要原因，`publish` 必须额外提供 `--allow-ai-use`。这些命令仅操作本地测试状态，不连接真实千牛发送。

## 验证

- 主项目与检查项目编译成功：0 警告、0 错误。
- 回归：465/465 项通过（V1.21 基线 446，新增 19）；原入口离线回归 17/17，ForbiddenBusinessCalls=0。完整检查日志位于 `Output/ValidationV122/regression.log`。
- Draft：测试 JSON 映射出 Name、Capacity、Material、Feature；3 个关联条目初始均为 Draft、AI 使用许可关闭。`AssistantGenerated` 来源也保持 Draft。
- 审核发布：3 个条目经显式 Submit、Approve、Publish 后，Provider 返回规格、材质和卖点；实际 CLI 跑通 3 项，聚合草稿状态为 Published。
- 新商品：不在旧商品 JSON 中的测试 ProductId，在 Draft 阶段不可读，审核发布后返回其自身 `Capacity=300ml`，不借用其他商品。
- 冲突：已发布规格为 `100ml, 200ml` 时，候选 `300ml` 得到 Conflict、0 个新审核项，已发布值不变；相同值不会重复创建。
- AI：本地模拟 AI 回调 1 次，生成 1 条使用已发布规格的 Shadow Reply Preview；**本轮外部 AI 调用为 0**，不据此评价真实模型回复质量。
- 安全：SentMessages=0，无 `send-request.json`；未接入自动发布、真实商品批量导入、RAG 或知识库后台。

## 当前状态与后续

已达到“测试商品资料 → Knowledge Draft → 人工审核 → Published Knowledge → Shadow AI 预览”。真实商品录入前仍需操作员鉴权、冲突修订流程及更可靠的持久化审计；本轮本地 JSON 不作为生产知识管理系统。后续可独立评估 JEV Intent Provider PoC、商品知识导入工具或 Agent Strategy Layer。
