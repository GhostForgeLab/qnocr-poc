# V1.9 会话身份与商品引用绑定验证报告

实现提交：`73d20c7379754360417e0bb5ac1d4cf589651398`。本报告随独立验证提交保存。

## 结果与修改原因

V1.8 可以生成回复预览，但 `ProductId` 主要是当前截图旁路元数据，Session 没有可持久追踪的商品引用历史。
V1.9 已形成 **Buyer → Session → Message Timeline → Product Reference** 的外层关系：
Session 保存买家 OCR 显示名、创建/最近观察时间、当前可见商品 ID 及商品引用历史；
Context 保存同 Session 消息列表、引用列表和逐消息引用旁路关系。
无法识别当前商品时 `ProductId` 仍为空，历史引用不会被冒充为当前商品。

商品引用只表示“同帧可见”，**尚不能证明该买家消息确实在询问该商品**；
OCR 显示名与隔离测试 BuyerId 也不是已验证的平台身份。

## 文件及架构影响

新增：`Models/ProductReference.cs`、`Validation/ChangeDetectionChecks/BindingChecks.cs`、本报告。
修改：`Integration/AgentContextWire.cs`、`Models/MessageSession.cs`、`Program.cs`、`README.md`、
`Shadow/AgentPipelineTrace.cs`、`Shadow/QnocrShadowRunner.cs`、`Shadow/SessionContextBuilder.cs`、
`Shadow/SessionRegistry.cs`、`Validation/ChangeDetectionChecks/AgentPipelineChecks.cs`、`Program.cs`、
`ProviderLiveSmoke.cs`、`ShadowChecks.cs`。删除：无。

`MessageSession` 增加可选 `BuyerDisplayName`、`CreatedAt`、`LastActivityAt`、`ProductReferences`；
旧 Session JSON 可以读取，首次 V1.9 观察时填充此前不存在的时间字段。
`ProductReference` 仅含 ProductId、Source、CapturedAt、CaptureId 和未验证关联状态；
`SessionContextView` 另有 `MessageProductLinks`，不在 MessageEnvelope 增加字段。
原 qnbot 核心、Agent 生成/Prompt、Provider、Reply Preview 与发送逻辑均未修改。
原 qnbot 工作树保持干净，基线提交 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

新消息生命周期日志包含 `SESSION_CREATED` → `BUYER_BOUND` →
`PRODUCT_REFERENCE_BOUND`（当前商品有效时）→ `MESSAGE_ATTACHED`，
并以 CaptureId、SessionId、BuyerId、ProductId 关联。
AI OFF 时，新消息对应帧仍保存 `session-context_*.json`；
AI ON 时附加字段作为旁路 Context 传递，**现有模型问题文本不读取商品引用**。

## 验证

| 验证 | 结果 |
|---|---|
| .NET 8 主项目与验证项目 | Build Success；0 警告、0 错误 |
| .NET Framework 4.8 隔离宿主 | Build Success；全量编译保留原上游源码既有 CS4014 警告 |
| 累计检查 | 253/253，通过；高于要求的 241 项 |
| qnbot 原入口回归 | 17/17，通过，禁止业务调用 0 |
| 多买家 | Buyer A / B 的 SessionId、隔离 BuyerId、ProductReference 各自独立 |
| 商品变更 | A 可保留 A/C 引用历史，B 的商品 B 不串入 A；当前 OCR 失败时当前 ProductId 留空 |
| 持久化 | Session 创建时间与引用跨 Registry 重启保留；旧版 Session JSON 保持 SessionId 并安全迁移 |
| AI 合成回归 | 新 Context 含历史 ProductReference 1、当前 ProductId 空、消息链接 1；AI Calls 1、Reply Preview 1、SentMessages 0 |
| 知识边界 | 持久 Session 无 ProductName、Specifications 等详情字段；未新增商品库、FAQ 或卖点 |

真实千牛 **AI OFF / Send OFF** 单帧基线：`Output/Shadow/shadow_20260925_215157_722_06c59507634448b384559a6e5aa50c6a`。
1 帧成功、0 失败、历史消息未派发、AI Calls 0、SentMessages 0。
其独立状态 `Output/State/V19-final/sessions/A66A2D8E64F64A1A1A8990EEC5743AE21CFBCED6D2D2DB1734C574A4E259BC12/session.json`
记录当前 `ProductId=1051087533910`、同 ID 引用 1 条、来源 `Qianniu.ProductRegion.OCR`、实际 CaptureId 和捕获时间。
真实 OCR 显示名留在本地 Output，不提交到 Git。

合成 AI 回归证据：`Output/ValidationV18/provider-smoke_20260925_214733` 和
`Output/v19-provider-pipeline-smoke.log`。它只发送匿名合成问题到已配置端点，
用于证明扩展 Context 与原 qnbot AI 输入兼容；不把合成结果冒充新的真实买家消息。
自动检查日志：`Output/v19-checks.log`；原入口：`Output/v19-original-entry.log`；
真实只读基线：`Output/v19-live-final.log`。这些运行证据均被 Git 忽略。

## 限制、回滚与下一步

商品引用目前是 OCR 同帧可见证据，不是商品实体、规格信息或已确认的消息归属。
OCR 买家显示名可能波动，真实平台 BuyerId 尚未绑定。
老 Session 的 `CreatedAt` 无历史原始值时按首次 V1.9 观察时间初始化，不能倒推真实会话开始时刻。
本阶段没有商品知识库、FAQ、商品详情库或商品卖点。

回滚：停止本项目 Shadow，回滚实现提交并重建；保留 `Output/State/V19-final` 和运行日志供审计。
后续评估长时间运行稳定性、真实千牛发送，以及 V2 阶段知识库系统设计；本阶段不进入知识库建设。
