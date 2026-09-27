# V1.25 Knowledge Source Adapter 验证报告

## 结果

已建立 `ProductKnowledgeProvider → ProductKnowledgeRepository → IKnowledgeSource`。现有 JSON 与审核状态文件的解析、验证和只读版本查询已移到 `LocalFileKnowledgeSource`；通用 Repository 只调用接口并校验 SellerId/ProductId 范围，不访问路径、文件或 JSON。V1.19 的 `JsonProductKnowledgeRepository(path, workflowItemsPath)` 作为兼容外壳保留，现有调用无需迁移。

## 修改原因与架构影响

此前 Repository 自身直接读取本地 JSON，使未来更换存储来源时需要改动读取层。本轮抽出 `IKnowledgeSource`，定义按 SellerId/ProductId 读取商品记录、按字段/版本进行显式历史审计查询，并暴露独立的存储来源类型 `KnowledgeStorageSourceType`。目前只实现 `LocalFile`；`Feishu`、`Database`、`Obsidian` 是预留类型，没有连接外部服务。Shadow Runner 现在显式组装 `ProductKnowledgeRepository(new LocalFileKnowledgeSource(...))`。

`ProductKnowledgeRecord` 与 `KnowledgeResult` 增加可选存储来源类型元数据；知识事实的 `Source` 与条目来源 `KnowledgeSourceType` 保持原有语义。`AgentPipelineTrace` 在成功完成来源读取后增加 `KNOWLEDGE_SOURCE_READ`，记录 SourceType、SellerId、ProductId、Version，不记录知识正文。缺失商品的已执行读取也保留来源类型，Version 为未知。`MessageEnvelope`、qnbot 核心、Knowledge Provider 接口、Knowledge Workflow、AI Provider、Prompt、发送链均未修改。

## 文件

实现提交：`29cd96e6a1044bc03030e3e713dc42c680a65a25`。新增 `Knowledge/IKnowledgeSource.cs`、`Knowledge/LocalFileKnowledgeSource.cs`、`Knowledge/ProductKnowledgeRepository.cs`、`Models/KnowledgeStorageSourceType.cs`、`Validation/ChangeDetectionChecks/KnowledgeSourceAdapterChecks.cs`；修改 `Knowledge/JsonProductKnowledgeRepository.cs`、`Knowledge/ProductKnowledgeProvider.cs`、`Models/ProductKnowledgeRecord.cs`、`Models/KnowledgeResult.cs`、`Models/MessageSession.cs`、`Shadow/AgentPipelineTrace.cs`、`Shadow/QnocrShadowRunner.cs`、`Program.cs`、测试入口；删除文件：无。

## 测试

- 编译：检查项目和本地 Knowledge Console 项目均成功，0 警告、0 错误。
- 回归：508/508 检查通过（V1.24 基线 495，新增 13）；原入口离线回归 17/17，ForbiddenBusinessCalls=0。检查日志在 `Output/ValidationV125/regression.log`。
- 数据读取：`LocalFileKnowledgeSource` 读取现有测试商品 `Capacity=100ml, 200ml`、版本 `test-v1`；旧构造器与新 Repository 返回相同字段。SellerId 不匹配返回空，不允许跨店铺借用。
- 架构：以内存假源模拟 `Database` 类型，通用 Repository 与 Provider 无需文件路径即可返回其隔离测试记录；假源返回错误 SellerId 时，Repository 拒绝。没有访问真实数据库或外部知识源。
- Source Trace：`KNOWLEDGE_SOURCE_READ` 包含 `LocalFile`、`seller-test`、商品 ID、`test-v1`，不含容量正文；随后保留原 `KNOWLEDGE_RESULT`。缺失商品的读操作仍标记 LocalFile 来源。
- AI：本地模拟 AI 回调 1 次，得到 `KnowledgeStatus=Available` 的 Shadow Reply Preview 1 条；外部 AI 调用 0 次。当前只验证适配层不改变模型输入和预览流程，不评价真实模型效果。
- 安全：SentMessages=0，无 send request；未修改真实千牛发送路径。

## 当前状态与下一步

已达到“Knowledge Provider → 通用 Repository → LocalFile Source Adapter”。后续可以单独设计 Feishu/数据库等外部来源的接口契约、安全与审计，或评估 JEV Intent Provider、Agent Strategy Layer；本阶段没有接入任何外部知识源，也没有迁移已有数据。
