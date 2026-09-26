# V1.17 单商品 Product Knowledge Provider PoC 验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`71d1740e5bce1e98d4022afba81f2001cb36458c`。本报告单独提交。

## 结论

已建立一条**显式启用、仅限单条本地合成测试商品**的 `ProductReference → ProductKnowledgeProvider → KnowledgeResult → 隔离AI模型上下文 → Reply Preview` 链。`1051087533910` 的测试数据只含Capacity、Material、CustomizationPolicy。它不是经过千牛或商品平台验证的真实商品资料，默认运行仍使用V1.16的EmptyKnowledgeProvider；本阶段没有完整知识库或自动发送。

## 修改原因与架构影响

V1.16仅能表示“缺少知识”，无法验证商品事实能否在不改qnbot核心Prompt/Provider接口的情况下进入AI。V1.17新增`ProductKnowledgeRecord`（ProductId、Dictionary Fields、Source、UpdatedAt、IsTestData）与`ProductKnowledgeProvider`。JSON只允许一个固定测试商品、三个白名单字段、16 KiB以内文件；不支持批量导入。

- `MessageEnvelope`：未修改；原`context.json`亦未覆盖。
- qnbot核心与Decision分类器：未修改。`防水等级`由KnowledgeRequestBuilder识别为所需`Waterproof`字段，不改V1.15的`OTHER` Intent。
- AI Provider接口与qnbot生成服务：未修改。隔离兼容宿主的**扩展层**在原模型问题文本后附加经过校验的测试事实/缺失字段块，qnbot核心Prompt模板保持原样；`--product-knowledge`仅在隔离AI Shadow、测试身份映射与指定Session下显式启用。
- Knowledge Interface：`KnowledgeResult`可携带ReturnedFields和UpdatedAt。日志只记返回字段**名称**、Source、MissingFields，不记商品值。成功字段写入单次调用专用`model-context.json`；当前ProductId不匹配、为空或字段缺失时不提供事实。
- 人工预览：标记KnowledgeStatus、KnowledgeSource、KnowledgeUsedByAi和KnowledgeGuardApplied。对缺失字段，下游AI仍可被调用，但**面向人工的预览**会改为“当前缺少已确认的商品信息，请人工核实后答复。”，不展示无依据的产品断言。原始AI调试回执仍可能保存原模型文本于本地忽略目录，不能作为可发送答案。

## 修改文件

新增：`Models/ProductKnowledgeRecord.cs`、`Models/KnowledgeContextView.cs`、
`Knowledge/ProductKnowledgeProvider.cs`、`Knowledge/KnowledgeModelContextWriter.cs`、
`Knowledge/product-knowledge.json`、`Validation/ChangeDetectionChecks/ProductKnowledgeChecks.cs`、
`Validation/ChangeDetectionChecks/ProductProviderLiveSmoke.cs`及本报告。
修改：`Integration/AgentContextWire.cs`、`Knowledge/KnowledgeRequestBuilder.cs`、
`Models/AssistedReply.cs`、`Models/KnowledgeResult.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/AgentPipelineTrace.cs`、`Shadow/AssistantReplyService.cs`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、`Shadow/ShadowAdapterClient.cs`、
`Validation/ChangeDetectionChecks/Program.cs`、`Validation/QnbotCompatibility/Program.cs`。
删除：无；qnbot工作树未改。

## 编译、回归与单商品用例

.NET 8主项目与验证项目Build Success、0警告、0错误；.NET Framework 4.8隔离宿主Build Success、0错误（完整构建可能有上游既有CS4014警告）。累计检查 **374/374**，高于353项基线；原入口 **17/17**，`ForbiddenBusinessCalls=0`。

| 用例 | 知识结果 | AI/预览结果 |
|---|---|---|
| “这款有200ml规格吗？” | PRODUCT_SPEC_QUERY→Capacity；本地测试记录返回Success=true、`100ml, 200ml` | 合成AI回调读取单次`model-context.json`中的Capacity，预览含200ml、KnowledgeStatus=Available、KnowledgeUsedByAi=true |
| “什么材质？” | Material=`TA1纯钛`，Success=true | 合成AI回调得到Material，预览标记Available |
| “防水等级是多少？” | Decision仍为OTHER；KnowledgeRequestBuilder请求Waterproof；测试记录无此字段，MissingFields=`Waterproof` | 故意注入下游“支持IPX8防水”的测试回调，最终人工预览不含IPX8、改为待核实答复，KnowledgeGuardApplied=true |

未知ProductId不会命中测试记录；本地数据源拒绝额外商品字段及凭证样式顶层字段；未启用AI Shadow时无法打开`--product-knowledge`。合成链路无发送请求，SentMessages=0。

## 隔离宿主与真实AI边界

离线`--agent-context-check`将同一条合成问题分别通过原Context和附带Capacity事实的model-context处理：模型问题长度由12增至105，`KnowledgeFactsPresent=true`、KnowledgeStatus=Available、AI Calls=0、SentMessages=0。把model-context中的ProductId改为另一商品时，隔离宿主以`InvalidDataException`拒绝，未调用AI。事实以附加用户问题上下文进入未修改的qnbot生成服务；这证明模型**可收到**事实，不等于真实模型已经按事实回答。

隔离宿主只读预检确认Provider为OpenRouter `openai/gpt-5.5`、Send OFF、未启动Bot或发送入口。合成OpenRouter实调状态：待本轮明确授权；未获授权前不会调用外部端点。默认EmptyProvider和本地合成回调测试均不访问外部AI。所有测试仍为Shadow，Send OFF。

## 当前状态与下一步

单商品Provider与知识→模型输入的接口PoC已形成。测试JSON中的“200ml/TA1纯钛/支持刻字”未经真实商品核对，不能用于真实客服确认或自动发送。下一阶段应先核实商品事实来源、更新时效和多商品隔离，再设计多商品Provider；若Intent分类误判成为主要瓶颈，再考虑独立JEV Intent Provider PoC。本阶段无FAQ、RAG、向量库或完整商品系统。

本地证据（`Output/`被Git忽略）：`Output/v117-checks.log`、
`Output/ValidationV117/original-entry-final/run.log`、`Output/ValidationV117/product-*/`中的
`capacity-state`、`material-state`、`waterproof-state`、隔离宿主`host-model-confirmed/context-check.json`和`host-tampered.log`。
