# V1.21 Knowledge Management Workflow 验证报告

## 结论

本轮建立了本地测试知识的创建、人工审核、发布、读取流程。显式启用审核状态文件后，Repository 和 Provider 只向 AI 上下文提供 `Published` 且 `AiUseAllowed=true` 的条目；Draft、Reviewing、Approved 但未发布、Rejected，以及已发布但未授权 AI 使用的条目均不可读取。状态文件丢失时安全失败，不回退到旧商品字段。没有真实发送。

## 修改原因与实现

V1.20 的 `ProductKnowledgeProfile` 能组织知识，但缺少条目级来源、审核和发布控制。新增 `KnowledgeItem`、`KnowledgeSource`、`KnowledgeStatus`、`KnowledgeVersion`，保存 SellerId、ProductId、Category、Key、Value、来源、时间、状态及版本。`KnowledgeReviewService` 提供显式 `CreateDraft`、`SubmitForReview`、`Approve`、`Reject`、`Publish`；每次状态操作需要操作者标签，发布时还须明确 `allowAiUse`。服务将测试状态写入 `knowledge-items.json`，将不含知识正文的 `KNOWLEDGE_CREATED`、`KNOWLEDGE_REVIEWED`、`KNOWLEDGE_PUBLISHED`、`KNOWLEDGE_REJECTED` 等事件写入 `knowledge-audit.jsonl`。重复发布同一店铺、商品、字段的活动版本会被拒绝。

`JsonProductKnowledgeRepository(path, workflowItemsPath)` 在显式指定状态文件时逐次读取审核状态，仅返回已发布且允许 AI 使用的条目。`ProductKnowledgeProvider` 在该模式下不读取旧 `Fields`／`Profile` 回退内容；`KnowledgeResult` 和 `KNOWLEDGE_RESULT`／`KNOWLEDGE_CONTEXT_ATTACHED` 追踪条目 ID、来源类型、来源引用、版本，不在审计日志写知识正文。Shadow CLI 增加可选 `--knowledge-items`，须同时提供已有的 `--product-knowledge` 测试源。

旧本地测试 JSON 不指定审核文件时仍按 V1.20 方式读取，以保留原有 427 项基线。这条兼容路径不表示旧知识已完成审核；正式使用审核流程必须显式指定状态文件。本阶段没有知识后台、角色鉴权或真实商品批量导入，操作者字符串只是审计标识，不是平台验证身份。

## 架构影响

- MessageEnvelope：未修改。
- qnbot 核心：未修改。
- Knowledge Interface：`IKnowledgeProvider`、`IProductKnowledgeRepository` 接口签名未变；结果增加可选条目来源元数据。
- Repository：增加显式审核状态文件读取和发布过滤；旧格式测试数据继续兼容。
- AI Provider、Prompt、发送链：未修改。仅在明确发布后将所请求的测试字段加入已有模型上下文。

## 测试记录

- 编译：`dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -v q` 成功，0 警告、0 错误；主项目作为引用一起编译。
- 回归：446/446 检查通过（基线 427，新增 19）；日志 `Output/ValidationV121/regression.log`。原入口离线回归 17/17，通过的 qnbot 核心源文件检查数为 6，ForbiddenBusinessCalls=0。
- 生命周期：Draft → Reviewing → Approved → Published 的每个阶段均检查；仅 Published 且授权 AI 使用的 `Capacity=200ml` 返回成功。Rejected Material、未发布 Approved、未授权 Published 均返回 Missing；缺少审核状态文件时抛错而不读旧字段。
- 版本与隔离：返回版本 `test-v3`、来源 `ManualInput/test-fixture:v121`；跨店铺请求 MissingProduct；同店铺商品字段的第二个活动发布被拒绝。服务重建后状态仍在。
- AI 链路：本地模拟 AI 回调 1 次，读取到已发布的 `Capacity=200ml`，生成 1 条 `KnowledgeStatus=Available` 的 Shadow Reply Preview。**没有在本轮调用外部模型**，因此不把这次结果称为真实模型质量验证。
- 安全：无 `send-request.json`，SentMessages=0；未修改真实千牛发送逻辑。审核日志不包含测试知识正文。

## 文件与提交

实现提交：`55fd8e1e4b3473e17429ae623ed4d7348456941e`。新增 `Models/KnowledgeItem.cs`、`Knowledge/KnowledgeReviewService.cs`、`Validation/ChangeDetectionChecks/KnowledgeWorkflowChecks.cs`；修改 `Models/ProductKnowledgeRecord.cs`、`Models/KnowledgeResult.cs`、`Models/MessageSession.cs`、`Knowledge/JsonProductKnowledgeRepository.cs`、`Knowledge/ProductKnowledgeProvider.cs`、`Shadow/AgentPipelineTrace.cs`、`Shadow/QnocrShadowRunner.cs`、`Program.cs`、`Validation/ChangeDetectionChecks/Program.cs`；删除文件：无。本报告另行提交。

## 当前边界与下一步

当前达到“本地测试 Knowledge Item → 显式人工审核 → Published → 测试 AI 上下文”的基础链路。若进入真实商品知识录入，先设计受控录入与操作员鉴权，并决定如何迁移旧测试字段；不要将本轮本地 JSON 审核状态当作生产审批系统。JEV Intent Provider 与 Agent Strategy Layer 留待后续单独评估。
