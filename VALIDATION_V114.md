# V1.14 真实客服辅助试运行数据沉淀验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`551e4cce2b575eb7b8843c30eb34164bf53897a5`。本报告在独立验证提交保存。

## 结论

已建立按单轮Shadow运行归档的Evaluation Batch，并完成一轮**真实千牛测试买家会话**试运行：3条新买家消息产生3条AI建议，用户人工评价Accepted 2、Edited 1、Rejected 0，反馈覆盖率100%。本轮只生成与复制本地预览，`SentMessages=0`；**未验证人工是否在千牛中粘贴并发送**。当前Buyer仍是`buyer-test:*`隔离映射，不是平台Verified身份。

## 修改原因与架构影响

V1.13已有单条反馈和按运行目录统计，但没有明确的测试周期、操作员评价原因或人工问题分类。V1.14以一个Batch绑定一轮Shadow运行，保存BatchId、评价开始/结束时间、环境声明和操作员标签；反馈新增人工分类、原因、脱敏原话备注与BatchId。报告统计咨询Session、买家消息、AI建议、三类反馈、覆盖率与人工标记的“缺少商品信息”问题，不自动判断问题类别或建设知识库。

- `MessageEnvelope`：未修改。
- qnbot核心、Agent Pipeline、AI业务逻辑、发送链：未修改；qnbot工作树干净。
- 扩展层：新增Batch模型、服务、CLI与回归；原反馈/样本增加兼容旧文件的可选字段。`--operator`只是本地审计标签，不是登录认证；`Environment`由操作者声明。
- 每轮运行最多一个Batch；Batch创建要求运行元数据明确`SendEnabled=false`且发送数为0，历史发送数未知只可使用`HistoricalReview`模式。Batch关闭后不接受反馈。
- 分类和原因由人工指定；样本继续尽力遮盖常见凭证、手机号、邮箱，原始预览仍按本地状态目录策略保管。没有自动训练、知识库、商品资料库或自动发送。

## 修改文件

新增：`Models/EvaluationBatch.cs`、`Shadow/EvaluationBatchCommand.cs`、
`Shadow/EvaluationBatchService.cs`、`Validation/ChangeDetectionChecks/EvaluationBatchChecks.cs`、本报告。
修改：`Models/AssistantFeedback.cs`、`Program.cs`、`README.md`、`Shadow/AssistantCommand.cs`、
`Shadow/AssistantReplyService.cs`、`Shadow/AssistantRuntimeDashboard.cs`、
`Validation/ChangeDetectionChecks/Program.cs`。删除：无。

## 编译、回归与功能验证

.NET 8主项目和检查项目Build Success，0警告、0错误；.NET Framework 4.8离线兼容宿主Build Success。累计检查 **327/327**，高于310项基线；qnbot原入口 **17/17**，`ForbiddenBusinessCalls=0`。

隔离检查覆盖Batch创建/关闭、同一运行禁止重复Batch、操作者匹配、反馈原因与类别必填、已关闭Batch拒绝追加、报告分母和覆盖率、人工问题过滤、发送关闭前置、敏感备注脱敏及无发送请求。真实CLI已完成Batch创建、3条预览的人审状态与反馈写入、报告生成及关闭。BatchId：`32ad65ba0b1445cdb5807138f5c05cb4`；环境声明`RealQianniuShadow`；评价时间`19:29:42.787`至`19:38:01.837 +08:00`。

## 真实试运行结果

获用户本轮授权后，AI Shadow仅允许测试Session `A66A2D8E64F64A1A1A8990EEC5743AE21CFBCED6D2D2DB1734C574A4E259BC12`调用已配置的OpenRouter `openai/gpt-5.5`；Send始终OFF。首次截图只建立历史基线。运行`168.76`秒，成功帧`52/52`、采集失败0、错误0；买家新消息3，AI Calls3，Reply Preview3，重复观察3次但重复派发0，当前商品ID三条均为空，`SentMessages=0`。同一Session第二、第三条预览均保留前一条问题，连续性检查`2/2`。

| 人工分类 | 测试买家问题 | AI建议 | 人工结果 |
|---|---|---|---|
| 商品规格 | 这款有200ml规格吗？ | 规格以页面选项为准，没看到200ml的话我帮您转人工确认下。 | **Edited**；原因归类“缺少商品信息”。用户提供最终文本：“这款有200ml规格，可以直接选择200ml哦”。这只是人工录入，系统未核实商品规格。 |
| 发货 | 今天下单多久发货？ | 发货时效以商品页和下单页显示为准哦，您可以下单前看下预计发货时间。 | **Accepted**；用户备注“回复符合实际，没有过度承诺”。 |
| 售后 | 刻字后不喜欢能退吗？ | 刻字属于定制商品，一般不支持无理由退换，具体情况建议联系人工客服确认。 | **Accepted**；用户备注“符合定制商品售后规则”。 |

总咨询Session 1、买家消息3、AI建议3；采用2、修改1、拒绝0、未评价0。按**已评价建议3条**计算：AI采用率`66.7%`、人工修改率`33.3%`、拒绝率`0%`，反馈覆盖率`100%`。这些是本轮人工评价，不代表买家接受或平台实际发送。人工修改动作1次、剪贴板复制3次；实际人工键入时长与千牛手动发送结果未知。三条问题分类各1；人工标记“缺少商品信息”1条，是未来商品信息需求候选，不代表现在已验证ProductId或建设知识库。样本量仅3，不能推断长期质量。

## 安全与证据

- Batch状态Closed；状态目录内反馈文件3份，未发现所检查的API Key/Bearer/Cookie样式凭证；发送请求/结果文件0。AI建议和人工最终文本只在本地状态中保存，原始内容不提交Git。
- 三条预览`SendStatus=Shadow`。编辑后的第1条与两条原AI建议经人工评价后分别审核、复制到本机剪贴板；程序没有在千牛中粘贴、点击或发送。当前Buyer身份仍未Verified，真实发送能力不可用。
- 本地证据（Git忽略）：`Output/v114-checks.log`、`Output/ValidationV114/original-entry-final/run.log`、`Output/Shadow/shadow_20260926_192905_382_87b733f6b13243bda38b1697bddae40f/summary.json`、`Output/ValidationV114/live-batch-closed-report.log`、`Output/State/V114-real-trial/evaluation-batches/32ad65ba0b1445cdb5807138f5c05cb4/batch.json`及对应3份`assistant-feedback.json`。

## 当前状态与下一步

**已达到一轮真实客服辅助试运行的数据沉淀**，但只覆盖一个测试会话、三类问题，商品ID均缺失。继续多批次收集人工评价、商品引用核对和真实操作成本后，再决定Prompt优化、多店铺支持与qnbot集成评估。知识库设计可以作为后续独立议题；本阶段没有建设知识库或商品资料库。
