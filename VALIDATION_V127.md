# V1.27 JEV Default Intent Provider 验证报告

## 结果

`dotnet run -- --shadow` 现在默认读取 `Config/intent-provider.json`，选择 JEV 作为 Intent Provider；`Config/jev-provider.runtime.json` 启用 TypeSafe 的 `jev-latest`，超时 8 秒。缺少 `TYPESAFE_API_KEY`、JEV配置不可用或单条请求失败时使用 RuleBased。JEV正常时其 `IntentResult` 进入现有 Decision 流程；降级时由原 `RuleBasedIntentClassifier` 生成同一接口的结果。AI回复生成与发送路径未改。

## 修改原因与架构影响

V1.26 仅做旁路对比，无法在实际 Shadow 消息中选择 JEV。本轮新增 `IntentClassifierFactory` 和顶层 `IntentProviderConfig`，只在应用的 Shadow CLI 显式传入默认配置；直接使用 `QnocrShadowRunner` 而未提供配置路径的旧测试/嵌入调用保持 RuleBased，以维持既有回归。配置 `Provider=RuleBased` 时不尝试 JEV；`Provider=JEV` 时先检查环境变量中的 API Key，再加载单独的 JEV 运行配置。配置与日志不含 API Key。

Trace 新增 `INTENT_PROVIDER_SELECTED`、`JEV_PROVIDER_UNAVAILABLE`、`JEV_PROVIDER_FAILED`、`INTENT_PROVIDER_USED`。使用事件记录 Provider、Intent、FallbackUsed、Timestamp 和消息标识，不记录买家正文或密钥。配置 `Enabled=false` 等同显式关闭选择；JEV失败仅影响该条消息的分类，后续仍可重试JEV，不将其失败变成 Agent 的 `DecisionFailed`。

`IIntentClassifier`、`IntentResult`、`MessageEnvelope`、qnbot 核心、Knowledge 系统、AI回复Provider、Prompt及发送链均未修改。`AgentPipelineEvent` 仅增可选 Provider/FallbackUsed 元数据；Runner 将工厂选出的分类器注入现有 `DurableShadowDispatch`。

## 文件与提交

实现提交：`004e8aedac69d56ca6f78ecd67e0ac9c7bf57e8f`。新增 `Config/intent-provider.json`、`Config/jev-provider.runtime.json`、`Decision/IntentClassifierFactory.cs`、`Decision/IntentProviderConfig.cs`、`Models/IntentProviderTrace.cs`、`Validation/ChangeDetectionChecks/IntentProviderFactoryChecks.cs`；修改 `Models/MessageSession.cs`、`Program.cs`、`Shadow/AgentPipelineTrace.cs`、`Shadow/QnocrShadowRunner.cs`、测试入口；删除文件：无。

## 测试

- 编译：检查项目与 Knowledge Console 均成功，0 警告、0 错误。
- 回归：531/531 检查通过（V1.26 基线 518，新增 13）；原入口离线回归17/17，ForbiddenBusinessCalls=0。日志位于 `Output/ValidationV127/regression.log`。
- JEV启用：注入符合 TypeSafe Choice 格式的 HTTP 响应，工厂选择JEV并返回 `MATERIAL_QUERY`；`INTENT_PROVIDER_SELECTED` 与 `INTENT_PROVIDER_USED` 正确记录。
- RuleBased显式配置：工厂只运行原规则，JEV HTTP调用0次。
- 降级：缺Key时启动前记录 `JEV_PROVIDER_UNAVAILABLE`，选择RuleBased且HTTP调用0次；JEV返回HTTP 503时该条消息记录 `JEV_PROVIDER_FAILED`，`INTENT_PROVIDER_USED`标记RuleBased及FallbackUsed=true。
- Agent链路：隔离派发的JEV路径与HTTP失败降级路径各调用模拟AI回调1次、各生成Shadow Reply Preview 1条；审计串起Provider使用、AI响应和预览；SentMessages=0，无send request。
- 实际CLI一帧启动：有Key时打印`INTENT_PROVIDER_SELECTED: JEV; FallbackUsed=False`；在独立进程移除Key后打印`RuleBased; FallbackUsed=True`，并在agent-pipeline.jsonl中记录不可用原因`ApiKeyMissing`。两次均AI OFF、Send OFF，因当前千牛窗口不可捕获而`CompletedWithIssues`、0帧成功、0消息、0 AI调用，不能视为真实买家JEV链路验收。日志位于`Output/ValidationV127/cli-default.log`及`cli-no-key.log`。
- V1.26曾对20条合成问题完成真实TypeSafe请求；本轮新增的逐消息Agent派发测试使用隔离HTTP响应，没有向TypeSafe发送真实买家问题。

## 当前状态与下一步

已达到应用Shadow模式的“Intent Provider选择 → JEV默认 → RuleBased降级”代码与隔离测试。真实千牛消息经默认JEV的端到端运行仍待可捕获窗口和明确的测试会话。下一步宜先在可见测试会话验证实际延迟与降级率，再凭人工标注数据评估Hybrid Intent Strategy；不应仅凭V1.26合成样本自动改动Decision或知识策略。