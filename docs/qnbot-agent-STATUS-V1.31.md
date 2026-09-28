# qnbot-agent 项目状态快照（V1.31）

快照日期：2026-09-28。文档编写前，qnocr-poc HEAD 为 ba7f6ddb166cb0179c6b82f2857a77f513e6430f，工作区干净；只读参照的 qnbot 仓库 HEAD 为 4802807f26a6abf4cfb4457d5b158b2f51f4ff18，工作区干净。下文的“最新 Commit”指该代码基线；本次文档提交另见 Git 历史。

## 项目目标与当前定位

目标是在 qnbot 桌面客户端基础上提供 AI 客服辅助能力。现有实现主要位于独立 Windows/.NET 8 项目 qnocr-poc：千牛视觉/OCR 输入、Session、Intent、Knowledge、AI Shadow 预览、人工编辑/复制/反馈；另有本地 Agent Console 与 Knowledge Console。qnbot 是 .NET Framework 4.8 桌面客户端。当前定位是**客服副驾驶**：人查看、编辑并自行在千牛发送；程序不自动发送。

**当前版本 V1.31：集成设计完成，集成尚未发生。** [接口 PoC](../Extension/IAgentExtension.cs) 与 [只读适配 PoC](../Extension/QnbotAgentAdapter.cs) 可编译、可离线测试，但没有订阅运行中 qnbot 事件、没有 IPC 或 qnbot 桌面 Agent 面板。独立 Web Console 是当前人工入口，不等于最终桌面入口。

## V1.0—V1.31 已完成内容与证据边界

| 阶段 | 当前代码/验证记录所证明的结果 |
| --- | --- |
| [V1.0](../VALIDATION_V10.md) | OCR Envelope 到 qnbot 原类型/聚合器的 .NET 4.8 离线兼容性，未接生产 Bot。 |
| [V1.1](../VALIDATION_V11.md) | 真实千牛 OCR → Envelope → 隔离 Adapter → Shadow 日志。 |
| [V1.2](../VALIDATION_V12.md) | 真实新买家消息增量捕获与去重验收。 |
| [V1.3](../VALIDATION_V13_ACCEPTANCE.md) | 真实买家问题的 AI Shadow 预览链路；首轮有漏触发，原始 10/10 验收未通过。 |
| [V1.4](../VALIDATION_V14.md) | 外层去重、Session、生命周期日志与异常恢复。 |
| [V1.5](../VALIDATION_V15.md) | Session Context、Pipeline Trace、Reply Preview 基础设施；报告明确真实 10/10 预览验收未完成。 |
| [V1.6](../VALIDATION_V16.md) | 1 条真实千牛新消息进入原 qnbot 隔离 AI 链并生成 1 份预览；发送 0。 |
| [V1.7](../VALIDATION_V17.md) | 默认关闭的人工确认状态与隔离发送适配测试；没有真实千牛发送验收。 |
| [V1.8](../VALIDATION_V18.md) | 隔离宿主中的可配置 AI Provider 与模型，密钥不写入配置或日志。 |
| [V1.9](../VALIDATION_V19.md) | Buyer、Session、消息时间线和商品引用的外层绑定。 |
| [V1.10](../VALIDATION_V110.md) | 本机 AI OFF/Send OFF 视觉输入连续约 2 小时观察与重启恢复；长跑无新消息，不证明高负载或持续 AI 稳定性。 |
| [V1.11-A](../VALIDATION_V111A.md) | BuyerIdentity 状态与发送前置拒绝；真实 OCR Buyer 尚未 Verified。[V1.11-B](../BUYER_IDENTITY_SOURCE_ANALYSIS.md)/[C](../BUYER_IDENTITY_RUNTIME_V111C.md) 仍未取得同会话可信身份快照。 |
| [V1.12](../VALIDATION_V112.md) | 本地回复预览、人工编辑、审核、复制辅助。 |
| [V1.13](../VALIDATION_V113.md) | 辅助运行统计、人工反馈与 Session 观察。 |
| [V1.14](../VALIDATION_V114.md) | Evaluation Batch；真实测试买家 3 条消息、3 条建议，人工反馈 Accepted 2/Edited 1，发送 0。 |
| [V1.15](../VALIDATION_V115.md) | Intent 接口与规则 Decision Layer；此版尚未接 JEV。 |
| [V1.16](../VALIDATION_V116.md) | Knowledge Provider 请求/结果与空 Provider，只读知识接口。 |
| [V1.17](../VALIDATION_V117.md) | 显式启用的一条合成测试商品事实进入 AI Shadow 上下文。 |
| [V1.18](../VALIDATION_V118.md) | 人工反馈按规则路由到 Knowledge/Prompt/Strategy/Decision 调查方向，不自动优化。 |
| [V1.19](../VALIDATION_V119.md) | 按 SellerId + ProductId 隔离的多商品 Repository，数据仍是合成测试记录。 |
| [V1.20](../VALIDATION_V120.md) | ProductKnowledgeProfile Schema；旧 Fields 兼容，FAQ 等分区尚不自动进入 AI。 |
| [V1.21](../VALIDATION_V121.md) | Draft → 人工审核 → Published；Provider 仅读已发布且允许 AI 使用的条目。 |
| [V1.22](../VALIDATION_V122.md) | 结构化测试资料生成 Knowledge Draft，仍需人工审核/发布。 |
| [V1.23](../VALIDATION_V123.md) | JSON 测试导入、字段修订、版本保留及并行冲突拦截。 |
| [V1.24](../VALIDATION_V124_KNOWLEDGE_CONSOLE.md) | 本地 Knowledge Console MVP：测试商品 Draft、编辑、审核、显式发布与修订；**没有完成真实商品迁移验收**。 |
| [V1.25](../VALIDATION_V125.md) | Knowledge Source 接口与 LocalFile 适配；Feishu/Database/Obsidian 仅预留类型，未连接。 |
| [V1.26](../VALIDATION_V126.md) | JEV 与规则分类旁路对比 PoC，未替换当时的默认分类。 |
| [V1.27](../VALIDATION_V127.md) | Shadow CLI 配置默认 JEV、缺 Key/调用失败回退 RuleBased；直接嵌入 Runner 的旧调用仍可用规则实现。 |
| [V1.28](../VALIDATION_V128.md) | 真实测试买家 10 条：OCR、单 Session、JEV Intent、Knowledge 状态、AI 预览均记录 10/10；商品 ID 为空，未证明商品知识命中。 |
| [V1.29](../VALIDATION_V129.md) | 当前商品上下文的人工/OCR 绑定与缺失/冲突阻断；知识命中仅用隔离测试商品验证，真实商品未绑定。 |
| [V1.30](../VALIDATION_V130.md) | 独立本地 Agent Console：会话、消息、Intent、商品、知识、预览、编辑/复制/反馈；无发送路由。 |
| [V1.31](../VALIDATION_V131.md) | qnbot 集成点分析、Extension 接口和只读 Adapter 离线 PoC；主检查 586/586，原入口离线回归 17/17。 |

## 当前架构状态

现行已运行/可测试的路径是：千牛视觉/OCR → MessageEnvelope → Shadow Session/Decision/Knowledge → 隔离 qnbot AI 链 → Reply Preview → 人工 Console。JEV 在配置/密钥可用时为 Shadow CLI 的默认 Intent Provider，失败降级 RuleBased。商品知识默认可为空；只有明确启用的本地测试资料才进入知识上下文。Knowledge Console 与 Agent Console 是单独的本机 Web 项目。详细边界见 [ARCHITECTURE](qnbot-agent-ARCHITECTURE.md)。

## 当前阶段、下一步与限制

V1.31 已提交；编写本快照前无进行中的代码改动。下一步按 [ROADMAP](qnbot-agent-ROADMAP.md) 评审 V1.32 小范围只读接入：先解决 qnbot 运行时身份来源、消息事件过滤、进程边界和退出清理，再考虑桌面 UI。该计划尚未实现。

当前限制和已知风险：真实买家身份仍为 Observed/Mapped/Test，不能证明 Verified；自动发送保持关闭。qnbot 原生事件在常规安全处理前触发，直接订阅不能继承安全结论。V1.31 去重只在 PoC 进程内存有效，尚无持久 IPC/重连设计。OCR 可因布局/显示变化漏读；真实商品缺乏经核对的 SellerId/ProductId/事实来源；合成知识不能用于真实答复。AI 预览须人工核实，2 小时长跑不能推广为持续 AI/多店铺生产稳定性结论。
