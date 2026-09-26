# V1.10 长时间运行稳定性验证报告

实现提交：`e4e5e452557a391ab1eaa7b5afb4b25604e062e5`。本报告在完成 120 分钟真实运行、重启核对后提交。

## 修改原因与架构影响

V1.9 已验证单次输入与预览，但缺少可持续观察的健康指标、Session 运行状态、日志大小边界和长时间运行证据。
本次在 qnocr-poc 外层增加每分钟健康快照、Session Active/Inactive/Closed 生命周期、保留全部审计的 JSONL 滚动及运行/状态目录容量上限。
达到容量上限时保留现有文件并停止该轮，不删除历史验证日志。MessageEnvelope、qnbot 核心、AI 业务链、商品知识及发送行为均未修改。

新增：`Shadow/RunLogWriter.cs`、`Shadow/SessionRunLifecycle.cs`、`Validation/ChangeDetectionChecks/StabilityChecks.cs`、`Validation/Summarize-StabilityV110.ps1`、本报告。
修改：`Program.cs`、`README.md`、`Shadow/AgentPipelineTrace.cs`、`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、
`Validation/ChangeDetectionChecks/Program.cs`、`ShadowChecks.cs`。删除：无。
Session业务模型与MessageEnvelope没有改动；`run-state.json` 是独立运行状态，`session.json` 的买家与商品引用在关闭后仍保留。

## 编译、回归与故障注入

- .NET 8 主项目及验证项目 Build Success，0 警告、0 错误。
- 累计 264/264 项检查通过；qnbot 原入口 17/17 项通过。
- 合成短暂 OCR 失败后可继续捕获；AI 失败被保留，同 Session 后续消息仍可生成预览。
- 单条 Adapter 异常留下 Uncertain，后续消息仍派发；重启后 Completed 与 Uncertain 都不盲目重放。
- Session 空闲转 Inactive，活动恢复 Active，停止时 Closed，下一轮可重开；商品引用仍在原 Session 状态。
- 8 MiB JSONL 活跃文件滚动为 `log-archive` 段，合成检查证明旧、新两条记录均保留。
- 极小运行容量上限触发 `StorageLimitReached`，保留 summary 与审计证据，SentMessages=0。

## 真实千牛连续运行

运行目录：`Output/Shadow/shadow_20260926_104905_985_bce7394b5a4448488af24bfb6e5fb2a2`。
模式：AI OFF、Send OFF；独立状态：`Output/State/V110-long-run`；上限：运行目录 1024 MiB、状态目录 4096 MiB。
启动：`2026-09-26 10:49:05 +08:00`。首帧只建立历史基线，不派发旧消息。

结束时间：`2026-09-26 12:49:07.917 +08:00`；自然停止，进程退出码 0。
实际运行时长：`7201.912` 秒（2 小时 1.912 秒），达到 120 分钟要求。
最终状态：`Completed`；没有触及运行/状态容量上限。
帧数：尝试 `2779`，成功 `2779`，失败 `0`；Product OCR `10` 次、Chat OCR `10` 次。
新消息与Session：OCR确认观察 `12` 次（含历史/重复），新增买家消息 `0`、Envelope `0`、派发尝试 `0`；共观察到 `4` 个真实界面买家会话。两名买家约在第110分钟由界面切换出现，帧图像确认是不同会话，不是昵称OCR抖动。
ProductReference与AI：ProductReference `4`；AI Calls `0`、Reply Preview `0`（长跑采用AI OFF）。
错误、内存和磁盘：Frame错误 `0`、恢复错误 `0`、日志错误 `0`、总ErrorCount `0`；工作集从 `74.85 MiB` 到 `92.54 MiB`，峰值 `94.32 MiB`；运行目录 `6.10 MiB`、状态目录 `6.10 KiB`、日志归档段 `0`。单轮内未见失控增长，但不能以2小时曲线证明永不泄漏。
重复派发、状态损坏、发送：控制台未发现Unhandled exception；4个Session在停止时均记录 `SESSION_RUN_CLOSED`。长跑没有新消息，因此真实重复派发/串线没有被活动消息触发；合成测试验证去重与跨Session隔离。`SentMessages=0`。

每分钟原始指标在 `health.jsonl`；恢复记录在 `recovery.jsonl`，Session运行状态在 `session-lifecycle.jsonl` 与状态目录的 `run-state.json`。
结束后以 `summary.json` 和控制台 `Output/v110-long-run.log` 交叉核对。

## 重启、审计与安全核对

只读汇总 `Output/ValidationV110/long-run-analysis.json` 对照 `summary.json`、118个健康采样、Session生命周期及控制台日志：
`ReachedTwoHours=true`、`SessionOpened=4`、`SessionClosed=4`、`DispatchAttempts=0`、
`CrossSessionMessageIds=[]`、`UnhandledExceptionInConsole=false`。唯一恢复记录为正常的 `WindowBound`，没有帧失败。

主长跑结束后，使用相同 `Output/State/V110-long-run` 重启两帧，独立日志为 `Output/v110-restart-check.log`：
`Completed`、2/2帧、`SESSION_RUN_RESUMED=1`、商品引用仍为1、原Session创建时间保持；
当前商品未显示时 `ProductId` 为空。重启时新消息、AI Calls、SentMessages均为0，随后该Session再正常Closed。

日志滚动与极小容量阈值由合成检查验证；本次真实长跑只占6.10 MiB，没有实际产生归档段或触发容量停机。
这些边界用于阻止单轮无限增长，系统不会自动删除已有审计与验证日志。

## 当前状态与下一步

在本机当前千牛窗口及AI OFF/Send OFF条件下，Agent视觉输入层完成连续2小时观察，帧捕获与健康记录稳定，4个Session最终关闭。重启同一状态目录又成功2/2帧，记录 `SESSION_RUN_RESUMED=1`，原SessionId、创建时间、商品引用均保留，旧消息不重复派发，发送0。结论限于此测试环境和负载；真实新消息高并发、持续AI调用及多店铺隔离仍未由本次长跑覆盖。

本轮不启用AI与真实发送。AI失败的隔离恢复由合成故障注入验证，不把它记为真实长跑期间的AI调用。
日志不会自动删除；单轮容量上限限制增长，长期保留策略需要人工归档/容量规划。
下一步仅评估真实千牛发送闭环、多店铺隔离和V2知识库系统设计，本阶段不建设知识库。
