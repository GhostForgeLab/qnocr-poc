# V1.23 Knowledge Import + Revision 验证报告

## 结果

本轮新增单商品测试 JSON 导入和单字段知识修订。导入仅生成 `KnowledgeDraft`；修订在已发布 v1 上创建新的 Draft v2。人工提交审核、批准并显式执行修订发布后，Provider 才从 v1 切换至 v2。v1 保留为 Published 历史条目，但撤销其 AI 使用许可。并行修改同一基线字段的第二个修订记录为 `Conflict`，不创建可审核候选项，也不自动合并。

## 修改原因与架构影响

V1.22 的录入服务没有导入请求标识，也没有安全替换已发布字段的版本操作。新增 `KnowledgeImportRequest`（含 ImportId、SellerId、ProductId、Source、RawData、CreatedAt）与单商品 `KnowledgeImportService`：严格解析受限 JSON，映射到现有 Authoring Service，使用 ImportId/RawData 哈希防止重试创建重复草稿；原始数据不进入审计日志。

新增 `KnowledgeRevision`、字段级 `KnowledgeFieldChange`（OldValue、NewValue、Reason）及 `KnowledgeRevisionService`。修订必须引用当前唯一的 Published 且允许 AI 使用的基线条目，候选版本号不得重复。V1.21 `KnowledgeReviewService` 增加人工触发的 `PublishReplacement`：一次状态写入保留旧版本并激活新版本，审核日志分别记录 supersede/publish。Repository 默认只返回当前 AI 可用 Published 条目；新增显式 `GetPublishedVersionAsync` 供历史审计查看，不供 AI Provider 使用。

`MessageEnvelope`、qnbot 核心、原有 `ProductKnowledgeProfile` Schema、Decision Layer、AI Provider 和发送链未修改。`KnowledgeItem` 只增加可选 `SupersededByKnowledgeId` 版本关联。没有自动审批、自动发布、自动覆盖旧版本或自动生成可信事实。本轮仅支持单商品测试 JSON 和单字段修订，不接收批量真实商品。

## 文件与命令

实现提交：`b673c058b9aa31ab4ea14e15206f0311519ef72c`。

- 新增：`Models/KnowledgeImportRevision.cs`；`Knowledge/KnowledgeImportService.cs`、`Knowledge/KnowledgeRevisionService.cs`、`Knowledge/KnowledgeImportRevisionCommand.cs`；两个合成测试 JSON；`Validation/ChangeDetectionChecks/KnowledgeImportRevisionChecks.cs`。
- 修改：`Models/KnowledgeItem.cs`、`Knowledge/KnowledgeReviewService.cs`、`Knowledge/JsonProductKnowledgeRepository.cs`、`Program.cs`、`Shadow/QnocrShadowRunner.cs`、`Validation/ChangeDetectionChecks/Program.cs`。
- 删除：无。

本地测试入口：`--knowledge-import <json> <sellerId> <productId> <stateDir> <operator>`；`--knowledge-revision <json> <stateDir> <operator>`；`--knowledge-revision-show <stateDir> <revisionId>`；`--knowledge-revision-publish <stateDir> <revisionId> <operator>`。候选 KnowledgeItem 仍需通过 V1.22 `--knowledge-review submit` 和 `approve` 人工审核；普通 `--knowledge-review publish` 不会替代修订发布操作。

## 验证

- 编译：主项目及检查项目成功，0 警告、0 错误。
- 回归：485/485 项通过（V1.22 基线 465，新增 20）；原入口离线回归 17/17，ForbiddenBusinessCalls=0。检查日志：`Output/ValidationV123/regression.log`。
- 导入：合成测试 JSON 创建含 Capacity、Material、Feature 的 Draft；重复 ImportId 返回原 Draft，不重复创建知识；相同 ImportId 配不同 RawData、真实商品标记、未知凭证式属性均拒绝。
- 修订：v1 `Capacity=200ml` 保持默认可读，修订 v2 `Capacity=300ml` 在 Draft 和 Approved 阶段均不可读；显式发布后默认返回 v2。字段级 Old/New/Reason 与基线版本均保存。
- 版本：v1 Published 历史条目仍可用显式版本查询读取，`AiUseAllowed=false`；v2 为唯一 AI 可用的 Published 条目。历史版本号不能复用。
- 冲突：第二个针对同一基线 Capacity 的并行式修订为 `Conflict`，没有候选知识项，不会合并或覆盖。
- CLI：实际完成导入 Draft（3 个知识项）→ 人工审核发布 v1 → 修订 Draft → 人工审核 → 显式发布 v2。
- AI：本地模拟回调 1 次，生成使用 v2 规格的 Shadow Reply Preview 1 条；**本轮外部 AI 调用 0 次**，不评价真实模型效果。
- 安全：SentMessages=0，无发送请求；没有改动真实千牛发送路径。

## 当前状态与下一步

已验证“测试商品 JSON → Draft → 人工审核 → Published v1 → 修订 Draft v2 → 人工审核 → Published v2 → Shadow AI 使用”。导入与修订仍是本地测试流程，操作者标签不是生产鉴权，冲突修订需人工重新创建。后续可单独评估商品知识运营工具、JEV Intent Provider PoC 或 Agent Strategy Layer；真实商品导入前需补充操作员鉴权与更强的跨进程事务保障。
