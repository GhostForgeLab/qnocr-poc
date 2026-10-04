# V1.35.0-B Product Context → Existing Knowledge Integration

日期：2026-10-04（Asia/Shanghai）。基线 Commit：0c0d70cfc2cad06a2e35eda097139729452804cb。

## 判定

**本阶段代码与受控集成验收通过。** 链路已形成 Agent Context → 原 Intent → 原 Product Resolver / ProductReference → 原 IKnowledgeProvider → Knowledge Result，并停止在结果边界。

Knowledge 专项 **61/61**；原 Intent 专项 **66/66**；全量 **1087/1087**（原 1026 + 本阶段 61）；qnbot 兼容 **17/17**。主项目、验证项目与 RuntimeObserver 的 .NET 8 编译均 0 Warning / 0 Error。net48 兼容宿主保留上游 CS4014 1 Warning / 0 Error。

本轮 MessageEnvelope / OCR Product Context / 两个 Buyer / 两个 Seller 为受控输入；JEV 使用受控 HTTP 返回；Knowledge 实际读取由原 Review Service 显式创建的本地 Published 测试数据。不是新一轮千牛真实采集、外部 JEV 网络测试或真实多买家验收。真实多买家继续 Pending，不阻塞本阶段。

Identity 保持 Observed；Shadow；Intent 与 Knowledge 允许执行；AI Reply=0、Prompt 组装=0、Reply Preview=0、自动发送=0、SentMessages=0。上述零计数依据是集成没有对应服务依赖或分发入口，并检查未产生模型输入、prompt、preview 或 send-request 文件；不把原有回归中的其他业务 mock 路径混算为本次运行。

## 现有 Product Context 架构与实际阻塞点

| 位置 | 当前实际职责 |
| --- | --- |
| Models/ProductContext.cs、Product/ProductContextResolver.cs | OCR ID / profile catalog 候选与匹配结果，不是当前 Session 的可靠绑定。 |
| Models/BoundProductContext.cs | 当前 SessionId + SellerId + ProductId、商品名、Source、Confidence、CapturedAt、BoundBy、CaptureId。 |
| Models/ProductReference.cs | 商品引用及 capture / association；不能仅凭一个历史 ID 证明当前消息在问该商品。 |
| Product/ManualProductResolver.cs | 原 operator 显式绑定、清除与 2 小时有效期，要求已有 64-hex MessageSession sidecar；禁止无 clear 静默换商品。 |
| Product/OcrProductResolver.cs | 原同帧 product-region 标记 ID，要求唯一、置信度 >=0.80 和非空 CaptureId。 |
| Product/SessionProductContextResolver.cs | 组合 Manual + OCR；低可信/歧义为 Uncertain，冲突为 Conflict；不查历史候选。 |
| Shadow/SessionRegistry.cs 的 BindCurrentProduct | 原绑定范围/同帧校验、ProductReference 与审计保存。 |
| Extension/AgentContext/AgentSession.cs | CurrentProductContext 仍是预留只读 null 字符串；不能直接拿它代替 BoundProductContext。 |

实际接口差异：Agent SessionId 是 32-hex Guid，而 Manual Resolver 要求 64-hex，并读取另一种 MessageSession JSON。不能直接将 AgentSession 写成原 sidecar 或改变 Context ID 规则。

最小适配方案已实现于 AgentProductContextAdapter：以 SHA-256("AgentProductResolver/v1:" + 原 Agent SessionId) 生成 namespaced 64-hex ResolverSessionId，并投影原 SellerId / BuyerId / 时间到原 MessageSession。这只是原 resolver 的输入副本，不调用 SessionRegistry.Resolve、不创建第二个生命周期、不中断原 SQLite 的事实来源。agent-session-link.json 保存两种 ID 的对应关系。若已有副本的 ID、Seller、Buyer 或 projection 标记不符，拒绝使用。

原 Manual / OCR / 组合 Resolver 与 BindCurrentProduct 均未改动。只有调用 Resolve 后，将返回 BoundProductContext 的 SessionId 映射回原 Agent SessionId，再交给原 KnowledgeRequestBuilder。查询及最终结果始终携带原 Agent ID。

## 现有 Knowledge 架构与 Published 边界

| 位置 | 当前实际职责 |
| --- | --- |
| Knowledge/IKnowledgeProvider.cs | GetAsync(KnowledgeRequest, CancellationToken) → KnowledgeResult。未扩接口。 |
| Knowledge/KnowledgeRequestBuilder.cs | 当前问题与现有 Intent 的字段映射；对显式绑定验证 Session / Seller / Product / Capture。 |
| Knowledge/ProductKnowledgeProvider.cs | 从 Repository 读对应 seller/product，解析请求字段，返回结构化结果；保留原 scope 与 Published 校验。 |
| Knowledge/ProductKnowledgeRepository.cs | 验证 Source 返回的 seller/product；委托 IKnowledgeSource；附加 StorageSourceType。 |
| Knowledge/IKnowledgeSource.cs、LocalFileKnowledgeSource.cs | 原 LocalFile adapter；cache legacy records，每次查询重新读取 workflow 状态。 |
| Models/KnowledgeRequest.cs、KnowledgeResult.cs、KnowledgeItem.cs | 原请求、结果、状态、版本、审阅、发布许可、来源模型。 |
| Knowledge/KnowledgeReviewService.cs 等 | 原 Draft → Reviewing → Approved → Published；本阶段运行路径不执行写作/审阅/发布。 |

另一实际安全差异：LocalFile Source 在没有 workflowItemsPath 时会允许 legacy Fields/Profile。新 Agent 链路明确要求非空 workflowItemsPath，构造原 ProductKnowledgeProvider → ProductKnowledgeRepository → LocalFileKnowledgeSource 的 workflow 模式；不允许选择旧无 workflow 模式。

原 Source 只返回 Published + AiUseAllowed + ApprovedBy + ReviewedBy + ReviewedAt 合格的项目，Provider 继续验证 item/version 的 Published 状态和 seller/product。Draft、Reviewing、Approved 未发布、Published 但未许可均不会产生事实命中。workflow 文件丢失或损坏时原 Source 抛错，集成返回 KnowledgeUnavailable，不降级读取 legacy 值。

未新增 Provider 体系、知识 Schema、Product Store、Workflow、外部 Source、商品模型或查询算法。新 Pipeline 使用 IKnowledgeProvider 字段调用，默认具体实现是现有 ProductKnowledgeProvider。原 Shadow/DurableShadowDispatch 的下游会继续调用 AI，因此不用于本次停止于 Knowledge 的路径。

## 集成与执行语义

1. AgentKnowledgePipeline 独占原 AgentIntentPipeline；Context 保存与新增消息筛选沿用 V1.35.0-A。
2. 只消费本次返回的 AgentIntentMessageResult。消息重放、历史恢复和 seller-only 输入没有新 Intent，因此没有新 Knowledge。
3. Intent 错误/不可用时不查询；NeedKnowledge=false 时返回原 NotRequired。
4. 原 Product Resolver 只读取当前 Session 的显式 manual binding 或本次 product-region OCR。投影副本不拷贝旧 CurrentProductContext / ProductReferences 作为候选。
5. 将解析结果映射回 Agent ID，并使用原 AgentContextIntentAdapter 的当前目标消息 + 原 KnowledgeRequestBuilder 构建请求；绑定状态始终显式设置，避免触发 builder 的 legacy 当前 ID 分支。
6. SellerId 直接来自原 AgentSession.Identity；ProductId 只来自可靠 BoundProductContext。Envelope.ProductId、Intent 类型、其他 Session/global/Archived 商品都不能补齐它。
7. 缺商品、歧义、冲突、无 CaptureId、过期 manual binding 返回现有 MissingProductContext（等价 NoProductContext），ProviderCalled=false，KnowledgeCalls 不增加。缺 Seller 的 Envelope 在原 Context 校验处拒绝；Adapter 同样拒绝缺 Seller 的副本。
8. 有可靠上下文才查询；同 Seller 不同 Product、同 Product 不同 Seller严格区分。仍需商品证据的 shipping 查询不会因为 NeedProductContext=false 而放宽边界。
9. Semaphore 串行消费本 Pipeline 的新 Intent，避免并发重复 query。没有新建去重索引或分布式幂等机制。
10. Knowledge / resolver / 文件读取异常返回 KnowledgeUnavailable + Error 类型 + ProviderCalled；已保存 Context 与 Intent 不回滚，不执行自动重试或 AI。调用者主动取消则传播 cancellation。
11. 仅返回结果或由采集宿主保存 JSON 收据；不写 Knowledge 执行历史到 SQLite。

AgentKnowledgeMessageResult 包装原 KnowledgeRequest / KnowledgeResult，包含原 SessionId、MessageId、SellerId、ProductId、完整 Agent Intent 回执、Provider、HitCount、ProductContextResolution、Timestamp、QueryStatus、ProviderCalled、Error。HitCount 指实际 ReturnedFields 数量；原 KnowledgeResult 保留 content、field values、KnowledgeId、version、ItemSource/reference 和 StorageSourceType，不制造第二套 KnowledgeItem 模型。

## 可选实时入口

原 --agent-context-capture 支持以下增量参数，三个 Knowledge 参数必须齐全且同时启用原 --intent-config：

~~~powershell
--intent-config D:/CodexWork/qnocr-poc/Config/intent-provider.json
--product-state D:/CodexWork/qnocr-poc/Output/agent-product-context
--knowledge-config D:/CodexWork/qnocr-poc/Knowledge/product-knowledge.json
--knowledge-workflow <已有 knowledge-items.json 的绝对路径>
~~~

启用后 Mode=ShadowContextIntentKnowledge；从已有同帧 product_region.png 调用原 NativeWorkerClient，未改 OCR/Adapter 架构。保存 product-ocr.json、knowledge_NNNN.json、knowledge_replay_NNNN.json，以及 Intent/Knowledge 查询、命中、错误统计。产品 OCR 失败会记录错误；无有效 OCR 时仍按原 Resolver 的显式 manual 规则处理。

收据包含 ResolverSessionId；可在当前已有 Session 上使用原 --product-bind PRODUCT_STATE RESOLVER_SESSION_ID SELLER_ID PRODUCT_ID PRODUCT_NAME OPERATOR_ID；变更商品仍需原 --product-clear。API 调用者可先用 PrepareManualContext(原 AgentSessionId) 准备现有 resolver 输入副本。首次没有显式商品时安全跳过，绑定后需要一条新的买家消息，旧消息不会自动补查询。

省略 Knowledge 参数保持 V1.35.0-A Intent-only；同时省略 --intent-config 保持 Context-only。参数不完整返回 2，测试确认没有创建 capture 数据库。

本轮未运行该新实时分支，不声称已经完成新的实时端到端验收。原 capture 分支回归、编译与受控 MessageEnvelope→完整 Pipeline 已通过。

## 验证结果与证据

| 验证 | 结果 |
| --- | --- |
| qnocr-poc .NET 8 Rebuild | 0 Warning / 0 Error |
| ChangeDetectionChecks .NET 8 Rebuild | 0 Warning / 0 Error |
| RuntimeObserver .NET 8 Rebuild | 0 Warning / 0 Error |
| Knowledge 专项 | 61/61 |
| 原 Intent 专项 | 66/66 |
| 全量回归 | 1087/1087 = 原 1026 + 新增 61 |
| qnbot 兼容 | 17/17；ForbiddenBusinessCalls=0；原六份源文件字节一致 |
| qnbot net48 编译 | 原 CS4014 1 Warning / 0 Error，BuyerMessageBurstCoordinator.cs:744 |
| Manual / OCR Product Context | 两者实际走原 Resolver，均命中相应 Published 材质 |
| No Product Context | 15 条回执为 MissingProductContext，均零 Provider 调用 |
| Seller / Product / Session 隔离 | 合成输入通过；未知 Seller 不查其他 Seller，缺 Seller 在 Context 拒绝 |
| 发布过滤 | Draft / Reviewing / Approved 未发布 / Published 未许可均无事实；合格 Published 命中 |
| Knowledge 异常 | JSON 源损坏返回 KnowledgeUnavailable；Context/SQLite保留；重复不重试，修复后新消息继续 |
| Knowledge 幂等 | Envelope 重放、恢复后重放、8 个并发重复输入均无重复 query |
| SQLite | schema v1；只有 Sessions、Messages、StateHistory，未新增表/迁移 |
| Identity / 下游 | Observed；AI Reply=0；Prompt=0；Preview=0；自动发送=0；SentMessages=0 |
| 新命令无效参数 | 返回 2；不进入实时采集、不创建 capture DB |
| 真实多买家 | Pending，合成测试不替代真实验收 |

独立专项统计：Intent 受控 HTTP **37 次**（36 个 JEV 成功结果 + 1 个已有 RuleBased fallback）；实际 ProductKnowledgeProvider **21 次**，其中 Available **14**、Missing **6**、KnowledgeUnavailable **1**。另有 MissingProductContext **15**、NotRequired **1**，这些均不调用 Provider。这里是受控 HTTP + 实际本地测试 Source 查询，不是外部 JEV 或真实千牛调用次数。

证据目录：Output/ValidationV1350B/checks/0d66f61de476477e87341b236ce2039c/，含 summary.json、knowledge-results.json、各场景测试 SQLite、原 resolver sidecars 与受控发布数据。全量运行另生成一份专项证据，见 regression.log 内 V1350B_CONTROLLED_EVIDENCE 路径。

日志：Output/ValidationV1350B/main-build.log、checks-build.log、runtime-build.log、knowledge-checks.log、intent-checks.log、regression.log、qnbot-compatibility.log、invalid-capture-args.log、invalid-knowledge-args.log。兼容原始产物：Output/Integration/run_20261004_205241_133/。

专项包含不同现有 Intent 的 Material、Capacity、ShippingTime、ReturnPolicy 与 GIFT_QUERY/Unspecified 关联，未新建场景查询算法。受控发布仅发生在验证 fixture 初始化和状态测试，由原 Review Service 的显式动作完成；运行 Pipeline 不持有写作/发布服务，并以文件逐字不变验证只读行为。

~~~powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
& ./.tools/dotnet/dotnet.exe build qnocr-poc.csproj --no-restore -t:Rebuild -v q
& ./.tools/dotnet/dotnet.exe build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false
& ./.tools/dotnet/dotnet.exe build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-context-knowledge-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-context-intent-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe
& ./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot
~~~

## 完整修改列表、原因与影响

| 文件 | 原因 / 影响 |
| --- | --- |
| Extension/AgentContext/AgentProductContextAdapter.cs | 将原 Agent ID 投影到已有 resolver 的 64-hex 合同，复用 Manual/OCR/Binding，安全映射回原 ID。 |
| Extension/AgentContext/AgentKnowledgePipeline.cs | 最小 Context→Intent→Product→Published Knowledge 编排；复用既有 Provider/Repository/Source/Request/Result，关联状态、命中与错误。 |
| Extension/AgentContext/AgentContextCaptureCommand.cs | 可选 Knowledge 实时入口、同帧 Product OCR、结果收据与统计；保留 Intent-only/Context-only 选择。 |
| Validation/AgentContextKnowledgeChecks.cs | 新受控完整集成与隔离、发布、异常、幂等、恢复、schema、No AI/Send 专项。 |
| Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj | 链接新专项，只编入验证项目。 |
| Validation/ChangeDetectionChecks/Program.cs | 注册独立专项与全量回归入口。 |
| docs/VALIDATION_V1350B.md | 本验收、架构约束、统计、风险和回滚。 |

本阶段只影响 Agent Extension 的 Context→Intent→Product→Knowledge 边界和可选采集宿主。未改 qnbot Core、MessageEnvelope、QnocrMessageAdapter、原 Agent Context/Identity/Stable ID/Session lifecycle/SQLite schema、Product Resolver、Knowledge Provider/Repository/Source/Schema/Workflow、AI 或 Send Pipeline。两份既有未跟踪 V1.32.2 文档不纳入提交。

## 风险、当前限制与回滚

- 沿用 V1.35.0-A 的 Session-scoped 最多执行一次：Context 保存/Intent 消费后，若进程中断、取消、Knowledge异常或当时无商品，旧消息不会自动补跑。绑定/修复后需新消息；不保证每条消息最终都有 Knowledge。
- Knowledge Result 仅运行时与 JSON 验证/采集收据，不持久化到 SQLite；IntentHistory、AgentSession.CurrentProductContext 占位保持原状。完整执行历史留后续评审。
- 原 Manual Resolver 的 2 小时期限、clear-before-change、OCR 的 0.80/歧义/冲突策略不变；不会自动复用历史 OCR。投影副本与审计文件保留，不用于历史候选检索。
- 同一 owning Pipeline 串行处理；不支持多进程/多个 Manager 同时写相同 Context DB。外部 operator sidecar 的并发修改仍按原文件读/写语义；未做生产高并发验收。
- LocalFile Source 仍是最多 32 条 legacy 测试产品 / 256 条 workflow item 的受限测试实现。本任务没有将它升级为生产 Catalog。
- 实时 SellerId 仍是 Observed hash；现有 LocalFile 数据要求明确的测试 Seller 标识。不会把 observed:seller:... 自动映射到 seller-test 或跨店铺查默认资料，真实店铺知识映射需明确的后续方案。当前受控 Seller 隔离验收通过，不声称真实 Seller Catalog 已接入。
- 本轮未测新实时 Product OCR、真实店铺 Published 资料或外部 JEV 网络质量/延迟；真实多买家仍 Pending。
- 注入 HttpClient 按原 JEV 合同使用尚未发送请求的新实例；组件重建复用原数据库/产品 sidecar，不复用已发过请求的 client。

独立 Commit ID 见最终交付。运行时回退：省略三个 Knowledge 参数，即回到 V1.35.0-A Context→Intent。代码回滚：git revert 本阶段 Commit，不需要数据库迁移或回退数据；恢复原 Context/Intent 和旧独立 Product/Knowledge 能力。

下一步建议：进入 V1.35.0-C 的 Knowledge Result→Existing AI Provider→Reply Preview 评审/小范围集成，保持人工确认和 No Auto Send；同时补充真实 Product OCR、明确店铺知识映射与真实多买家验收。
