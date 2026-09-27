# V1.28 真实 JEV Agent Pipeline 验证记录（进行中）

## 已实现

运行时 `INTENT_PROVIDER_RESULT` 记录 SessionId、Provider、Intent、LatencyMilliseconds、FallbackUsed。每轮 Shadow 在 `intent-stats.json`、健康日志及 summary 中记录 TotalIntentRequests、JEVSuccess、JEVFailure、RuleFallbackCount、AverageIntentLatencyMilliseconds。启动时缺 Key 另计 `JevUnavailableAtStartup`，不虚报为一次 JEV 请求失败。

新增本地人工入口：`--intent-review-list <runDir>` 展示经脱敏的买家问题及意图结果；`--intent-review-mark <runDir> <64位MessageId> IntentCorrect|IntentWrong <operator>` 只写一次人工判断，不修改分类器、规则、知识或模型。

实现提交：`0c70bbc98bc83ab8a263c0cc1093ff6e89d93249`；构建修复提交：`691c443d8d55eddfa20d831a85ec68d7fbd94ed9`（主项目排除 `Output/**/*.cs`，避免临时烟测源码进入正式编译）。MessageEnvelope、qnbot核心、Knowledge、回复AI与发送链未修改；扩展外层 Intent Trace/统计及 CLI。

## 修改文件与架构边界

新增 `Decision/IntentRuntimeStatistics.cs`、`Decision/IntentQualityReviewService.cs`、`Models/IntentRuntimeSnapshot.cs`、`Models/IntentHumanReview.cs`、`Validation/ChangeDetectionChecks/IntentRuntimeChecks.cs`；修改 `Decision/IntentClassifierFactory.cs`、`Models/IntentProviderTrace.cs`、`Models/MessageSession.cs`、`Shadow/AgentPipelineTrace.cs`、`Shadow/QnocrShadowRunner.cs`、`Program.cs`、测试文件及 `qnocr-poc.csproj`；删除文件无。只扩展外层 Intent 观察和人工反馈，不改 MessageEnvelope、qnbot 核心、Knowledge Provider、AI 回复生成或发送逻辑。

## 已验证

- 编译：检查项目与 Knowledge Console 项目均成功，0 警告、0 错误。
- 回归：542/542 检查通过（V1.27 基线531，新增11）；原入口离线回归17/17。检查日志：`Output/ValidationV128/regression.log`。
- 合成真实端点烟测：默认工厂通过现有Key向TypeSafe发送一条合成规格问题，JEV成功1/1、降级0、`PRODUCT_SPEC_QUERY`，平均分类耗时852.1ms；无真实买家数据、无回复生成或发送。
- 隔离派发：JEV正常与HTTP失败降级均产生 `INTENT_PROVIDER_RESULT`，保持模拟AI回调与Shadow Reply Preview；没有发送。统计、平均耗时、人工正确/错误标记及防重复记录通过检查。
- 真实环境只读预检：普通沙箱阻止 EnumWindows；交互式权限下千牛截图1/1成功，历史消息3条、新增0、AI调用0、SentMessages=0。建立的本地测试 SessionId 为 `A66A2D8E64F64A1A1A8990EEC5743AE21CFBCED6D2D2DB1734C574A4E259BC12`。预检日志：`Output/ValidationV128/window-check-elevated.log`。

## 待完成的真实验收

当前仍需测试会话保持可见、用户明确授权本轮第三方 AI 端点调用，并在新基线建立后发送10条测试买家问题。真实消息数、JEV成功/降级、平均耗时、Knowledge状态、AI回复预览及人工标签尚未取得，不能将上述隔离检查计为10条真实验收。Send保持OFF。
