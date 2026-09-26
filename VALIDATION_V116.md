# V1.16 Knowledge Interface只读知识接口层验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`f0424a92fba483b77f3d38b59b70eab554bbfe38`。本报告单独提交。

## 结论

已建立 `Decision → KnowledgeRequestBuilder → IKnowledgeProvider → 原AI适配器 → Reply Preview` 的只读接口链。默认`EmptyKnowledgeProvider`没有网络、数据库或文件读取；对所需字段一律返回`Success=false`、`Source=None`、`Confidence=null`、空`Content`及`MissingFields`。知识缺失或Provider异常时AI仍沿原输入生成Shadow回复；程序没有自动发送。

## 修改原因与架构影响

V1.15只识别“需要商品上下文/知识信息”，未定义从哪里请求。V1.16增加`KnowledgeRequest`（SessionId、当前观察到的ProductReference、Intent、最新买家Query、NeedFields、MessageId/CaptureId）与`KnowledgeResult`（Success、Source、Confidence、Content、MissingFields），以及可替换`IKnowledgeProvider`。字段名仅是接口需求：容量`Capacity`、发货`ShippingTime`、材质`Material`、价格`CurrentPrice`、定制`CustomizationPolicy`、售后`ReturnPolicy`；实际没有商品事实。兼容`AFTER_SALES_QUERY`与`AFTERSALE_QUERY`两种售后Intent拼写。

- `MessageEnvelope`和原`context.json`：未修改；知识请求的Query只在内存传给空Provider，不新增正文日志。
- qnbot核心、AI Provider、Prompt、模型问题文本与生成策略：未修改；qnbot工作树干净。
- Decision Layer：分类规则不变，新增按Intent生成字段需求的旁路；当前ProductId为空时`ProductReference=null`，不复用历史商品ID。
- Shadow扩展：AI调用前写`KNOWLEDGE_REQUEST / KNOWLEDGE_RESULT`，字段含SessionId、Intent、ProductReference、NeedFields、Success、Source、MissingFields；Outbox只保存知识**结果元数据**，不保存Content。Provider错误另写`KNOWLEDGE_ERROR`、计入KnowledgeErrors并降级Missing；AI继续。`NeedKnowledge=false`时跳过Provider，预览状态`NotRequired`。
- 新Reply Preview兼容地增加`KnowledgeStatus`（本轮Empty结果为`Missing`，`Available`只为未来预留）；旧预览字段为空仍可读取。人工视图同步显示知识状态。发送、知识库、FAQ、RAG、向量库和真实商品数据均未接入。

## 修改文件

新增：`Knowledge/IKnowledgeProvider.cs`、`Knowledge/EmptyKnowledgeProvider.cs`、
`Knowledge/KnowledgeRequestBuilder.cs`、`Models/KnowledgeRequest.cs`、`Models/KnowledgeResult.cs`、
`Validation/ChangeDetectionChecks/KnowledgeChecks.cs`及本报告。
修改：`Models/AssistedReply.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/AgentPipelineTrace.cs`、`Shadow/AssistantReplyService.cs`、
`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、
`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`及其`Program.cs`。删除：无。

## 编译、回归与接口检查

.NET 8主项目及验证项目Build Success、0警告、0错误；.NET Framework 4.8隔离兼容宿主Build Success、0错误（完整构建时上游`BuyerMessageBurstCoordinator.cs`可出现既有CS4014警告）。累计检查 **353/353**，高于342项基线；qnbot原入口 **17/17**，`ForbiddenBusinessCalls=0`。

| 输入/状态 | Decision | KnowledgeRequest/Result |
|---|---|---|
| “这款有200ml规格吗？”且当前ProductId可见 | `PRODUCT_SPEC_QUERY` | NeedFields=`Capacity`；ProductReference只标记当前帧观察。空Provider返回`MissingFields=[Capacity]`、Success=false、Source=None |
| “多久发货？”且当前ProductId为空 | `SHIPPING_QUERY` | NeedFields=`ShippingTime`；ProductReference=null，即便Session保留历史商品引用。空Provider返回Missing |
| `OTHER`且NeedKnowledge=false | `OTHER` | Provider调用0；预览`KnowledgeStatus=NotRequired` |
| Provider抛异常 | 原Intent保持 | 写`KNOWLEDGE_ERROR`和Missing结果，KnowledgeErrors=1；AI仍生成预览 |

合成Agent端到端测试记录`DECISION_CREATED → KNOWLEDGE_REQUEST → KNOWLEDGE_RESULT → AI_REQUEST → AI_RESPONSE → REPLY_PREVIEW_CREATED`；原适配器调用1次、模拟AI Calls=1、Reply Preview=1、`KnowledgeStatus=Missing`、`SendStatus=Shadow`、`SentMessages=0`。回调检验`context.json`与`envelope.json`未加入决策/知识内容。日志不写入买家Query、API Key或知识Content；`knowledge-result.json`只含缺失元数据。人工CLI预览显示DecisionRisk=Medium、身份Risk=High和KnowledgeStatus=Missing；V1.14旧真实预览仍可读，其Decision/KnowledgeStatus为空。本轮**没有新的外部AI调用**；模拟调用只验证原链路在缺知识时继续。

## 当前状态与下一步

已达到接口层面的“Decision Layer → Knowledge Interface → AI Reply”。本阶段没有可用知识，`Missing`不能解释为“查询过真实商品库且未命中”；AI仍可能给出需要人工核对的泛化建议。下一阶段更应先设计第一版只读商品Knowledge Provider的数据来源、可信度、时效和授权边界，再考虑接入真实商品资料。若后续人工Intent样本显示规则不足，再做独立JEV Intent Provider PoC；当前不扩展。

本地证据（`Output/`被Git忽略）：`Output/v116-checks.log`、
`Output/ValidationV116/original-entry-final/run.log`、`Output/ValidationV116-preview-cli.log`、
`Output/ValidationV116-legacy-preview.log`，以及`Output/ValidationV15/`最新合成运行的
`decision.json`、`knowledge-result.json`与`agent-pipeline.jsonl`。
