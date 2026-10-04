# V1.35.0-A Agent Context → Existing JEV Intent Integration

日期：2026-10-04（Asia/Shanghai）。基线：b6d636b1dc3a8186b942ee09c32c7d36329e8b5a。

## 判定与范围

**本阶段代码与受控集成验收通过。** Agent Context 已通过独立 AgentIntentPipeline 连接当前仓库的 IIntentClassifier / IntentClassifierFactory / JEVIntentClassifier；失败继续使用同一工厂内的 RuleBasedIntentClassifier。当前链路结束于关联后的 Intent Result。

新增专项 66/66；全量 1026/1026（原 960 + 本阶段 66）；qnbot 兼容 17/17。受控测试实际执行现有 JEV 请求序列化、响应解析、Provider 选择与 fallback，但 HTTP 返回由 HttpMessageHandler 控制，不代表此次已完成外部 JEV 网络测试或真实双买家验收。既有 V1.34.0-C 单买家真实 Context 证据保持有效，真实双买家仍 Pending。

Identity=Observed；Shadow；Knowledge 调用=0；AI Reply 调用=0；自动发送=0；SentMessages=0。计数依据为新 Pipeline 及采集分支没有 Knowledge / Reply / Send 服务依赖、下游分发或发送入口，并检查未生成 preview/send 文件。NeedKnowledge / NeedProductContext 仅保留现有 IntentResult 路由提示，不执行下游。原有回归含其他业务 mock 测试，不将这些路径的计数混作本次 Intent-only 运行计数。

## 当前实际 Intent 架构

| 位置 | 当前职责 |
| --- | --- |
| Decision/IIntentClassifier.cs | 现有同步 Classify(SessionContextView) → IntentResult 合同。 |
| Decision/IntentClassifierFactory.cs | 读取顶层配置；默认选 JEV；缺 key / 缺端点配置 / 端点禁用时启动降级；逐消息 JEV 异常调用同一 RuleBased classifier。未修改。 |
| Decision/JEVIntentClassifier.cs | 现有 TypeSafe HTTP 客户端、9 项 Choice、概率/confidence 验证、429/529 原有重试、模型版本与 IntentResult 映射。本阶段只增加显式可选的有界 dialogue state。 |
| Decision/RuleBasedIntentClassifier.cs | 原关键词 fallback；Confidence=null。不读取商品事实，不执行 Knowledge。未修改。 |
| Models/IntentResult.cs | IntentName、Confidence、NeedProductContext、NeedKnowledge、NeedHumanReview、RiskLevel、RuleVersion、MatchedRule。未重写或扩充。 |
| Config/intent-provider.json | Enabled=true、Provider=JEV、Fallback=RuleBased，指向现有 jev-provider.runtime.json。未修改。 |
| Shadow/QnocrShadowRunner.cs | 原 Intent 工厂入口，继续使用旧调用方式。本阶段未修改。 |
| Shadow/SessionContextBuilder.cs | 原 DispatchItem/Shadow 上下文 builder，会收集其他业务侧商品/身份字段，不适合直接作为独立 AgentSession 输入；保持原状。 |

本次未新建 classifier、JEV 客户端、Intent 枚举、Provider selector 或 fallback 体系。SessionContextView 新增 nullable IntentRecentMessages 属性；null 时序列化忽略，JEV 保持旧单字符串 state / instructions。只有 Agent adapter 明确设置该属性时才发送结构化 state。

输入 shape 与 [TypeSafe HTTP API](https://docs.typesafe.ai/api) 及 [State 文档](https://docs.typesafe.ai/concepts/state) 核对：state 支持对象，可将当前问题与近期对话放入命名字段；本次不迁移 SDK 或接口版本。

## 集成方式与幂等

1. AgentIntentPipeline 独占一个原 AgentContextPipeline。Identity Normalization、Stable Identity、Manager 隔离、SQLite 保存、生命周期沿用原实现。
2. 建立 Pipeline 时，从现有 SQLite 快照记录每个 Session 已接受消息数量；既有 Context-only 消息同样算历史，启动不发起 Intent。
3. ProcessMessage 首先调用原 Context Pipeline；根据返回的不可变、按追加顺序排列的历史与已有数量，取本次真正追加的消息。重复 Envelope 没有追加，因此没有 Intent。
4. 先记录本次接受数量，再逐条处理新增 buyer 消息；seller / 其他角色只进入 Context。整个调用由本 Pipeline 锁串行化，避免并发重放重复执行或 Provider trace 错配。
5. Adapter 从指定 Session 的指定 MessageId 构建现有 SessionContextView，不查询其他 Buyer / Session / Archived 历史，不生成 AI 请求。
6. 调用工厂提供的 IIntentClassifier。读取现有 IntentProviderTrace 获取实际 Provider、fallback、原因与耗时，核对 trace 的 SessionId / MessageId。
7. 返回 AgentIntentBatchResult（Session、AppendedMessages、Results）。每条 AgentIntentMessageResult 包含 SessionId、MessageId、原 IntentResult、Provider、Timestamp、FallbackUsed、FallbackReason、LatencyMilliseconds，以及非预期错误类型 Error。
8. Context 保存失败时不执行 Intent；JEV 失败由原工厂 fallback，Context 已保存。其他非 OOM Intent 边界异常返回错误结果，不回滚 Context 或重试；采集摘要将其标为 CapturedWithIntentErrors。

此处数量只用于消费原 Manager 的追加决定，不建立第二套 MessageId 去重索引。容量淘汰后清理数量缓存，继承现有 Session 上限。

幂等范围继承原 Manager 的 Session 生命周期：同一保留/恢复 Session 的同一稳定 MessageId 最多执行一次；原策略因 idle/retention 新建 Session 时，不引入跨生命周期全局去重或借用旧 Session。组件重建不是多进程并发写入支持。

## Context Window

- 当前目标 buyer 消息原文 + 其之前最近最多 3 条 buyer/seller 消息，最多 4 条。
- 使用 Agent Session 的追加顺序，不按源 OCR 时间重排；批内较早消息不能读取尚在其后的消息。
- 近期消息每条最多 1000 UTF-16 字符，近期总预算 2000；优先保留最近的消息，历史超长只截断发送副本并避开 surrogate pair。
- 当前问题不由 Adapter 截断；沿用 JEV 对 trim 后当前问题的 1000 字符限制，超限直接走现有 RuleBased fallback，不发 HTTP。
- 真正发往 JEV 的正文总计最多 3000 字符。此预算不包含 JSON、角色、instructions 和 criteria 的固定开销，不是 token 数承诺。
- Provider 对 opt-in 历史再次校验条数、角色、单条长度和总长度。
- JEV state 只有 currentBuyerMessage 与 recentMessages 的 role/text，不发送 SessionId、BuyerId、时间、ProductId 或全部历史。
- 不传商品绑定、Knowledge、外部 Session 或长期 Memory。Adapter 拒绝 Inactive、Archived 和带 foreign SessionId/Source 的快照。

## 实时入口与回滚开关

既有 --agent-context-capture 增加可选 --intent-config <file>。省略该开关仍为 V1.34 Context-only。提供开关使用同一原采集 / OcrMessageSource，但下游切换至 AgentIntentPipeline，Mode=ShadowContextIntent；JEV / RuleBased 的默认选择仍来自现有配置。

每个被接受的 Envelope 保存 intent_NNNN.json；重放保存 intent_replay_NNNN.json，必须没有新增 Results。摘要记录 IntentCalls、JEVSuccess、RuleFallbackCount、IntentErrors 和 AverageIntentLatencyMilliseconds，保持 AI/Knowledge/Reply/Send 为 0。

~~~powershell
# 在原有受控采集命令参数后追加：
--intent-config D:/CodexWork/qnocr-poc/Config/intent-provider.json
~~~

本次未点击 UI 或发送消息，未运行本轮真实采集。入口接线通过编译、已有采集/Context 回归和受控 OcrMessageSource→AgentIntentPipeline 路径验证；真正外部网络与新实时端到端验收需另行补做。

## 检查与证据

| 项目 | 结果 |
| --- | --- |
| 主项目 .NET 8 Rebuild | 0 Warning / 0 Error |
| ChangeDetectionChecks .NET 8 Rebuild | 0 Warning / 0 Error |
| RuntimeObserver .NET 8 Rebuild | 0 Warning / 0 Error |
| Intent 专项 | 66/66 |
| 全量回归 | 1026/1026，原 960 全部保留通过 |
| qnbot 兼容 | 17/17；ForbiddenBusinessCalls=0；六份源文件字节一致 |
| qnbot net48 编译 | 原 CS4014 1 Warning / 0 Error，BuyerMessageBurstCoordinator.cs:744；不屏蔽、不修改上游 |
| 真实双买家 | Pending；本轮两个 Buyer 为合成输入 |
| Identity / 下游调用 | Observed / Knowledge=0 / AI Reply=0 / 自动发送=0 / SentMessages=0 |
| IntentHistory | 未写入；保留只读空占位，结果由 Pipeline 返回/采集收据保存 |
| SQLite schema | v1；仍只有 Sessions、Messages、StateHistory，无迁移 |

专项覆盖：Context 保存先于 JEV、单条一次调用、连续省略问句上下文实际送达、稳定 ID 与标准化身份重放、seller 不触发、批内与混合重放、批次未来消息排除、并发重复输入、合成 A/B/A 隔离、组件重建不重跑、恢复后 A/B 新消息各自继续、Context-only 旧历史不补跑、Archived 不借历史、有界长度、当前超长 fallback、无 key、显式 RuleBased 回滚配置、HTTP/异常/超时/不可用 response fallback 后下条恢复 JEV、SQLite 保存失败不请求 Provider 与修复后重试、No Preview / No Send。

主路径（连续会话 + B + 恢复）受控现有 JEV 成功 7 次，Provider=JEV，模型回执 jev-v1350a-controlled，0 失败，平均分类耗时 **3.12ms**。这是本地受控 transport 的测量，不能和历史真实 JEV 约 402ms 网络耗时对比。本文件不把其他边界子场景的成功次数混入此主路径样本。

| 受控故障 | 实际 fallback | 原因 |
| --- | --- | --- |
| HTTP 503 | RuleBased；材质查询保持 MATERIAL_QUERY | HttpRequestException |
| transport 抛异常 | RuleBased；同一稳定消息重放不再调用 | HttpRequestException |
| timeout | RuleBased；后续新消息恢复 JEV | TaskCanceledException |
| 缺少 intent 的 HTTP 200 response | RuleBased；Context 不丢失 | KeyNotFoundException |
| 启动无 key | RuleBased；HTTP=0 | ApiKeyMissing |
| 当前问题超长 / recent 条数越界 | RuleBased；越界输入不发 HTTP | InvalidDataException |

所有上述 fallback 测试通过现有工厂执行，无新增 fallback。

最终独立专项证据：Output/ValidationV1350A/checks/98cc6885e1464cdc9ac4541d82007bc5/（summary.json、intent-results.json、隔离的测试 SQLite）。原始日志：Output/ValidationV1350A/main-build.log、checks-build.log、runtime-build.log、intent-checks.log、regression.log、qnbot-compatibility.log。全量运行另生成一份专项证据，具体路径记在 regression.log。兼容原始产物：Output/Integration/run_20261004_105926_287/。

初次受控专项因测试复用已发过请求的 HttpClient，在组件重建时触发现有 JEV 构造器设置 Timeout 的 InvalidOperationException；测试随后按真实组件重建方式新建 HttpClient，仅共享受控 Handler，最终 66/66。未为该测试改变 Provider HTTP 生命周期行为。

~~~powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
& ./.tools/dotnet/dotnet.exe build qnocr-poc.csproj --no-restore -t:Rebuild -v q
& ./.tools/dotnet/dotnet.exe build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false
& ./.tools/dotnet/dotnet.exe build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe --agent-context-intent-only
& ./Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe
& ./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot
~~~

## 完整修改列表、原因与影响

| 文件（相对仓库根） | 原因 / 影响 |
| --- | --- |
| Extension/AgentContext/AgentContextIntentAdapter.cs | 新增最小 AgentSession→现有输入适配，窗口与隔离边界。 |
| Extension/AgentContext/AgentIntentPipeline.cs | 新增独立编排：原 Context 先保存，消费新追加、调用既有工厂 classifier、关联结果与重建基线。 |
| Decision/JEVIntentClassifier.cs | 现有 Provider 增加可选 structured dialogue state 和长度检查；保留旧 null 输入路径。 |
| Models/MessageSession.cs | 现有 SessionContextView 增加可选 IntentRecentMessages，未改 MessageEnvelope 或 IntentResult。 |
| Extension/AgentContext/AgentContextCaptureCommand.cs | 原受控实时入口提供可选 Intent 开关与收据/统计，默认 Context-only。 |
| Validation/AgentContextIntentChecks.cs | 受控集成、fallback、窗口、幂等、恢复、SQLite 与边界专项。 |
| Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj | 链接专项文件，只编入验证项目。 |
| Validation/ChangeDetectionChecks/Program.cs | 注册独立专项入口与全量回归。 |
| docs/VALIDATION_V1350A.md | 本验收、证据、限制与回滚记录。 |

影响仅在 Agent Extension→Intent 边界及既有 JEV 的 opt-in 输入。本阶段未改 qnbot Core、MessageEnvelope、QnocrMessageAdapter、IdentityNormalizer、Stable Identity、AgentSessionManager、SQLite Schema、Session 生命周期、Knowledge、AI Reply 或 Send Pipeline。两份原有未跟踪 V1.32.2 文档不纳入本提交。

## 风险、限制与回滚

- 采用最多执行一次而非保证最终完成：Context 已落库但 Intent 未完成时若进程退出，该消息下次启动作为历史跳过；不自动重试或补跑。这避免重复外部调用，但可能缺 Intent Result。
- IntentResult 与 Provider 元数据不进入 SQLite / AgentSession.IntentHistory；实时入口可保留文件收据。完整 Intent History 持久化留后续。
- 同一 Pipeline 内同步串行调用，继承现有 JEV timeout 与 HTTP retry。慢 Provider 会延迟返回，但不会回滚已保存 Context；不属于高并发生产验收。
- SQLite 仍要求单个 owning manager；不支持 Context-only 和 Intent Pipeline 同时写同一个库或其他进程并发写。
- 结构化窗口已核对官方输入合同并以受控请求体验证；中文连续问句推断质量、真实外部延迟与新实时端到端稳定性本轮未测。
- OCR 文本/时间波动仍可能改变 stable MessageId；Observed 仍不证明平台身份。真实双买家继续 Pending。
- 注入 HttpClient 时按原 Provider 要求使用尚未发送请求的新实例；默认构造由原 Provider 管理客户端。

运行时回退：省略 --intent-config 即恢复原 Context-only Shadow；或使用现有 RuleBased 配置关闭 JEV。代码回滚：git revert 本阶段独立 Commit（ID 见最终交付），不需要数据库迁移或数据回退。回滚后保留原 Session/消息 SQLite、Context、旧 JEV、qnbot 能力。

建议下一阶段进入 V1.35.0-B 的 Intent→Product Context→Knowledge Provider 小范围集成，先保持 No AI Reply / No Send；同时补充真实双买家和真实外部 JEV 上下文验收，不重新设计 Context 或 Intent。
