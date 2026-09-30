# V1.34.0-A Real Message Context Integration PoC 验证

日期：2026-09-30（Asia/Shanghai）

项目：qnbot-agent / qnocr-poc。基线：V1.33.0-C `76a4d6171daaa21c75b30d8da4dee00b0215ed5b`。

## 验收结论与边界

本轮由用户在千牛测试会话中**人工发送新消息**，通过真实窗口捕获、PaddleOCR 和原有 OcrMessageSource 生成 MessageEnvelope，进入独立 Agent Context 并保存到 SQLite。不是历史 JSON 回放替代实时采集。

最终受控样本：**3 条不同消息 → 3 个稳定 MessageId → 1 个 Observed Session → SQLite 3 行 Messages**。同一 Envelope 再次进入、组件重建后重放均未重复保存；独立进程恢复了原 SessionId、身份、消息、时间及生命周期历史。

因此，受控真实消息的 `MessageEnvelope → Stable Identity → AgentSessionManager → SQLite Session → Agent Context` 生产方向链路已成立。**这不是一般 OCR 波动下的幂等或生产上线验收**：第一轮实际暴露了昵称及正文识别变化，详见下文。没有接入运行中 qnbot 的内部 Session，没有增加 AI 客服能力。

## 修改文件

| 文件 | 改动 |
| --- | --- |
| `Extension/AgentContext/AgentContextPipeline.cs` | 新增 SQLite Context 宿主入口；直接复用原 Manager 默认稳定身份路径，支持既有 IMessageSource，返回已经保存的 AgentSession。 |
| `Extension/AgentContext/AgentContextCaptureCommand.cs` | 新增有界 Context-only 实时采集命令；复用现有窗口、布局、OCR、Structurer、OcrMessageSource；仅接受显式测试标记与人工确认的买家标签。 |
| `Program.cs` | 增加独立采集命令分支，直接进入 Context-only 路径。 |
| `Validation/RealMessageContextChecks.cs` | 新增 24 项集成检查、实时证据核对和独立恢复子进程。 |
| `Validation/ChangeDetectionChecks/Program.cs` | 增加集成/实时证据/恢复子进程入口，并将集成检查纳入全量回归。 |
| `Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj` | 链接新增验证文件。 |
| `docs/VALIDATION_V1340A.md` | 本报告。 |

未修改 qnbot Core、Models/MessageEnvelope.cs、Integration/MessageEnvelopeWire.cs、Integration/QnocrMessageAdapter.cs、Extension/QnbotAgentAdapter.cs、OcrMessageSource、Stable Identity Provider、Session Manager 或 SQLite Store。没有修改 AI、Intent、Knowledge 或发送逻辑。工作区原有两个 V1.32.2 未跟踪文档不纳入本次提交。

新宿主位于 Extension/AgentContext：Integration/**/*.cs 已被主工程排除，并由原 .NET Framework 4.8 兼容工程编译。将 .NET 8 Context 宿主放在这里，避免改变旧 Adapter 的编译边界。既有 Shadow/AgentPipelineTrace 与 SessionContextBuilder 属于 AI/业务观察流程；本轮只复用它们无业务依赖的底层采集工具，不创建第二个 AI Pipeline。

## 真实消息接入链路

```text
用户人工发送千牛测试消息
  → QianniuWindowCapture / NativeWorkerClient（PrintWindow，只读）
  → QianniuLayoutResolver / QianniuRegionExtractor（只对聊天区域 OCR）
  → NativeWorkerClient / 原 PaddleOCR worker
  → 原 BuyerMessageStructurer（完整买家气泡、原显示时间）
  → 本轮测试标记筛选 + 显式买家标签一致性检查
  → 原 OcrMessageSource
  → 原 Models.MessageEnvelope schema v1
  → AgentContextPipeline.ProcessMessage
  → 原 AgentSessionManager.ProcessMessage(envelope)
  → 原 AgentMessageIdentityProvider（版本化 SHA-256）
  → Session 归属与去重
  → 原 SQLiteAgentSessionStore.SaveSession（事务快照）
  → 返回原 AgentSession，作为已持久化的 Agent Context
```

持久化在 Context 返回之前完成。没有调用旧 ShadowRunner、DurableShadowDispatch、qnbot 业务入口、AI Provider、Intent Classifier、Knowledge Provider、Reply 生成或发送功能。采集子进程仅执行原 `--capture-worker` 和 `--ocr-worker`。

SellerId 来自接待中心窗口标题的确定性观察标签；BuyerId 来自用户确认并与 OCR 头部精确匹配的买家标签。二者采用 `observed:seller/ buyer:<SHA256>`，不包含 HWND、采集时间或随机数，身份等级始终为 Observed。它们不是原生平台 ID，也没有增加身份信任等级。

## 编译

SDK：仓库 `.tools/dotnet/dotnet.exe`，8.0.425。CLI_HOME 指向仓库 `.tools/cli-home`，没有安装新工具或新增包依赖。

| 命令 | 结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -t:Rebuild -v q` | **0 Warning / 0 Error** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |
| `dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |

日志：`Output/ValidationV1340A/build-main.log`、`build-checks.log`、`build-runtime-observer.log`。

原 .NET Framework 4.8 QnbotCompatibility 宿主编译仍有 **1 条既有 CS4014 Warning / 0 Error**，位于禁止修改的上游 BuyerMessageBurstCoordinator.cs:744；未修改、屏蔽该警告。不能把所有旧兼容宿主编译描述为零警告。本轮新增和修改的 .NET 8 工程均零警告。

## 最终受控真实消息测试

用户确认测试买家标签，并人工发送了第二轮三条新消息。命令：

```powershell
& ./bin/Debug/net8.0-windows/win-x64/qnocr-poc.exe --agent-context-capture --database Output/ValidationV1340A/live-final-sessions.db --output Output/ValidationV1340A/live-02 --hwnd 190DC8 --prefix 1340940 --buyer-label <人工确认的OCR买家标签> --frames 100 --interval-ms 1500
```

实际起止：**16:54:57–16:57:07（Asia/Shanghai）**；达到三条样本后写入该输出目录的 `stop.request` 正常退出。最终完成 63 帧，失败 0 帧，聊天 OCR 5 次，输出 3 个逐步重叠的 Envelope（1、2、3 条），不是 6 条独立新消息。Frame 26 的买家头部与确认标签不一致，Held，未进入 Context。

| OCR 原始正文 | 千牛显示时间 | 首次接受帧 | Stable MessageId |
| --- | --- | --- | --- |
| `134094001规格测试` | 2026-09-30 16:54:43 | 0021 | `agent:v1:9a87db1462f5e2678bad9b42d767c3604ebb1bd06cd6a4287275df1939f42d4e` |
| `134094002容量测试` | 2026-09-30 16:54:53 | 0029 | `agent:v1:7118de4853982b4e31999ff8905a8122f670ce569c120e5260c2af2956910396` |
| `134094003发货测试` | 2026-09-30 16:55:01 | 0030 | `agent:v1:ac8d4d20952b156b2ecd8a23bf45b36a252d5e94f881dac5d143f3426bdf293b` |

Timestamp.Kind 均为 Unspecified，原显示 ticks/Kind 保留；CreatedTime/LastActiveTime 为 UTC 接收时间。截图中的空格可能未被 OCR 保留，上表记录实际 Envelope 正文，未为测试修改或归一化正文。

SessionId：`80570fc6c89942ddaacce84113a931bf`。消息数顺序 **1 → 2 → 3**；所有消息的 Source、SessionId、Role、原文和时间均与 SQLite/Context 对应。

主链证据保存在 Git 忽略的 `Output/ValidationV1340A/live-02/`：

- `session.json` / `summary.json`：命令、观察范围、时间和总数。
- `frames/0021/envelope_0001.json`、`frames/0029/envelope_0002.json`、`frames/0030/envelope_0003.json`：真实 Envelope。
- 相邻 `context_*.json`：保存成功后返回的 Context。
- 每个相关帧的 `capture.json`、完整 PNG、`chat_region.png`、OCR worker JSON、`buyer-messages.json`、`identity.json`：采集至消息和身份的来源证据。
- `frames/0030/chat_region.png`：人工目视核对三条完整买家消息；卖家侧内容没有进入 Envelope。
- `expected-recovery.json` / `expected-recovery.json.child.log`：恢复快照与独立进程结果。

截图、真实昵称、窗口标题及数据库只保留在本地忽略目录，没有提交到 Git。

## Stable Identity 与重复消息测试

没有更换原身份算法，也没有用测试序号伪造 Source MessageId。测试标记只筛选输入、审计 OCR 修订；真实 MessageId 仍由原 Provider 对完整观察身份、Role、Timestamp ticks/Kind、原始正文计算。

每个实际 Envelope 保存后，先写入证据 JSON，再反序列化为新 DTO，重新进入同一个默认 ProcessMessage 入口：返回同一不可变 Context 实例，消息数量不增长。共 3 次即时重复检查通过。

实时证据验证重新创建 Provider/DTO，逐条核对相同 MessageId；3 条不同消息有 3 个不同 ID。独立只读 SQL `SELECT COUNT(*) FROM Messages` 返回 **3**，与 Context 消息总数一致，没有额外行。重叠 Envelope 中的已有消息没有追加第二份。

合成集成检查额外给 SQLite Sessions UPDATE 安装拒绝写入的触发器，再提交重复 Envelope：操作仍成功，证明纯重复没有调用 SaveSession；检查后移除触发器。该故障注入只使用单独的合成测试数据库。

## Session 验证与多 Session 测试

真实最终样本：创建 1 个 Session、追加至 3 条；匹配失败的买家观察不创建 Session。没有身份串入或重启后新建第二个 Session。

新增集成检查通过同一个 Context pipeline 接收相同消息、分别改变 BuyerId / SellerId / Source，创建三个独立 Session；原 Session 保持 3 条，其他 Session 各 2 条，恢复后隔离保持。

**多 Session 变化使用合成身份夹具**，没有把它描述为两位真实买家的实时验收。本轮人工实时测试只覆盖一个可控买家。

## SQLite 持久化与恢复

数据库：`Output/ValidationV1340A/live-final-sessions.db`。使用原 schema v1、同一绝对文件路径和默认 30 分钟 idle timeout。

1. 采集进程结束并释放 pipeline / Manager。
2. 验证进程新建 SQLite Store 与 Context Pipeline。
3. 精确核对原 SessionId、Source/SellerId/BuyerId、Observed 等级、Active 状态、CreatedTime、LastActiveTime、消息顺序、ID、正文、Timestamp ticks/Kind、状态历史。
4. 重放全部真实 Envelope：消息数、SessionId 和 LastActiveTime 均不变化。
5. 退出该 Pipeline 后，启动独立验证子进程，再次恢复完整快照。

独立进程结果：`RESTORED: sessions=1; messages=3; AI/Intent/Knowledge/Reply/SentMessages=0`，退出码 0。

合成集成检查还验证恢复后新消息追加到原 Session，以及该追加可被独立进程恢复。既有 91 项持久化检查继续覆盖生命周期、容量、事务失败和恢复行为。

实时证据检查应在默认 idle timeout 内运行；超时之后组件恢复会按原生命周期进入 Inactive，不应把这种预期行为解释为数据丢失。

## 第一轮真实输入发现与处理

第一轮 `live-01`（16:47:04–16:51:09）采集了用户实际发送的带问号消息。结果是 **2 个观察 Session / 5 条记录**，不作为最终通过样本，也未删除证据。

- 单帧买家标签被识别为另一个字符串，后续多帧读到一致的标签，原按标签精确隔离导致两个 Session。
- 同一气泡的正文在空格、全角/半角问号上出现变化。Provider 忠实区分这些不同字段，形成额外 MessageId。
- 当时使用的采集启动时间过滤还排除了第一条：千牛显示时间 16:46:53，早于本机启动时间 16:47:04。显示/服务器时间与本机时间不能作为相同时间线假定。

最终入口增加人工确认的买家标签精确约束：不匹配时 Held，不猜测或合并账号。移除按本机启动时间裁剪消息；通过每轮唯一测试标记限定输入，避免以墙钟替换原消息时间。使用另一个测试标记与新数据库重新进行实时验收，正文仍完全保留原 OCR 结果。

最后的 26 项实时证据检查包含审计：同身份、Role、显示 ticks/Kind、数字测试标记所代表的受控消息数必须等于不同 MessageId 数，防止 OCR 修订生成额外 ID 后仍把结果报告为通过。**这个标记审计没有被用来去重或改写生产消息身份。**

## 安全检查

| 本轮 Context-only 路径 | 结果 |
| --- | --- |
| AI Provider 调用 | **0** |
| Intent 调用 | **0** |
| Knowledge 调用 | **0** |
| Reply 生成调用 | **0** |
| SentMessages | **0** |
| SendEnabled | **false** |

这些是本轮路径无业务调用能力的结构性结果及回执字段，不是伪造的业务 Provider 遥测计数。完整历史回归仍执行已有合成 AI/Intent/Knowledge mock 检查；未运行任何 provider-live-smoke。用户人工发送和千牛窗口里已有的卖家侧内容不计为本采集端发送。

命令拒绝缺少参数、重复参数、无效帧数/间隔、既有输出目录和不可见窗口。每帧核对相同 HWND、PID、窗口标题及可见性；窗口变化终止，不自动切换店铺。采集/ OCR 使用既有有超时的独立 worker，只停止自己创建的 worker，不控制千牛或发送消息。没有修改 qnbot 运行文件或源码。

## 测试与回归

| 命令 | 结果 |
| --- | --- |
| `ChangeDetectionChecks.exe --real-message-context-only` | **24/24**（合成 Context 集成检查） |
| `ChangeDetectionChecks.exe --real-message-context-evidence Output/ValidationV1340A/live-02` | **26/26**（本轮新实时消息证据） |
| 完整 `ChangeDetectionChecks.exe` | **834/834**，原 810 项全部保留并通过，增加 24 项 |
| `Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot` | **17/17**，ForbiddenBusinessCalls=0，QnbotCoreSourceUnmodified=true，6 个上游源文件字节一致 |
| `git diff --check` | 通过 |

SHA-256 证据清单：`Output/ValidationV1340A/evidence-hashes.json`（Envelope、Context、聊天 PNG/OCR、恢复快照与最终数据库）。

日志：`Output/ValidationV1340A/context-checks.log`、`live-evidence.log`、`regression.log`、`compatibility.log`、`live-01.log`、`live-02.log`。兼容原始产物：`Output/Integration/run_20260930_165706_458/`。

## 当前限制

- 精确 Stable Identity 对相同 Envelope 稳定，**没有消除 OCR 昵称/正文/时间识别波动**。第一轮已经实证该限制；受控短文本通过不等于任意真实聊天跨帧幂等。
- 身份依赖人工确认的 OCR 标签与窗口标题；仍为 Observed。改昵称、切客服子账号或窗口标题变化可能改变观察身份，不具备原生账号/会话证明。
- 新命令只允许一个明确测试标签与每轮唯一标记，属于只读受控验收入口，不是通用生产消息订阅器。不能复用旧测试标记来证明“本轮新消息”。
- 本轮只有一个真实买家；多店、多买家实时切换未验收。隔离逻辑由合成集成和原回归覆盖。
- OCR 每个气泡必须完整可见并通过原 Structurer 校验；气泡被遮挡、裁切或低置信度会被 Held/Review，不能保证消息无遗漏。
- 复用原 SQLite 的单 owning Manager、有界快照、Session 内去重和生命周期规则；没有多进程协同写入、跨 Session 永久去重、schema 迁移或备份机制。
- 宿主读取持久化组件失败时直接报错；没有新增持久化传输队列、业务重试、自动恢复调度或生产背压流程。
- 所有采集已停止；没有自动回复、自动发送、AI、Intent、Knowledge 或 Reply 功能。

## 下一步建议

先取得可信的原生 Seller/Buyer/Conversation 身份和原生 MessageId，明确如何在保持原 Envelope/Adapter 的边界下以旁路证据交给 Agent。随后单独处理 OCR 修订、身份漂移、同秒同文消息的识别及持久化背压，并用两位受控买家和不同店铺进行实时隔离验收。

在这些输入完整性问题有明确方案之前，继续保持 Context-only Shadow；AI/Intent/Knowledge/Reply/发送应作为另一个明确授权的阶段。