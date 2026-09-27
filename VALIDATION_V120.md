# V1.20 Product Knowledge Schema商品知识模型验证报告

日期：2026-09-27（Asia/Shanghai）。实现提交：`a5919dfce3a1bf56ae1c6e470865fa889e09660d`。本报告单独提交。

## 结论

已建立源无关的`ProductKnowledgeProfile`结构，包含Identity、Specification、Material、Features、Scenarios、FAQ、AfterSales、Restrictions和Metadata。Identity中的SellerId/ProductId必填，其他分区可选。新Profile作为`ProductKnowledgeRecord`的可选字段进入原Repository；旧`Fields`格式仍可读取，重叠字段若与结构化值冲突则拒绝。样例只有一条**合成测试商品**，不是正式商品知识库。

## 修改原因与架构影响

V1.19的`Dictionary Fields`足以回答少量参数，但不能稳定表达FAQ、限制、场景和来源元数据。V1.20拆分类型、增加Schema校验，让未来JSON、数据库、ERP或后台可映射同一目标形状；本阶段没有实现这些来源或资料录入流程。

- `MessageEnvelope`、qnbot核心、AI Provider和Decision Layer：未修改；qnbot工作树干净。
- Knowledge Interface：`IKnowledgeProvider`与`IProductKnowledgeRepository`方法签名不变；`KnowledgeResult`兼容地增加KnowledgeCategory。类别包含Specification、Material、Feature、Scenario、FAQ、AfterSales、Restriction和Mixed等，当前Provider实际只对已请求的规格、材质及旧CustomizationPolicy字段赋类。
- Repository：兼容旧单商品/多商品`Fields` JSON；新Schema记录可用空`Fields`加Profile。校验必填SellerId/ProductId、可选章节内容边界、外层身份及Metadata一致性；返回深拷贝，不能由调用者改动缓存。旧Fields与新Profile中同一容量/材质值冲突则拒绝。
- Provider：先读取结构化Specification/Material，缺失时回退旧Fields；未查询FAQ、Restrictions、Features或Scenarios，不把整个Profile送入AI。知识类别只标示返回字段类型，不代表事实经过平台核实。
- 隔离AI扩展：已请求的测试规格/材质随类别进入单次model-context；qnbot核心Prompt模板、原context.json与发送链不变。

## 修改文件

新增：`Models/ProductKnowledgeProfile.cs`、`Knowledge/ProductKnowledgeSchemaValidator.cs`、
`Knowledge/product-knowledge.schema.sample.json`、
`Validation/ChangeDetectionChecks/ProductKnowledgeSchemaChecks.cs`及本报告。
修改：`Models/ProductKnowledgeRecord.cs`、`Models/KnowledgeResult.cs`、
`Models/KnowledgeContextView.cs`、`Models/MessageSession.cs`、`Models/AssistedReply.cs`、
`Knowledge/JsonProductKnowledgeRepository.cs`、`Knowledge/ProductKnowledgeProvider.cs`、
`Knowledge/KnowledgeModelContextWriter.cs`、`Integration/AgentContextWire.cs`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/AgentPipelineTrace.cs`、
`Shadow/AssistantReplyService.cs`、`Shadow/QnocrShadowRunner.cs`、
`Validation/QnbotCompatibility/Program.cs`、`Program.cs`、`README.md`及验证入口。
删除：无。

## 编译、回归与Schema验证

.NET 8主项目和验证项目Build Success，0警告、0错误；.NET Framework 4.8隔离宿主Build Success、0错误（完整构建可出现上游既有CS4014警告）。累计检查 **427/427**，高于405项基线；原qnbot入口 **17/17**，`ForbiddenBusinessCalls=0`。

新Schema样例`3000000000002`包含规格（100ml、200ml、测试尺寸/重量）、TA1纯钛材质、1条FAQ和1条限制。Features、Scenarios、AfterSales可作为可选分区，Identity-only Profile也合法；缺少必填SellerId/ProductId、与外层身份或版本元数据不一致、FAQ/限制内容为空、容量内容超界、新旧Capacity冲突均被拒。Repository深拷贝结构化列表；V1.17单商品及V1.19多商品旧Fields回归继续通过。

| Provider请求 | KnowledgeResult | Category |
|---|---|---|
| Capacity | `100ml, 200ml` | Specification |
| Material | `TA1纯钛` | Material |
| Size | `测试尺寸` | Specification |
| Capacity+Material | 两项均返回 | Mixed |

## AI链路与安全边界

合成Agent回调仅看到`model-context.json`内明确请求的Capacity及`KnowledgeCategory=Specification`，没有FAQ/Restrictions分区；模拟AI适配器调用1次、Reply Preview正常显示CurrentProductId、Available、Specification、`KnowledgeUsedByAi=true`，`SentMessages=0`。隔离qnbot宿主离线`--agent-context-check`显示`KnowledgeFactsPresent=true`、KnowledgeCategory=Specification、PromptLength=178、AI Calls=0、SentMessages=0。本阶段未发起新的外部模型调用，不能把合成回复当作真实AI质量结论。样例内容均为测试数据，不能直接用于真实客服商品承诺。

无真实商品导入、FAQ检索系统、RAG、向量库、运营后台、自动知识生成、自动修改或自动发送。原有未验证Buyer身份与Send OFF边界保持。

## 当前状态与下一步

已达到架构层面的“Product → Knowledge Schema → Knowledge Provider → AI Reply Preview”，同时保留旧Fields兼容。下一阶段优先设计商品知识的人工录入/审核流程、数据来源证明、版本与时效规则，再考虑正式数据源；JEV Intent Provider和Agent Strategy Layer应由更多人工反馈驱动，不能因本轮Schema样例自动接入。

本地证据（`Output/`被Git忽略）：`Output/v120-checks.log`、
`Output/ValidationV120/original-entry-final/run.log`、
`Output/ValidationV120/schema_*/`内的合成state、`host-schema/context-check.json`，
以及`Output/ValidationV120/schema-preview-cli.log`。
