# V1.7 人工确认发送闭环验证报告

实现提交：`b32d63d4f4d415da7d0ba3d2ec2d1f2b9b1283ea`。本报告随独立验证提交保存。

## 结论与范围

按用户选择，本轮完成**隔离发送**验收：把 V1.6 的一份真实 AI Reply Preview 复制到独立测试状态目录，
依次进行预览审阅、显式确认、Send Adapter 调用和结果落盘。
隔离适配器只记录本地请求，`ActualSent=false`；**没有向真实买家发送消息，也没有调用生产 qnbot 发送能力**。
状态 `Sent` 在本轮只表示隔离适配器已确认记录，不代表千牛投递成功。

## 修改原因与架构影响

V1.6 的回复预览不能直接构成发送授权。V1.7 在 qnocr-poc 外层增加独立的投递状态、确认命令、文本哈希核对、
Send Adapter 端口及不可重复的发送请求记录。默认 `--shadow` 继续 AI OFF、Send OFF；打开预览不会发送。

MessageEnvelope schema v1、qnbot 核心、既有 AI 生成流程和 qnbot 原发送逻辑均未修改。
新增 `IQnbotSendPort` / `QnbotSendAdapter` 扩展契约，要求经过验证的平台买家和卖家身份；
OCR 的 `buyer-test:*` 身份在调用该端口前被拒绝。当前 CLI 只暴露 `--adapter isolated`，没有真实发送命令路径。
原 qnbot 工作树保持干净，基线提交 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

新增源文件：`Models/ReplyDelivery.cs`、`Shadow/ConfirmSendCommand.cs`、
`Shadow/IReplySendAdapter.cs`、`Shadow/PreviewConfirmationService.cs`、
`Validation/ChangeDetectionChecks/ConfirmationChecks.cs`、本报告。
修改文件：`Program.cs`、`Shadow/DurableShadowDispatch.cs`、`Shadow/QnocrShadowRunner.cs`、
`Validation/ChangeDetectionChecks/Program.cs`、`README.md`。删除文件：无。

## 状态、安全和审计

预览落盘后状态从 `Created` 转为 `WaitingConfirmation`；只有提供 `ReplyPreviewId`、操作者标签和与审阅正文完全一致的 SHA-256，
才依次进入 `Confirmed`、`Sending`、`Sent` / `Failed`。等待中的预览可取消为 `Cancelled`。
确认记录操作者标签、命令执行系统用户、确认时间及正文哈希；`send-request.json` 保存实际请求正文，
`send-result.json` 保存适配器回执。`send-lifecycle.jsonl` 把 CaptureId、SessionId、AiRequestId、AiResponseId、
ReplyPreviewId 和 SendRequestId 串到 `SEND_RESULT`。

有效但不存在的预览 ID 写 `PREVIEW_NOT_FOUND`；重复确认写 `DUPLICATE_CONFIRM` 且不重发；
适配器失败写 `SEND_FAILED`。正文改动、哈希不符或已取消也拒绝发送。
异常或不确定结果没有自动重试路径。

`--operator` 是操作者自报标签，并非身份认证；系统用户另行记录，真实人工身份绑定仍待设计。

## 测试结果

| 项目 | 结果 |
|---|---|
| .NET 8 主项目 `dotnet build --no-restore -warnaserror` | 成功；0 警告、0 错误 |
| 累计检查 | 241/241，通过；原 229 项保持通过 |
| qnbot 原入口回归 | 17/17，通过；禁止业务调用 0 |
| 默认真实千牛 Shadow 单帧 | 1/1 成功；`AI_ENABLED=false`、`SEND_ENABLED=false`、`SentMessages=0` |
| 隔离确认前 | `WaitingConfirmation`；Send Request 0、适配器调用 0 |
| 显式隔离确认后 | Send Request 1、`IsolatedRecorded` 1、`ActualSent=false`、真实 `SentMessages=0` |
| 重复确认 | `DUPLICATE_CONFIRM` 1；Send Request 仍为 1 |
| 异常/安全检查 | `PREVIEW_NOT_FOUND`、`SEND_FAILED`、取消、文本改动、哈希不符均不产生未经确认的发送 |
| qnbot 端口边界 | 测试身份被拒绝；已验证身份仅在合成端口注入测试中委托一次，未连接真实 qnbot |

隔离测试使用原 V1.6 `ReplyPreviewId=7e4fcb2ab9e848ec826d1d2024c4dab1` 的**副本**，
位于 `Output/ValidationV17/isolated-demo`；原 `Output/State/V16-acceptance` 下预览和状态没有被修改。
审阅文本为“规格以页面可选项为准，若没有200ml选项可能暂未上架哦。”，
正文 SHA-256 为 `4351B3FC9BC7C3E62FBC304CDD57A4D05ED01F2D68AFBE3807794A4F8B1BA166`。
测试操作者标签为 `Codex-Isolated-Test`，确认时间 `2026-09-25 20:55:54 +08:00`；
`SendRequestId=230f1fca4d4e4d03a07a2659e34f56e0`。

本地证据：`Output/v17-preview-review.log`、`Output/v17-confirm-isolated.log`、
`Output/v17-duplicate-confirm.log`、`Output/ValidationV17/isolated-demo/send-lifecycle.jsonl`、
同目录 `outbox/<MessageId>/send-request.json` 和 `send-result.json`。
回归日志：`Output/v17-checks.log`、`Output/v17-original-entry.log`；
默认 Shadow 日志：`Output/v17-default-shadow-final.log`。
含买家聊天与回复正文的原始文件均留在 Git 忽略的 `Output`。

## 当前状态与后续

已完成 `AI Reply Preview → 显式人工确认接口 → 隔离 Send Adapter → 可追踪 Send Result`。
**尚未验证真实千牛投递**；那需要真实平台身份绑定、运行中 qnbot 发送端口、具体预览内容的单次人工批准，以及回显核对。
下一步可评估 AI Provider/模型配置层、真实身份绑定、商品上下文关联与长时间运行稳定性；
本阶段不建设知识库。

回滚：停止本项目 Shadow，回滚 V1.7 实现提交并重建；保留被 Git 忽略的 `Output` 审计证据。
