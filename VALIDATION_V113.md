# V1.13 AI客服辅助运行验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`c800a0e8508ea44c18c320021d05d02b888d773a`。本报告单独提交。

## 结论

按运行批次统计、人工反馈、脱敏会话样本和Session连续性观察已实现。隔离样本验证了完整的AI建议→人工编辑/审核→复制→反馈统计流程；真实千牛接待中心完成12帧只读Shadow稳定观察，但本轮没有新的测试买家消息，因而**尚未完成本轮真实新消息→AI建议→人工操作的全链路验收**。从V1.6既有真实Shadow记录只读提取了1条历史买家问题/AI建议样本，人工最终回复未知，明确标记`Unreviewed`，不补造采纳结论。

## 修改原因与架构影响

V1.12能够人工查看、编辑和复制单条预览，却无法按运行批次判断使用量、人工改写程度或会话连续性。V1.13新增只读仪表盘：通过`CaptureId`与指定Shadow运行目录关联共享状态，统计运行时间、消息、Session、AI建议、编辑动作、复制动作和明确反馈；不把旧批次混进当前统计。

每个预览最多一次显式`Accepted / Edited / Rejected`反馈。Accepted/Edited要求已复制，反馈文本按当前有效回复读取；Rejected可记录单独的人工最终回复，随后阻止对该AI建议继续审核/复制。反馈和样本存于同一`assistant-feedback.json`，包含买家问题、AI建议、人工最终回复和当前商品引用；常见凭证、手机号、邮箱在新增样本中尽力遮盖。未反馈预览只在只读样本视图里标记`Unreviewed`、人工最终回复留空，不额外落盘。旧原始预览仍按原状态目录保管，导出前需人工复核。

- `MessageEnvelope`：未修改。
- qnbot核心、Agent Pipeline与AI业务逻辑：未修改；qnbot工作树干净。
- 辅助层：新增反馈模型、按批次仪表盘、样本脱敏与CLI入口；原辅助审计改用已有8 MiB归档写入器，旧日志不删除。
- 自动发送、Send Adapter调用、模型训练、知识库或商品详情库：均未新增。
- `--product-assessment`和`--context-assessment`是人工标记，不是平台Verified证据；商品ID空值不会从历史引用补齐。

## 修改文件

新增：`Models/AssistantFeedback.cs`、`Shadow/AssistantRuntimeDashboard.cs`、
`Shadow/AssistantSampleRedactor.cs`、`Validation/ChangeDetectionChecks/AssistantOperationsChecks.cs`、本报告。
修改：`Program.cs`、`README.md`、`Shadow/AssistantCommand.cs`、
`Shadow/AssistantReplyService.cs`、`Validation/ChangeDetectionChecks/Program.cs`。
删除：无。

## 编译与回归

.NET 8主项目及检查项目Build Success，0警告、0错误；.NET Framework 4.8离线兼容宿主Build Success，仅保留上游`BuyerMessageBurstCoordinator.cs`既有CS4014警告。累计检查 **310/310** 通过，高于293项基线；qnbot原入口 **17/17**，`ForbiddenBusinessCalls=0`。

新增检查覆盖：反馈一次性、Accepted/Edited与复制状态匹配、Rejected后禁止复制、常见敏感字符串脱敏、无反馈比率为null、按批次隔离、审计归档后计数、两买家Session隔离、同会话Q1出现在Q2上下文、未知当前商品不借用历史ID，以及Send OFF下不生成发送请求。

## 运行统计与样本

| 验证 | 运行时长/帧 | 消息/Session | AI建议/调用 | 编辑/复制/拒绝 | SentMessages |
|---|---:|---:|---:|---:|---:|
| V1.13隔离客服样本 | 120秒/60帧（夹具） | 3/2 | 3/3（模拟AI结果） | 编辑动作2，复制2，拒绝1 | 0 |
| V1.13真实千牛只读观察 | 26.57秒/12帧 | 新消息0，基线Session1 | 0/0，AI OFF | 0/0/0 | 0 |
| V1.6历史真实Shadow记录，只读分析 | 150.84秒/37帧 | 处理消息1，Session2 | 1/1（历史调用） | 人工最终回复未知 | null（旧记录无字段） |

隔离样本明确反馈3条：Accepted/Edited/Rejected各1条，三项比率各33.3%，分母是**已反馈预览3条**，不是全部真实客服会话；这是人工复制/评价代理指标，不证明实际发送。该样本的人工商品标记Correct 1、Incorrect 1、Unknown 1；上下文标记Continued 1、Unknown 2。同一Session中Q1的文本及时间戳出现在Q2输入Context，另一个买家和商品保持独立。当前商品ID缺失的建议仍保持空值，2条有当前商品ID的关联均标记为未独立核验。编辑延迟包含人工等待，不等于键入耗时。

真实千牛观察的窗口为可见接待中心，12帧成功、失败0、错误0；首次截图只建历史基线，没有新消息事件。历史真实样本仅有买家问题、AI建议、当前商品ID和ContextMessageCount=1；没有人工最终回复，因此未计入反馈率。该旧批次`SentMessages`无法从旧日志确认，仪表盘保持null。本轮未向外部AI端点发起新请求，也未执行千牛粘贴、点击或发送。

本地证据（`Output/`被Git忽略）：

- `Output/v113-checks.log`：310项检查。
- `Output/ValidationV113/original-entry-final/run.log`：17项原入口回归。
- `Output/Shadow/shadow_20260926_164240_109_7c43efe3017545d7b98fff46c473be49/summary.json`与`Output/ValidationV113/live-dashboard.log`：本轮真实千牛只读观察。
- `Output/ValidationV113/historical-real-samples.log`与`historical-real-stats.log`：既有V1.6真实记录的脱敏只读分析。
- `Output/ValidationV113/cli-feedback/feedback.log`：用既有合成AI预览演示CLI记录Edited反馈；程序发送数0。

## 当前状态与下一步

V1.13统计和反馈基础可用于AI客服副驾驶试运行；**真实新买家消息到人工采纳的现场效果仍待客服实际操作数据**。真实Buyer身份仍未Verified，自动发送保持关闭。下一轮可在明确授权的测试会话收集新消息、人工最终回复与产品/上下文判断，再据样本决定多店铺隔离、Prompt优化和qnbot集成评估。知识库或商品资产体系不在本阶段实施。
