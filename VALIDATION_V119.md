# V1.19 多商品 Knowledge Repository 架构验证报告

日期：2026-09-27（Asia/Shanghai）。实现提交：`d464638225bd99f3994613685270ca6bd8421edd`。本报告单独提交。

## 结论

已建立`(SellerId, ProductId) → IProductKnowledgeRepository → ProductKnowledgeProvider → KnowledgeResult → AI模型上下文/人工预览`的多商品基础架构。Provider不再知道JSON路径或文件格式；默认运行仍使用EmptyKnowledgeProvider，只有隔离AI Shadow显式指定本地**合成测试文件**才启用多商品数据。没有真实商品批量导入、知识库后台、RAG、向量检索或自动发送。

## 修改原因与架构影响

V1.17的Provider自己加载一条JSON记录，无法区分商品，也无法防止不同店铺复用相同ProductId。V1.19新增`IProductKnowledgeRepository.GetAsync(sellerId, productId)`和只读`JsonProductKnowledgeRepository`。Repository一次性读取小型本地测试文件，精确按店铺+商品键索引；拒绝重复键、额外属性、未标记测试的数据以及超限记录；返回防御性副本。它兼容旧单商品JSON，并支持`Products`数组。`ProductKnowledgeRecord`增加SellerId与Version，保留Source/UpdatedAt；缺失店铺或商品时`KnowledgeResult.MissingProduct=true`，不回退其他商品。

- `MessageEnvelope`、qnbot核心、Decision Layer和AI Provider接口：未修改；qnbot工作树干净。
- Knowledge Interface：`KnowledgeRequest`增加SellerId，`KnowledgeResult`增加MissingProduct、ProductVersion和SellerId，均采用兼容旧读取的可选字段；`IProductKnowledgeRepository`为未来其他来源提供替换点。本阶段不建立数据库。
- `ProductKnowledgeProvider`只依赖Repository，不包含JSON解析。模型上下文只接受与当前消息SellerId/ProductId一致、带来源和版本的本地测试字段。隔离宿主扩展层同样校验店铺与商品，qnbot核心Prompt模板不变。
- Knowledge Trace：KNOWLEDGE_REQUEST记SellerId、ProductId、NeedFields；KNOWLEDGE_RESULT记SellerId、ProductId、Version、Source、ReturnedFields名称、MissingFields和MissingProduct，不写商品值或买家问题正文。人工预览单列CurrentProductId、知识Version与MissingProduct，避免把历史引用当作当前商品。

## 修改文件

新增：`Knowledge/IProductKnowledgeRepository.cs`、`Knowledge/JsonProductKnowledgeRepository.cs`、
`Knowledge/product-knowledge.multi.sample.json`、
`Validation/ChangeDetectionChecks/MultiProductKnowledgeChecks.cs`及本报告。
修改：`Knowledge/ProductKnowledgeProvider.cs`、`Knowledge/KnowledgeRequestBuilder.cs`、
`Knowledge/KnowledgeModelContextWriter.cs`、`Knowledge/product-knowledge.json`、
`Models/ProductKnowledgeRecord.cs`、`Models/KnowledgeRequest.cs`、`Models/KnowledgeResult.cs`、
`Models/KnowledgeContextView.cs`、`Models/MessageSession.cs`、`Models/AssistedReply.cs`、
`Integration/AgentContextWire.cs`、`Shadow/DurableShadowDispatch.cs`、`Shadow/AgentPipelineTrace.cs`、
`Shadow/AssistantReplyService.cs`、`Shadow/QnocrShadowRunner.cs`、`Program.cs`、`README.md`及验证入口/夹具。
删除：无。

## 编译、回归与多商品结果

.NET 8主项目和验证项目Build Success，0警告、0错误；.NET Framework 4.8隔离宿主Build Success，0错误。累计检查 **405/405**，高于387项基线；qnbot原入口 **17/17**，`ForbiddenBusinessCalls=0`。

| 查询作用域 | 测试商品 | 结果 |
|---|---|---|
| seller-test + Product A `1051087533910` | Capacity=100ml，Version=test-v1 | 只返回A的字段与版本 |
| seller-test + Product B `2000000000001` | Capacity=200ml，Version=test-v2 | 只返回B的字段与版本，不带A的100ml |
| seller-other + 与A相同ProductId | Capacity=500ml，Version=other-store-test-v1 | 不复用seller-test的A记录 |
| seller-test + Product C `3000000000001` | 不存在 | Success=false、Source=None、MissingProduct=true、MissingFields=[Capacity]；没有A/B字段 |

Repository重复SellerId/ProductId键被拒；返回记录的Fields被调用者修改后，再次查询仍保持原值；恶意Repository返回另一店记录时Provider以`InvalidDataException`拒绝。旧单商品文件已显式补SellerId/Version，V1.17单商品回归仍通过。

## AI链路、宿主隔离与安全

合成Agent回调在Product B单次`model-context.json`中看到SellerId=seller-test、ProductId B、Version=test-v2、Capacity=200ml；Shadow预览KnowledgeStatus=Available、KnowledgeUsedByAi=true、CurrentProductId=B、ProductKnowledgeVersion=test-v2。不存在的C只进入Missing状态且MissingProduct=true；即使测试回调返回“有200ml”，人工预览也被改为待核实答复。合成AI回调按消息各1次、SentMessages=0，状态目录没有发送请求。

隔离qnbot宿主的离线`--agent-context-check`对B报告KnowledgeFactsPresent=true、Version=test-v2、模型问题长度135、AI Calls=0、SentMessages=0；把model-context中的SellerId篡改为另一店时以`InvalidDataException`拒绝。本阶段**没有新的外部AI模型调用**；回调和离线宿主只证明知识字段正确进入现有模型问题路径，不冒充真实模型质量验收。测试JSON全部为合成数据，不能作为已核实的客服商品事实。

## 当前状态与下一步

已达到架构和隔离测试层面的“ProductId → Multi Product Knowledge Provider → AI Reply Preview”。数据模型仍是有界本地测试夹具，非正式多商品知识系统。下一步优先设计真实商品知识数据模型、来源校验、版本/时效规则及店铺隔离授权，再决定是否实现正式Repository；若后续Intent误判成为主要瓶颈，可另做JEV Intent Provider PoC。Agent Strategy Layer也应由更多人工反馈驱动，不能根据本轮合成商品数据自动改变策略。

本地证据（`Output/`被Git忽略）：`Output/v119-checks.log`、
`Output/ValidationV119/original-entry-final/run.log`、`Output/ValidationV119/multi_*/`内的
`product-b-state`、`missing-c-state`、`host-product-b/context-check.json`与`host-tampered-seller.log`，
以及`Output/ValidationV119/product-b-preview-cli.log`。
