# V1.34.0-C 真实单买家 Context 验收

日期：2026-10-03（Asia/Shanghai）。基线：f9af7df8ed2166d3906ccc999efbf4576833bb21。

## 判定与下一阶段

**单买家真实验收通过 / 多买家真实验收待补。**

一个受控真实买家完成连续两条消息、OCR 身份稳定、原 Envelope 重放、SQLite 持久化、组件重建、恢复去重及恢复后第三条真实消息追加。独立真实证据审计 **40/40**；全量回归 **960/960**。最终为 **1 Session / 3 Message**，身份保持 Observed。

用户已明确：多买家真实 A/B 隔离保持 Pending，不阻塞后续 Agent Context → JEV Intent 集成评审。V1.34.0-B 已完成的合成多买家隔离结果保留为代码级证据，不能替代真实双买家验收。本任务没有执行 JEV、AI、Knowledge、Reply 或发送。

## 真实消息与归属

使用脱敏标签 Buyer A。显示时间按原 OCR Envelope 保留，Timestamp.Kind 为 Unspecified；Context 活动时间是接收/处理时间，独立于显示时间。

| 输入 | 原 Envelope 正文 | 显示时间 | 结果 |
| --- | --- | --- | --- |
| A1 | 1340220601规格测试 | 2026-10-03 22:06:45 | 原 Session |
| A2 | 13402206020容量测试 | 2026-10-03 22:06:58 | 同一 Session；原始实发标记确实多一个 0 |
| 恢复后新消息 | 1340220604恢复测试 | 2026-10-03 22:23:27 | 同一 Session；消息数从 2 增至 3 |

SessionId：30a4ed903c3d4ab3b9a02ade7eede605。

三个 Stable MessageId：

- A1：agent:v1:1a39aaa3f9faf645fcb22853d0dab811a1cdb523c84288ec5efe449f1ba960cd
- A2：agent:v1:fc68befebe4c46a2c283199546f2c7d63b07ea07c5094bc923467cff2edcd532
- 恢复消息：agent:v1:5bf785bdf0edabea38c2c636e0dfe164c9bdf2a2a233a7cbb9612bc1e9b0ebe8

三条消息均由实际千牛截图/OCR 经原 Structurer 和 OcrMessageSource 生成；没有用合成 Envelope 补充真实计数，没有改写文本、时间或身份算法。

## 链路检查

| 检查 | 实际结果 |
| --- | --- |
| 连续消息 | A1/A2 在同一真实 OCR 批次进入同一 Session；两条不同 MessageId |
| OCR 身份稳定 | 本轮有效收据的原始昵称形式 1 种；Normalized 标签、Observed Buyer Hash、Source/Seller 一致 |
| 自然昵称波动 | 未自然观察到；另做 4 种明确标注的受控输入形式回放，其中 ASCII 样本的 NFKC/NFKD 原值相同 |
| Stable Identity | 原 Provider 对每条原 Envelope 重新计算，均与 Context / SQLite MessageId 相符 |
| 原 Envelope 重放 | 新 DTO 重新进入 Context；消息数、MessageId、创建时间、LastActiveTime、状态和状态历史完全不变 |
| SQLite | schema v1；1 Session / 3 Message；IdentityLevel=0；完整性检查 ok，外键检查无错误 |
| 组件重建 | Dispose 后重开相同测试 SQLite；2 条消息、原 SessionId、Identity、活动时间、状态历史完整恢复 |
| 恢复去重 | 两条旧消息恢复后重放，消息数仍为 2，不刷新 LastActiveTime |
| 恢复后追加 | 追加第三条真实消息到原 Session；总数 3，无新 Session |
| 创建/活动时间 | CreatedTime 保持 22:16:42.6620522；重放保持 LastActiveTime；实际追加后 LastActiveTime 为 22:31:49.2484152（均为 Asia/Shanghai 展示） |
| Observed | 始终 Observed；未升级 Verified；未填充 Intent/Product 业务状态 |
| AI / Intent / Knowledge / Reply / SentMessages | Context 验收路径 0 / 0 / 0 / 0 / 0 |

零计数依据为此路径没有业务服务依赖或分发/发送入口，计数字段不是 Provider 级遥测，也不表示千牛中其他会话的人工操作计数。原回归中的业务 mock 不属于真实服务调用。

## 原始尝试与恢复过程

所有原始尝试保留，未将失败或未完成结果隐藏为 Passed：

1. live-single-20261003-220650：第一条进入 Context / SQLite；界面切到普通客户会话，测试第二条未继续采集，阶段超时。原结果 Incomplete，1 Session / 1 Message；普通客户消息未进入该测试库。
2. retry-02：第二条实发标记多一个 0，截图与 OCR 相符。原计划拒绝该批次，结果 Failed（验收计划不匹配），SQLite 0 / 0。保存原 Envelope。
3. retry-03：按实发文本计划采集 A1/A2，完成重放、别名回放与组件重建。原 4 分钟恢复消息窗口到期，原结果 Incomplete，1 / 2。
4. retry-04：随后截图包含第三条真实恢复消息；该新库的初始阶段因包含后续阶段消息被拒绝，原结果 Failed，0 / 0。其原始截图、OCR 和完整 Envelope 保留。

最终闭环使用 retry-03 的原 SQLite。第三条实发时间晚于首次组件重建；因采集窗口到期，使用 retry-04 保存的真实 OCR Envelope 恢复原组件并追加。22:31:49 再次重开原库，用实际墙钟确认完整状态恢复、重放去重，然后处理原始三条消息重叠批次，只追加第三条。

原 retry-03 的 Incomplete summary、收据、Hash 清单和两消息数据库复制为 pre-resume-* 并保留；旧数据库副本与旧清单 Hash 相符。完整三消息库、原两消息副本、恢复记录和实际截图均可复核。报告不宣称第三条在原连续采集进程中即时入库。

单买家等待窗口改为 15 分钟，多买家仍为 4 分钟；业务 30 分钟 idle timeout、schema 和生命周期规则不变。恢复入口受 EvidenceKind、原数据库 Hash、原快照、真实 OCR/Adapter 等值及原 SessionId 约束，不强行复活过期 Session。

## 本地证据

最终目录：Output/ValidationV1340C/live-single-20261003-220650-retry-03/。

主要文件：

- sessions.db、A1A2.json、A4.json
- before-restart.json、after-replay.json、controlled-aliases.json、after-alias-replay.json、after-restart.json
- after-resume-restore.json、resume.json、receipts.json、summary.json
- pre-resume-summary.json、pre-resume-receipts.json、pre-resume-evidence-hashes.json、pre-resume-sessions.db
- frames/0001/、frames/0002/、frames/resume-0001/ 等原始采集/OCR/Envelope/Context
- evidence-hashes.json

最终 sessions.db SHA-256：

EA72E3795D86ED435139BF7341733A97B6F269AF9458643826BD9E02C3312427

审计日志：Output/ValidationV1340C/single-live-evidence.log。恢复日志：single-resume.log。其余编译、回归及兼容日志均位于同一 V1340C 根目录。私密原值/截图保留在 Git 忽略目录，不提交到仓库。

## 修改范围与限制

业务代码修改数为 0。修改仅在验证宿主、测试入口、启动脚本及文档。原 qnbot Core、MessageEnvelope、Adapter、Identity Normalizer、Stable Identity Provider、Session Manager、SQLite Store 与 schema 均未修改。验证提交可独立回滚。

当前只证明单买家真实链路及保存的真实输入在恢复后追加。真实 A/B 隔离、A→B→A、多店铺、多进程写入、生产容量和可信平台 Buyer ID 均不在本轮证明范围。观察昵称仍不能提升 Verified。下一步允许开展 Agent Context → JEV Intent 的正式集成评审，同时保留真实双买家补验项。
