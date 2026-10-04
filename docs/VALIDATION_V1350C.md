# V1.35.0-C Knowledge → Existing AI Provider → Reply Preview

日期：2026-10-04（Asia/Shanghai）。基线：cdbc8aec4e849103d375adfda9d2147aadf6f6c1。

## 判定与证据范围

**本阶段代码及受控集成验收通过。** 新 Agent 主链已接到现有 OpenRouter Provider 的 HTTP 实现、原 ReplyPreview 模型和原 AssistantReplyService 人工工作流，终点是人工审阅建议。没有真实发送。

Reply 专项 59/59；Intent 66/66；Knowledge 61/61；全量 1146/1146 = 原 1087 + 新增 59；qnbot 兼容 17/17。三个 .NET 8 项目均 0 Warning / 0 Error；net48 兼容宿主保留原 CS4014 1 Warning / 0 Error。

本次为受控 JEV HTTP + 实际本地 Published Knowledge + **现有 OpenRouter Provider 的受控 HTTP transport**，并实际跨 .NET 8 / net48 进程边界。不是另一个 fake Provider；测试仅替换 HTTP 返回/key getter，Factory、配置解析、Prompt 构建、请求序列化、响应解析和 Preview 服务实际执行。未运行真实外部 AI 或新的千牛实时端到端；真实多买家仍 Pending。

独立专项统计：Intent HTTP 25 次；AI HTTP **20 次**；Reply Preview **17 个**，其中模型 Preview **14 个**、确定性人工核实 Preview **3 个**；AIUnavailable 9 条；自动发送 **0**；SentMessages **0**；Identity **Observed**。失败样本包含故意的 HTTP/超时/异常/无效/空响应/缺 key/禁用、超长输入和回执篡改，不代表真实 Provider 的失败率。

## 当前实际 AI Provider 架构

| 文件 | 当前职责 / 本阶段复用方式 |
| --- | --- |
| Validation/QnbotCompatibility/IAIProvider.cs | 原 net48 AI 合同；保留 GenerateAsync / SmokeAsync，增加 PreviewAsync 和本实例 HTTP 次数。 |
| Validation/QnbotCompatibility/AIProviderConfig.cs | 原六字段非 secret 配置与 AIProviderFactory；增加受控 transport/key 注入参数，未新建配置体系。 |
| Config/ai-provider.json | 原 OpenRouter、https://openrouter.ai/api/v1、openai/gpt-5.5、temperature=0.15、max_tokens=220、Enabled=true；未修改。 |
| Validation/QnbotCompatibility/OpenRouterProvider.cs | 原 Provider；将原 SmokeAsync 的非流式 HTTP 请求/解析提取为同一 RequestAsync，由 SmokeAsync 与 PreviewAsync 复用。未新增 OpenAI/SSE Client。 |
| Validation/QnbotCompatibility/AiShadowRuntime*.cs | 原隔离 runtime / key 读取 / 旧 qnbot generation；本阶段未改。真实 Preview 仅复用隔离存储的 key 读取，不调用旧业务路由。 |
| Validation/QnbotCompatibility/Program.cs | 复用原 BuildModelQuestion / ValidateKnowledgeContext；新元数据有值时加入 Intent、商品上下文、Knowledge ID/reference。 |
| Shadow/ShadowAdapterClient.cs | 原 task-owned 隔离子进程、deadline、No Send 回执检查；增加 Agent Preview 模式，附加 Provider/model 元数据。 |

实际发现的安全边界：旧 GenerateAsync 调用 qnbot StreamingBuyerAnswerService.GetAnswerAsync；该方法会执行 SmartReplyRouter、读取 RecentTurns / ContextDigest、额外 Knowledge、store prompt rules、学习记录及观察器。即使将当前 Agent 数据传入 question，也不能证明 Prompt 只来自允许的当前 Agent 窗口。

最小方案是在**原 Provider 内复用已有非流式 HTTP 实现**，增加 PreviewAsync，而不调用上述业务路由。原 GenerateAsync / SSE / 旧 AI Shadow 入口保持行为。没有新 Provider、第二份配置、第二个 HTTP Client 体系或 SSE 实现；现有 Factory 继续选择 OpenRouter。

真实 Preview 分支从原隔离 key store 获取 API key；不创建 Buyer 平台映射，不启动 Bot App / QN / 接待或发送入口。受控分支以明确 --agent-reply-preview-controlled 标识，报告 EvidenceKind=ControlledHTTPExistingProvider，不能当作真实外部 AI 验收。

## 当前实际 Preview 与人工操作

- 原 Models/MessageSession.cs 的 ReplyPreview 继续使用，模型未修改。
- 原 AssistantReplyService.InitializePreview / List / Show / Edit / Approve / Copy / RecordFeedback 继续使用；没有新人工工作流或 UI。
- Preview.SendStatus 始终 Shadow；Identity 以原 Agent Observation 映射为 BuyerIdentity.Status=Observed，未生成 Verified 或平台 ID。
- 原服务按 PreviewId 扫描 review 文件，兼容原 Agent 32-hex SessionId 和 opaque Stable MessageId。review 文件夹使用 SHA-256(SessionId + ":" + MessageId)，避免 agent:v1:... 的冒号作为 Windows 路径。
- 复用原 outbox/<safe-key>/reply-preview.json、assistant-reply.json 文件布局，仅作为人工 review 文件位置；不新增 delivery Outbox、重试队列或 Execution Journal。
- Preview 与 review sidecar、关联回执就绪后才发布 reply-preview.json。已有同 key Preview 不覆盖，不破坏人工编辑。
- 不调用 PreviewConfirmationService、ConfirmSend、IReplySendAdapter 或 qnbot/千牛发送。
- 验证 Clipboard 使用原 IAssistantClipboard 的受控 probe，不覆盖操作者真实剪贴板；复制和 Feedback 均验证原接口。

原 AssistantReplyService 的初始化状态名仍为 AIGenerated。确定性安全建议通过 AgentReplyMessageResult.AiProvider=SafetyGuard、AiCalls=0、KnowledgeGuardApplied=true、对应 KnowledgeStatus 区分，不伪报为模型回复。

## 集成、Prompt 与 Knowledge 策略

AgentReplyPipeline 独占原 AgentKnowledgePipeline，消费本次新增消息的 Knowledge 结果：

Context 保存 → 现有 Intent → Product Resolver → Published Knowledge → AgentReplyInputAdapter → 原 Prompt builder / Provider → 原 ReplyPreview → 原人工 review 服务。

AgentReplyInputAdapter 核对原 Session / Message / Seller / Intent / Bound Product 关联，并调用原 AgentContextIntentAdapter。只允许 Active Session；拒绝 foreign Seller/Product 和 Archived snapshot。

**窗口**：当前买家消息 + 最近最多 3 条 buyer/seller，最多 4 条，复用既有历史截断预算，正文总计 <=3000 UTF-16 字符，当前问题 <=1000。超长当前问题不截断、在 AI 前安全失败，SQLite 原文保留。角色大小写仅在 wire 副本规范化，Stable MessageId / 存储原文不改变。

额外附加当前 IntentName / Confidence / Provider、当前 ProductName / Source，以及 Knowledge SellerId / ProductId / source / version / category / KnowledgeId / reference。旧 contexts 未设置这些字段时，原 Prompt 路径保持不变。最终 model question <=12000 字符，此外只有固定 safety system prompt；不是 token 数保证。

Available 使用原 KnowledgeModelContextWriter 的 scope / value / complete-fields 校验。显式 publishedPreview 模式允许当前 RequestBuilder 已支持的 ShippingTime / ReturnPolicy / CurrentPrice 等字段，并要求 KnowledgeId 与 ItemSource；默认调用维持原字段白名单。Published 项目的筛选仍由原 B 阶段 Source/Provider 完成，未改 Workflow。Prompt 不读取其他 Session、其他 Seller/Product 的知识、Archived history、全局最近消息、qnbot 独立知识或店铺规则。

| Knowledge 状态 | AI / Preview 行为 |
| --- | --- |
| Available | 仅当前商品的合格 Published 事实进入 Prompt，调用原 Provider；正常结果为 ReadyForReview。 |
| Missing | AI=0，复用原固定“当前缺少已确认的商品信息，请人工核实后答复。”，标记原 Missing / GuardApplied。 |
| MissingProductContext | AI=0，固定人工核实 Preview，ProductId 为空；不猜商品或借用其他 Session。 |
| KnowledgeUnavailable | AI=0，固定人工核实 Preview，保留该状态及上游错误类型，不假装知识 Available。 |
| NotRequired / OTHER | 可以调用原 Provider；Prompt 不携带任何商品事实。 |
| Intent 或输入关联不可用 | 安全失败，AIUnavailable，无模型 Preview。 |

Safety 文案从既有 DurableShadowDispatch 提取成共享 KnowledgeReplyGuard 常量；旧 dispatch 仅改为引用同一文本，未改变它的执行或发送行为。

每个 AgentReplyMessageResult 包含原 Knowledge/Intent 关联、原 ReplyPreview、状态、AiProvider、AiModel、AiInvoked、AiCalls、Error。Preview 内有 SessionId、Input.MessageId、ProductId、Intent、Knowledge 状态、生成时间、回复文本；SellerId / Provider 元数据由外层回执补齐，避免创建第二套 Preview 模型。AI confidence 保持 null，不将 JEV confidence 伪装为回复置信度。

## 幂等、恢复与异常

复用 V1.35.0-A/B 的“只消费新追加”机制；串行 Reply Pipeline 不建立另一份 MessageId 索引。重复 Envelope / 并发重复 / 恢复旧消息，没有新 Intent/Knowledge 回执，因此不再调用 AI 或创建 Preview。

重建 Context 后，已有 Preview、编辑/审批/复制 sidecar 保留；旧消息不批量重跑。恢复后的新消息可以重新完成整个当前链路。本次验证同一原 SessionId 下的新消息正常进入 Preview，人工已编辑结果未被覆盖。

沿用 Session-scoped 最多执行一次和“中断后不自动补跑”的技术债。保存后若取消、AI 请求失败、Provider 不可用或发布失败，不自动重试旧消息；修复后通过新消息继续。不增加数据库表、migration、Execution Journal 或 recovery Outbox。

| AI 场景 | 结果 |
| --- | --- |
| HTTP 503 | AIUnavailable，无 Preview；Context 保存；同消息不重试。 |
| timeout | AIUnavailable，无 Preview；无发送。 |
| Provider 抛异常 | 仅记录类型，不记录异常消息；无 Preview。 |
| HTTP 200 无效 JSON / 空文本 | 无 Preview，不制造成功结果。 |
| 缺 key | HTTP=0、AIUnavailable，Context 不回滚。 |
| Enabled=false | HTTP=0，原配置开关有效。 |
| 返回 key echo | 原 Provider 替换为 [REDACTED] 后才进入回执/Preview。 |
| scope / No Send 回执异常 | 发布 Preview 前拒绝；不调用发送。 |

Provider 内部超时 90 秒；原 child helper deadline 150 秒，只终止本任务创建的 helper。没有模型重试或发送 fallback。

## No Send 与日志证据

新宿主不创建 Coordinator、不调用 native message converter、平台 identity 映射、业务路由或 delivery。客户端和 Pipeline 双重核对 Mode、关联 ID、Provider/model、CoordinatorCreated=false、ForbiddenBusinessCalls=0、SendEnabled=false、SendEntryInvoked=false、SentMessages=0。

负向检查仅**篡改测试收据的 SendEnabled=true**，未执行任何发送；Pipeline 正确拒绝该收据，未发布 Preview。不能把这种负向 metadata 样本描述为真实发送。

实际 Preview 默认仅经现有人工 review / copy 工作流操作。专项目录中无 send-request / reply-delivery 文件；原上游兼容六文件字节不变。全量旧回归含独立的发送 mock 检查，这不代表新链路执行真实发送。

HTTP Authorization 只在 header；配置不包含 key；正常运行不打印完整 Prompt/header。受控 fixture 才保存 controlled-provider-request.json，用于检查真实序列化的 body，没有 Authorization。Exception 只保存类型，回复中的精确 key echo 在原 Provider 脱敏。专项 synthetic secret audit=SecretPresent:false；未为测试读出或打印真实 secret。

model-context.json、Preview、review 回执存于用户选定的本地 review/Output 目录，包含私有对话与允许的事实，不加入 Git。普通统计只保存关联/计数/状态。

## 验证与运行入口

| 项目 | 结果 |
| --- | --- |
| qnocr-poc .NET 8 Rebuild | 0 Warning / 0 Error |
| ChangeDetectionChecks .NET 8 Rebuild | 0 Warning / 0 Error |
| RuntimeObserver .NET 8 Rebuild | 0 Warning / 0 Error |
| net48 Compatibility Host | 原 CS4014 1 Warning / 0 Error，BuyerMessageBurstCoordinator.cs:744 |
| Reply 专项 | 59/59 |
| Intent 专项 | 66/66 |
| Knowledge 专项 | 61/61 |
| 全量 | 1146/1146，原 1087 全保留 |
| qnbot 兼容 | 17/17；ForbiddenBusinessCalls=0；6 源文件字节一致 |
| JEV + Knowledge + AI | 现有 serializer/parser/Factory/Prompt/Provider/Preview 全链通过 |
| RuleBased fallback 完整链 | JEV 受控失败后正常完成，Prompt Provider=RuleBased |
| 人工工作流 | 原 List/Show/Edit/Approve/Copy/Feedback 接口兼容；copy 使用 probe |
| Session / Product / Seller | 实际 AI body 隔离通过 |
| 恢复与幂等 | 原 Preview/人工编辑保留，旧消息不跑，新消息继续；8 个并发重复仅一次 |
| Identity / SQLite | Observed；schema 不变，未增加 Preview 执行历史表 |
| 自动发送 / SentMessages | 0 / 0 |
| 缺 Preview 参数 | 返回 2，不创建 capture DB |
| 真实外部 AI / 新千牛 E2E | NotRun；不伪报真实验收 |

专项最终证据：Output/ValidationV1350C/checks/2539facba4554518adb073b979dc967f/，含 summary.json、测试 Context DB、Published fixture、Preview/review sidecar、子进程收据、受控请求体、人工 Feedback。全量另生成同类目录，路径见 regression.log。

日志：Output/ValidationV1350C/main-build.log、checks-build.log、runtime-build.log、compat-build.log、reply-checks.log、intent-checks.log、knowledge-checks.log、regression.log、qnbot-compatibility.log、invalid-capture-args.log、invalid-reply-args.log。兼容原始产物：Output/Integration/run_20261004_215258_559/。

新 capture 开关在 B 阶段参数之上增加以下两项；必须同时提供，知识三个参数与 --intent-config 也必须齐全：

~~~powershell
--reply-state <人工 review 状态目录>
--ai-provider-config D:/CodexWork/qnocr-poc/Config/ai-provider.json
~~~

Mode=ShadowReplyPreview，收据 reply_NNNN.json / reply_replay_NNNN.json，累计 AI 调用、Preview 数量；重放同时核对 Intent、Knowledge、AI、Preview 不增长。省略这两个参数即回到 V1.35.0-B。新的实时分支本轮未运行。

~~~powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
& ./.tools/dotnet/dotnet.exe build qnocr-poc.csproj --no-restore -t:Rebuild -v q
& ./.tools/dotnet/dotnet.exe build Validation/QnbotCompatibility/QnbotCompatibility.csproj --no-restore -t:Rebuild -v q
& ./.tools/dotnet/dotnet.exe build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false
& ./.tools/dotnet/dotnet.exe build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-reply-preview-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-context-intent-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-context-knowledge-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe
& ./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot

# 使用原人工操作入口：
& ./bin/Debug/net8.0-windows/win-x64/qnocr-poc.exe --assist-list --state-dir <reply-state>
& ./bin/Debug/net8.0-windows/win-x64/qnocr-poc.exe --assist-show <PreviewId> --state-dir <reply-state>
# edit / approve / copy / feedback 的原参数协议不变。
~~~

## 完整修改文件、原因与影响

| 文件 | 修改原因 |
| --- | --- |
| Extension/AgentContext/AgentReplyInputAdapter.cs | 当前 Agent/Intent/Product/Published 关联检查，复用窗口与知识 writer，生成已有 wire 的有限输入。 |
| Extension/AgentContext/AgentReplyPipeline.cs | 独立编排现有 Knowledge、Provider、Preview/人工服务；Guard、错误、幂等、关联及 No Send 检查。 |
| Extension/AgentContext/AgentContextCaptureCommand.cs | 可选 Preview 开关、对应 Mode/收据/计数及重放检查，保留 B/A/Context-only。 |
| Integration/AgentContextWire.cs | 原跨版本 wire 增加可选 Seller/Intent/Knowledge provenance 元数据；不改 MessageEnvelope。 |
| Knowledge/KnowledgeModelContextWriter.cs | opt-in Published Preview 字段及来源校验，原默认行为保留。 |
| Shadow/KnowledgeReplyGuard.cs | 原人工核实文案的共享常量。 |
| Shadow/DurableShadowDispatch.cs | 引用上述原文常量，仅文本复用，不接新链或改变执行语义。 |
| Shadow/ShadowAdapterClient.cs | 复用原 helper 生命周期，新增 Agent Preview mode、Provider/model 回执字段和严格 No Send 校验。 |
| Validation/QnbotCompatibility/IAIProvider.cs | 原接口增加 PreviewAsync / HTTP 计数，不建第二接口体系。 |
| Validation/QnbotCompatibility/AIProviderConfig.cs | 现有 Factory 的受控 transport/key seam，配置字段/验证不变。 |
| Validation/QnbotCompatibility/OpenRouterProvider.cs | 抽取并复用现有 Smoke HTTP transport；新增 Preview safety system 消息。 |
| Validation/QnbotCompatibility/AgentReplyPreviewHost.cs | Preview-only host，核对 Observed/bounded/scope、原 Prompt、原 Provider，输出 No Send 回执；受控故障入口。 |
| Validation/QnbotCompatibility/Program.cs | 注册新宿主模式；复用并扩展原 Prompt helper 的可选元数据。 |
| Validation/AgentReplyPreviewChecks.cs | 新完整链、Prompt、Guard、Provider失败、人工操作、恢复、隔离与 No Send 专项。 |
| Validation/AgentContextKnowledgeChecks.cs | 仅开放既有受控 fixture 给新专项复用，不改 B 阶段检查语义。 |
| Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj | 链接新专项。 |
| Validation/ChangeDetectionChecks/Program.cs | 独立专项/参数拒绝/全量注册。 |
| docs/VALIDATION_V1350C.md | 本架构与验收记录。 |

影响仅在 Agent 的 Knowledge→AI Preview 边界、必要原 Provider/Prompt 适配与验证入口。qnbot Core、MessageEnvelope、QnocrMessageAdapter、Identity Normalizer、Stable ID、Agent Session lifecycle、SQLite Schema、Intent Provider、Knowledge Workflow、Product Resolver、真实 Send Pipeline 未修改。两份既有未跟踪 V1.32.2 文档不属于提交。

## 当前限制、风险与回滚

- Available Prompt 的事实来源与作用域受控，但本次没有评估真实模型的事实忠实度；仍需人工审阅。Missing / No Product / KnowledgeUnavailable 则以不调用 AI 的确定性 Guard 保证不强答。
- 真实外部 AI、真实千牛新 E2E、真实多买家未验收；B 阶段 Observed Seller→实际商品知识目录的明确映射限制继续存在。
- 原 Web AgentConsoleReadService 仍假定 64-hex session / 原 message-folder layout；本阶段验证并提供原 Assistant CLI / service 的完整人工接口，未改 Web Console。后续可单独适配 Web read projection，不能把本轮接口验证当成 Web UI 已联调。
- 使用原 Provider 的非流式 Preview transport，未复刻 SSE，未把旧 qnbot 自动业务路由接进新链。真实 key 来自隔离原设置，需原有效配置；本轮未修改或应用 provider settings。
- 同一 owning Pipeline 串行执行，slow AI 影响返回延迟但不会回滚 Context；多进程、高并发、取消后补跑不在本轮范围。
- Preview/review JSON 持久化复用现有文件服务；SQLite 只保留原 Context。最多执行一次的中断缺失不补跑，明确保留技术债。
- 恢复后是否再次进入同一 Session 仍按原 idle/retention 规则；不能使用 Archived 商品/历史。

本阶段独立 Commit ID 见最终交付。运行时回退：省略 --reply-state / --ai-provider-config，保留 B 参数即可恢复 Context→Intent→Product→Knowledge；代码回滚：git revert 本阶段 Commit，无数据库 migration，不影响原 Context、Intent、Product、Published Knowledge。

下一步优先真实千牛单买家端到端 Reply Preview 验收，以及 Feedback 闭环接入/评估；可再单独评审 Web read projection。继续人工编辑/审批/复制，不立即进入自动发送，Buyer 身份安全条件另行验收。
