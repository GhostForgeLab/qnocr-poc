# V1.12 AI客服辅助工作台基础层验证报告

日期：2026-09-26（Asia/Shanghai）。实现提交：`be6e352d76a7e3276e9472def7661ce049b93326`。本文件由独立验证提交保存。

## 结论与边界

已建立 `ReplyPreview → CLI人工查看 → 编辑 → 审核 → Windows剪贴板复制 → 人工自报结果` 的本地辅助链路。
真实平台Buyer身份仍未达到Verified，因此仍是客服副驾驶：程序不粘贴、不点击千牛发送、不调用Send Adapter。
演示使用既有合成Provider预览，人工结果记录为`NotSentReported`；这不代表真实买家消息或人工实际发送验收。
复制后的 `PastedAndSentReported` 也只是操作者自报，并非平台发送回执。

## 修改原因与架构影响

V1.11-C证明当前OCR买家只有Observed/Mapped/Test身份，没有运行时平台证据；不能把AI预览直接交给真实发送。
本阶段把预览作为人工核对和修改的入口，保留原AI文本，并把复制行为与操作者、时间关联，避免“生成了建议”等同于“已发送”。

- `MessageEnvelope`结构：未修改。
- qnbot核心、Agent Pipeline主流程、AI Provider、知识库、商品详情、发送逻辑：未修改。
- 原`ReplyPreview`结构和`reply-preview.json`：未修改；新增独立`assistant-reply.json`侧车状态及`assistant-actions.jsonl`。
- `DurableShadowDispatch`生成预览时初始化侧车；旧预览首次查看可补建。`Program`新增`--assist-*` CLI分支，不接入Send Adapter。
- `AIGenerated → HumanEdited → Approved → Copied`使用当前文本SHA-256审核；修改使旧哈希失效。复制失败保持Approved并写`COPY_FAILED`。
- 风险提示只取身份状态和商品引用有无：Test/未Verified为High，Verified但无引用为Medium，Verified且有引用为Low。Low不代表产品事实正确、会话目标可信或可自动发送。
- `--operator`是审计标签，不是经过认证的登录身份。人工结果只允许复制后记录一次，不提供平台回执。

## 修改文件

新增：`Models/AssistedReply.cs`、`Shadow/AssistantCommand.cs`、`Shadow/AssistantReplyService.cs`、
`Validation/ChangeDetectionChecks/AssistantChecks.cs`、本报告。
修改：`Program.cs`、`README.md`、`Shadow/DurableShadowDispatch.cs`、
`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`、`Validation/ChangeDetectionChecks/Program.cs`。
删除：无。qnbot工作树未改。

## 验证结果

| 项目 | 结果 |
|---|---|
| .NET 8主项目及验证项目 | Build Success，0警告、0错误 |
| .NET Framework 4.8隔离宿主 | Build Success，0错误；仅原上游`BuyerMessageBurstCoordinator.cs`既有CS4014警告 |
| 累计检查 | 293/293通过，高于要求的276项 |
| qnbot原入口 | 17/17通过，`ForbiddenBusinessCalls=0`；离线隔离回归，非运行中Bot |
| AI调用 | 本轮没有新的外部AI请求；合成Agent回归记录`AiCalls=1`并生成新预览。CLI演示复用V1.8已保存的OpenRouter合成测试预览，不能把它计为本轮网络调用 |
| Reply Preview | 自动侧车初始化、旧预览补建、列表与详情展示均通过；原`SendStatus=Shadow` |
| 人工编辑 | 原AI回复保留、修改文本/时间/操作者持久化；重启后可读；旧哈希无法审核 |
| 审核与复制 | 未审核禁止复制；审核后系统剪贴板写入成功，`Get-Clipboard -Raw`与持久化修改文本一致；重复复制禁止 |
| 审计与结果 | `AI_REPLY_CREATED → HUMAN_EDITED → HUMAN_APPROVED → COPY_TO_CLIPBOARD/COPY_ACTION → MANUAL_OUTCOME_RECORDED`；演示结果`NotSentReported` |
| 异常与风险 | 剪贴板故障记录`COPY_FAILED`且状态保持Approved；Test/Unknown身份High、合成Verified缺产品Medium、有产品Low |
| 安全 | 未创建`send-request.json`或`send-result.json`，无Send Adapter调用，程序`SentMessages=0`，真实千牛发送0 |

本地日志（Git忽略）：`Output/v112-checks.log`（293项），
`Output/ValidationV112/original-entry/run.log`（17项），
`Output/ValidationV112/assistant-demo/{list,show,edit,approve,copy,outcome}.log`，
`Output/ValidationV112/assistant-demo/state/assistant-actions.jsonl`。
演示PreviewId为`819375c7776a45ada464a31cc27410cb`，BuyerId=`buyer-test`，风险High。
CLI列表/编辑/审核/复制/未发送结果均以退出码0完成；状态仍为Shadow。

## 当前状态与下一步

达到“AI回复建议 → 人工核对与编辑 → 复制 → 人工自行处理”的基础辅助流程。
**没有证明人工在真实千牛中粘贴并发送，也没有平台送达回执**；程序只能记录操作者自报。
真实Buyer身份仍未Verified，自动发送继续关闭。下一步可按实际使用体验评估多店铺隔离、更强会话上下文和操作员认证；知识库及商品资产体系留待独立V2决策。
