# V1.30 Agent Console MVP 验证报告

## 结果

已提供独立的本地Web客服辅助工作台：会话列表 → 该Shadow运行采集的买家消息时间线 → Intent/Provider/Confidence → 当前ProductContext → KnowledgeStatus/Source → AI Reply Preview。人工可编辑回复、批准当前文本、复制到本机剪贴板，并记录Accepted、Edited或Rejected反馈；人工绑定的当前商品可清除。页面不调用千牛发送能力，Send始终OFF。

## 修改原因与架构影响

此前Agent Pipeline已有本地预览和CLI人工操作，但日常客服需要把会话、分析、商品、知识、回复放在同一个可查看入口。本轮新增AgentConsole ASP.NET Core本机项目，只绑定127.0.0.1，启动时生成访问令牌；页面与表单均要求令牌，HTTP响应禁缓存并限制内容策略。读取层只展示指定run/state配对的Shadow预览，写操作检查预览确属本运行批次。

页面复用既有AssistantReplyService.Edit/Approve/Copy/RecordFeedback与ManualProductResolver.Clear。清除人工商品后立即将Session当前绑定置为Unknown；OCR来源不可从页面人工清除。MessageEnvelope、qnbot核心、Agent Pipeline分类/知识/AI生成与发送逻辑均未修改；新增的只是本地UI、只读投影模型与人工操作路由。客服复制之后仍须自行切换千牛并人工粘贴发送。

## 文件与提交

实现提交：6a77e491d65313a8f184ada3d494d9a28f5bb64a。新增AgentConsole/AgentConsole.csproj、Program.cs、AgentConsolePages.cs、README.md、Models/AgentConsoleView.cs、Shadow/AgentConsoleReadService.cs、Validation/ChangeDetectionChecks/AgentConsoleChecks.cs；修改Product/ManualProductResolver.cs、相关回归检查、主项目编译排除项与.gitignore；删除文件无。

启动示例与操作说明见AgentConsole/README.md。需要显式指定匹配的--state-dir和--run-dir，服务仅在本机开放；本轮测试后已停止服务。

## 验证

- 编译：AgentConsole、主项目和检查项目成功，0警告、0错误。
- 回归：576/576检查通过（V1.29基线564，新增12）；原入口离线回归17/17，ForbiddenBusinessCalls=0。日志：Output/ValidationV130/regression.log。
- 真实Shadow只读UI：使用V1.28已授权的测试买家运行批次，首页显示1个Session；会话页显示10条Shadow新消息、JEV Provider/Intent、Knowledge Missing状态和AI建议。无令牌GET返回403；合法首页和会话页均返回200。历史基线不被伪装成新消息。
- 合成操作UI：隔离的3条测试预览显示Session、买家消息/时间/来源、JEV意图和0.9置信度、人工商品ID、Knowledge Available及AI回复。POST编辑=200、批准=200、复制=200，Windows剪贴板内容与批准文本完全一致。反馈Edited、Accepted、Rejected各1条均成功；Edited记录原AI回复、最终人工文本和ProductId。人工商品清除=200，Session立即从Bound变为Unknown。
- 审计与安全：原有assistant-actions.jsonl含HUMAN_EDITED、COPY_ACTION、FEEDBACK_RECORDED；合成状态无send-request.json。真实V1.28批次SentMessages=0，本地Console没有发送路由。服务层拒绝不属于当前run的PreviewId以及不匹配的run/state配对。

## 当前状态与下一步

已达到“Agent Console → 人工客服辅助”的MVP验收。界面当前通过手动刷新查看新的Shadow预览，消息时间线只包含本运行采集的新消息；并非千牛完整历史聊天镜像。若投入实际客服日常使用，下一步应验证操作员身份与多用户权限、状态目录保留策略、实时刷新和真实使用体验；继续保持人工复制发送，不能因此开启自动发送。
