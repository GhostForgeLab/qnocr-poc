# V1.8 AI Provider / Model 配置层验证报告

实现提交：`7533c274cdd79126d2ffd7fff8c87de28d7daaff`。本报告随独立验证提交保存。

## 结论

已在 qnocr-poc 的隔离 Extension 宿主增加 `IAIProvider`、`OpenRouterProvider` 和非密钥配置文件。
合成输入经原 qnbot Agent Pipeline、可配置 Provider 产生 **AI Calls 1、Reply Preview 1、SentMessages 0**。
默认模型 `openai/gpt-5.5` 从 `Config/ai-provider.json` 读取；另一模型值由同一文件结构读取、
备份后应用到隔离配置快照并回读，再恢复默认值。未修改生产 qnbot 或真实买家会话。

## 原因、文件与架构影响

此前 OpenRouter 端点及直接冒烟调用散落在隔离宿主里，模型由隔离 qnbot 设置固定，
缺少一个可审查的非密钥配置入口。现在 Agent 调用 `IAIProvider.GenerateAsync`，
工厂按 `ProviderName` 选择实现；`OpenRouterProvider` 核对 HTTPS 端点、已应用模型及启用状态。
独立 Provider 冒烟的 HTTP 请求、API Key 读取与响应解析已收拢到实现类。
**实际 Agent 回复仍交给原 `StreamingBuyerAnswerService.GetAnswerAsync`，保留其 Prompt、知识判断、路由和回复解析**；
这不是对 qnbot 内部生成服务的替换。其他 Provider 需要新增实现并单独验证。

新增：`Config/ai-provider.json`、`Validation/QnbotCompatibility/AIProviderConfig.cs`、
`IAIProvider.cs`、`OpenRouterProvider.cs`、`Validation/ChangeDetectionChecks/ProviderLiveSmoke.cs`、本报告。
修改：`Program.cs`、`README.md`、`Shadow/AgentShadowClient.cs`、`QnocrShadowRunner.cs`、
`ShadowAdapterClient.cs`、`Validation/ChangeDetectionChecks/Program.cs`、
`Validation/QnbotCompatibility/AiShadowRuntime.Provider.cs`、`AiShadowRuntime.cs`、`Program.cs`。
删除：无。

MessageEnvelope schema v1、qnbot 核心、Agent Pipeline 主业务逻辑、Reply Preview 生成与发送逻辑均未修改。
qnbot 工作树保持干净，基线提交 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

## 配置与参数边界

提交的 JSON 仅含 `ProviderName`、`Endpoint`、`Model`、`Temperature`、`MaxTokens`、`Enabled`。
默认值为 OpenRouter / `https://openrouter.ai/api/v1` / `openai/gpt-5.5` / `0.15` / `220` / `true`。
`--ai-config-check` 读取文件；`--ai-provider-apply-config` 先备份隔离设置，再写入模型并回读。
Agent 运行时模型不匹配会失败，不会继续使用旧模型。

原 qnbot 生成服务在未修改源码中固定 `temperature=0.15`、`max_tokens=220`。
因此 **Agent Pipeline 不能在本阶段应用不同的这两个值**；配置层明确拒绝不一致设置。
独立 OpenRouter Provider HTTP 冒烟可以使用配置中的不同值，已用 `0.3` / `300` 验证。
要让原 Agent 生成链真正可调这两个参数，需要后续获得该核心调用的受支持注入点或另立明确范围，
本轮没有绕过原 Prompt/业务链。

## 验证结果

| 检查 | 结果 |
|---|---|
| .NET 8 主项目与验证项目 | Build Success；0 警告、0 错误 |
| .NET Framework 4.8 隔离宿主 | Build Success；全量编译保留原 qnbot 源码既有 CS4014 警告 |
| 累计回归 | 241/241，通过 |
| qnbot 原入口回归 | 17/17，通过；禁止业务调用 0 |
| 默认文件读取 | `Model=openai/gpt-5.5`、Endpoint 正确、`ApiKeyIncluded=false` |
| 改模型读取与应用 | 临时文件 `openai/gpt-4.1-mini` 被读取，隔离设置应用后回读一致；随后恢复 `openai/gpt-5.5` |
| 独立 Provider HTTP 冒烟 | 合成句 HTTP 200，解析 `OPENROUTER_OK`；配置 `Temperature=0.3`、`MaxTokens=300` 也成功 |
| Agent Pipeline 合成真实 AI 调用 | `Provider=OpenRouter`、`Model=openai/gpt-5.5`、AI Calls 1、Reply Preview 1、SentMessages 0 |
| 参数不兼容 | Agent 配置 `0.3` / `300` 显式拒绝，未伪称生效 |
| 安全 | 配置中含 `ApiKey` 或非 OpenRouter 目的地被拒绝；最终运行 13 个文本证据文件未发现已保存 Key 明文 |

合成 Agent 输入为“你好，请回复一句简短的测试问候。”；回复预览为“您好，在的，有什么可以帮您？”。
该测试不含真实买家数据，也不构成模型效果评测。
证据目录：`Output/ValidationV18/provider-smoke_20260925_212912`；
其中 `agent-pipeline.jsonl` 可追踪 `MESSAGE_CAPTURED` → `AI_REQUEST` → `AI_RESPONSE` → `REPLY_PREVIEW_CREATED`，
`state/outbox/<MessageId>/reply-preview.json` 与 `attempt_0001/shadow-adapter-result.json` 保留预览和原 qnbot 回执。
控制台证据：`Output/v18-provider-pipeline-smoke-final.log`、`v18-provider-params-smoke.log`、
`v18-config-alternate.log`、`v18-apply-alternate.log`、`v18-apply-default.log`、
`v18-secret-audit-final.log`、`v18-checks.log`、`v18-original-entry.log`。

API Key 沿用现有隔离 qnbot 设置及原环境变量配置方式，不在 JSON 或源码中出现明文值。
`.tools/` 和 `Output/` 被 Git 忽略；提交内容不含这两处的运行时配置、日志或截图。
密钥审计仅证明本次指定输出范围未检出已保存 Key 的字节串，不等于任意外部系统的全域审计。

## 当前状态与下一步

已达到 **Agent Pipeline → 可配置 OpenRouter/模型 → AI Reply Preview**，发送始终关闭。
尚未实现第二家 Provider，也未让原 Agent 核心生成链支持任意温度和 Token 上限。
下一步可评估真实身份绑定、商品上下文关联、真实千牛发送与长时间运行稳定性；不建设知识库。

回滚：停止本项目 Shadow，回滚实现提交并重建；必要时从
`.tools/ai-shadow/provider-backups/` 恢复本地隔离设置。生产 qnbot 配置未被修改。
