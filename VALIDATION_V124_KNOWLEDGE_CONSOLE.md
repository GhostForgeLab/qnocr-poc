# V1.24 Knowledge Console MVP 验证报告

## 结果

已提供独立的本地 Web 知识控制台。页面可查看商品列表（SellerId、ProductId、ProductName、KnowledgeStatus、Version、UpdatedAt），创建一个测试商品 Draft，编辑 Draft 字段，逐条提交审核、批准或拒绝，明确确认发布，并从现有 Published 字段创建 Revision Draft。所有写入复用 V1.21–V1.23 的 Authoring、Review、Revision 服务；没有自动批准、自动发布、AI 调用或千牛发送。

## 修改原因与架构影响

此前知识操作依赖 CLI/JSON，非开发人员不易逐项核对与操作。本轮增加单独的 `KnowledgeConsole` ASP.NET Core 项目，仅绑定 `127.0.0.1`，启动时生成本轮访问令牌；所有页面及表单都需令牌。发布表单另需人工勾选确认，操作员必须填写名称。服务层增加 Draft 编辑（仅 Draft 状态）、草稿列表与修订列表；已发布字段修改必须走 Revision，旧版本保留。控制台仍只创建 `IsTestData=true` 的单商品资料。

`MessageEnvelope` 和 qnbot 核心未修改。Knowledge Schema 只给 Authoring 输入增加可选 Size、Weight、FAQ、Restrictions；Repository 与 Provider 接口未改；AI Provider、Prompt、发送链未改。主项目排除 Web 项目源码编译，Web 项目引用现有主项目。前一条真实商品迁移请求的未提交试验改动没有纳入本次提交；本轮未取得可核对的真实商品资料。

## 文件与使用

实现提交：`d8d0e00100cebe82a0c76887831aeb2cbaa79f65`。新增 `KnowledgeConsole/KnowledgeConsole.csproj`、`Program.cs`、`KnowledgeConsolePages.cs`、`README.md`、`Validation/ChangeDetectionChecks/KnowledgeConsoleChecks.cs`；修改 `.gitignore`、`Knowledge/KnowledgeAuthoringService.cs`、`Knowledge/KnowledgeReviewService.cs`、`Knowledge/KnowledgeRevisionService.cs`、`Models/KnowledgeDraft.cs`、主 `Program.cs`、`Shadow/QnocrShadowRunner.cs`、测试入口及主项目文件；删除：无。

启动方式见 `KnowledgeConsole/README.md`：`dotnet run --project KnowledgeConsole/KnowledgeConsole.csproj -- --state-dir D:\CodexWork\qnocr-poc\Output\KnowledgeConsole --port 18724`。终端输出本轮本机访问地址。状态目录位于 `Output/`，不提交草稿或商品内容。

## 测试

- 编译：Web 项目与检查项目均成功，0 警告、0 错误。
- 回归：495/495 检查通过（V1.23 基线 485，新增 10）；原入口离线回归 17/17，ForbiddenBusinessCalls=0。检查日志：`Output/ValidationV124/regression.log`。
- 服务层：测试商品的 Capacity、Size、Weight、Material、Feature、FAQ、Restriction 映射为 7 个 Draft 项；编辑 Draft 后 Provider 仍不可读；Approved 仍不可读；明确发布后 Provider 返回更新后的 `Capacity=250ml`；Revision 发布后返回 v2 `Capacity=300ml`。没有 send request。
- HTTP 冒烟：带本地令牌 GET=200，无令牌 GET=403；表单创建 5 个测试知识项；无确认的发布 POST=400，状态仍 Approved；逐项确认后 5 项 Published。修订无确认 POST=400；确认后当前值为 `300ml/console-v2`，旧值 `250ml/console-v1` 保留且不再供 AI 使用。
- AI：原有隔离回归的模拟 Agent 回调与 Shadow Reply Preview 继续通过；本轮没有新的外部 AI 调用，不能据此评价真实商品回复质量。
- 安全：SentMessages=0；控制台没有发送入口。HTTP 请求日志设为 Warning 级，避免本地访问令牌进入常规请求日志。

## 当前状态与下一步

已达到“本地测试商品 → Web Draft → 人工审核 → Published → KnowledgeProvider 可读”的 MVP。下一步应先验证实际操作体验，并为真实商品录入增加操作员鉴权、来源核对和生产级审计；真实商品及真实买家 AI 验收仍需独立资料和授权。
