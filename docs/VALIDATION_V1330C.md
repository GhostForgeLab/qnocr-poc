# V1.33.0-C Agent Session Persistence & Stable Identity 验证

日期：2026-09-30（Asia/Shanghai）

项目：qnbot-agent / qnocr-poc

前置提交：V1.33.0-A `1cd601f4205eaa77232ce3b74b3b3cfb3f43f60d`；V1.33.0-B `d6b52478c9b4b99655e1f7c1ee8e60a5224d892b`。

## 结果与范围

建立 `MessageEnvelope → Stable Identity → AgentSessionManager → SQLite Persistence → Agent Context`。

默认消息入口生成确定性 MessageId；注入同一个 SQLite 文件后，重新创建 Store 和 Manager 能恢复 SessionId、观察身份、状态、消息顺序、状态转换历史、CreatedTime、LastActiveTime 和去重索引。独立验证子进程成功恢复、去重并追加消息，其退出后追加仍可读取。

未修改 MessageEnvelope、Adapter、Integration 或 qnbot Core；未使用 qnbot 内部 Session。没有接入真实业务消息流、AI、Intent、Knowledge、Reply 或自动发送。默认 Store 仍为 InMemoryAgentSessionStore，宿主需要显式选择 SQLite 文件。

## 修改文件列表

| 文件 | 改动 |
| --- | --- |
| `Extension/AgentContext/IAgentMessageIdentityProvider.cs` | 新增独立消息身份解析接口。 |
| `Extension/AgentContext/AgentMessageIdentityProvider.cs` | 新增三优先级、版本化确定性身份生成。 |
| `Extension/AgentContext/SQLiteAgentSessionStore.cs` | 新增 SQLite 三表、事务快照、枚举、查询、级联删除。 |
| `Extension/AgentContext/AgentSessionPersistenceModel.cs` | 新增 schema 版本、快照验证及只读集合复制。 |
| `Extension/AgentContext/AgentSessionManager.cs` | 默认稳定身份、Store 启动恢复、去重及生命周期恢复。 |
| `Extension/AgentContext/IAgentSessionStore.cs` | 增加 LoadSessions；明确独占 Manager 及完整一致快照契约。 |
| `Extension/AgentContext/InMemoryAgentSessionStore.cs` | 实现线程安全 LoadSessions，可由新 Manager 恢复。 |
| `Extension/AgentContext/IAgentSessionManager.cs` | 更新默认入口的稳定 ID 文档。 |
| `Extension/AgentContext/AgentSessionMessage.cs` | 更新 MessageId 契约说明。 |
| `Validation/AgentSessionPersistenceChecks.cs` | 新增 91 项身份、SQLite、重启、故障与恢复检查。 |
| `Validation/ChangeDetectionChecks/AgentSessionChecks.cs` | 将 GUID 断言改为稳定 ID；时钟和并发追加夹具使用不同消息，保留原 38 项检查。 |
| `Validation/ChangeDetectionChecks/AgentSessionHardeningChecks.cs` | 故障 Store 实现 LoadSessions；原 79 项断言保持不变。 |
| `Validation/ChangeDetectionChecks/Program.cs` | 新增 persistence-only / persistence-child 入口并纳入全量回归。 |
| `Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj` | 链接上级目录中的持久化验证文件。 |
| `qnocr-poc.csproj` | 引入 Microsoft.Data.Sqlite 8.0.28。 |
| `packages.lock.json` | 锁定 SQLite 托管/原生依赖；已有依赖版本保持不变。 |
| `docs/VALIDATION_V1330C.md` | 本轮设计、命令、结果、限制和下一步。 |

工作区已有的两个 V1.32.2 架构/可行性文档未纳入本次提交。

## Stable Identity 设计

`IAgentMessageIdentityProvider.GetMessageId(SessionIdentity, EnvelopeMessage, sourceMessageId?, fallbackIdentity?)` 保持独立于输入 DTO；Source ID 和 fallback key 通过 Agent 接口传入，不扩展 MessageEnvelope。

优先级：

1. 已有非空 Source MessageId：原样返回，区分大小写，不修剪。去重范围继续为单个 Session；重复 ID 保留第一次消息内容。
2. 消息有 Timestamp：对版本、Source、SellerId、BuyerId、Role、Timestamp.Ticks、Timestamp.Kind、完整正文进行 SHA-256 指纹计算。
3. 无 Timestamp：对观察身份、Role、fallback 域和稳定 fallback key 生成指纹；未提供 key 时使用完整正文。

生成格式为 `agent:v1:` 加完整 64 位小写十六进制 SHA-256。字符串采用长度前缀和精确 UTF-16 code unit 编码，避免分隔符拼接碰撞、NUL 歧义或不完整 UTF-16 被 UTF-8 替换造成的哈希碰撞。Ticks 和 Kind 保持原始值，不推测显示时间所属时区。产品 ID、到达时间、批次位置、SessionId、随机数均不参与，避免产品切换、重试、分批和重启改变消息身份。

默认 `ProcessMessage(envelope)` 使用该 provider；原 `ProcessMessage(envelope, messageIds)` 的精确 ID 语义保持不变，已有平台 ID 可直接通过此重载提交。混合平台 ID 和指纹 ID 可先逐条调用 provider，再提交 ID 列表。可通过构造参数注入替代 provider，仍须返回每条消息的非空 ID。

相同输入在 100 次调用及新 provider 实例中一致；固定 v1 golden vector 锁定身份算法格式，避免未来改动无意改变已持久化消息 ID。纯重复不写 Store、不刷新 LastActiveTime；批内重复和混合批次保持首次接受顺序。

Fallback 解析层支持缺少时间的外部观察，但 Manager 继续要求完整非默认 Timestamp、Role、Text。这保留原消息完整性校验；需要接受不完整消息时必须另立阶段修改契约。

## SQLite Store 设计

唯一存储抽象继续为 IAgentSessionStore：

```csharp
void SaveSession(AgentSession session);
AgentSession? GetSession(string sessionId);
bool DeleteSession(string sessionId);
IReadOnlyList<AgentSession> LoadSessions();
```

InMemory 和 SQLite 均实现这一接口；Manager 不进行 SQLite 类型判断，未来 CloudStore 可沿用相同恢复入口。自定义 Store 必须补充一致的 LoadSessions 实现。

| 表 | 持久化字段与约束 |
| --- | --- |
| Sessions | SessionId、Source、SellerId、BuyerId、IdentityLevel、State、CreatedTime、LastActiveTime。SessionId 主键；Source/SellerId/BuyerId 使用 BINARY 精确比较；同一观察身份最多一个 Active。 |
| Messages | MessageId、SessionId、Ordinal、Timestamp ticks、TimestampKind、Content、Source、Role。`(SessionId, Ordinal)` 主键；`(SessionId, MessageId)` 唯一。 |
| StateHistory | SessionId、Ordinal、OldState（创建记录为 null）、NewState、Reason、Timestamp。`(SessionId, Ordinal)` 主键。 |

Ordinal 保存接受顺序，允许消息时间乱序；DateTime 的 ticks / Kind 分列存储并原样重建。UTC 接收及状态时间采用 invariant round-trip `O` 文本。IdentityLevel 只允许 Observed，不通过数据库恢复提高信任等级。

SaveSession 先验证并复制完整快照，在一个事务内 upsert 元信息、替换 Messages / StateHistory，全部成功才 commit。任意插入失败，元信息、清空操作及已经插入的行全部回滚；Manager 仅在保存成功后提交内存及去重 ID。所有数据值使用 SQL 参数。

GetSession / LoadSessions 在同一读事务中读取完整关系快照，返回只读集合。DeleteSession 使用事务和外键 ON DELETE CASCADE 删除对应消息和状态历史，重复删除返回 false。默认开启 WAL、外键检查，连接不使用池；每次操作创建和释放连接，避免测试或宿主退出后文件仍被连接池占用。单个 Store 自带实例锁；SQLite 负责数据库事务锁和 5 秒命令超时。

数据库使用 `PRAGMA user_version=1`。空文件初始化 schema；未知版本或已有无版本业务表的无关数据库拒绝初始化。损坏、不完整、身份级别异常或生命周期链无效的快照拒绝恢复，不悄悄删除或伪造历史。

依赖及 API 参考：[Microsoft.Data.Sqlite 8.0.28](https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.28)、[官方事务文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)、[官方连接字符串文档](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)。

## 恢复和生命周期

启动时先 LoadSessions，验证所有快照及配置容量，再构建 SessionId 索引、Active 身份映射及已接受 MessageId 集合。重复 SessionId / Active 身份拒绝启动。恢复消息与 Session 不共享可变集合。

旧进程的单调时间戳不能持久化后直接用于新进程：

- 启动时使用 `max(0, utcNow - LastActiveTime)` 计算停机累计空闲时间。
- 记录新进程的单调时间基点；后续空闲时间 = 启动累计空闲时间 + 新进程单调 elapsed。
- 新消息成功保存后，累计恢复空闲时间清零，以新接收时间重新计时；重复消息不清零。
- 构造时执行 sweep，停机已达到 idle / retention 边界的 Session 立即进入 Inactive / Archived，并写回 SQLite；跳过两个边界时记录两个转换。
- Archived 保留历史，不复活；Inactive 的新输入创建新 SessionId，不借用旧历史。恢复淘汰按真实累计空闲年龄排序。
- 超过当前容量设置时，启动直接抛 AgentSessionCapacityException，保留数据库原始证据，不自动截断或删除。

单次 Store 操作具有事务原子性；启动对多个 Session 的生命周期更新、容量淘汰与随后新建之间继续没有跨操作事务。已成功的前序写入不会因后续失败回滚。

使用方式（重启时使用同一绝对数据库路径、超时和容量配置）：

```csharp
IAgentSessionStore store = new SQLiteAgentSessionStore(
    Path.GetFullPath("Output/AgentSession/sessions.db"));
using IAgentSessionManager manager = new AgentSessionManager(
    idleTimeout: TimeSpan.FromMinutes(30),
    store: store,
    options: new AgentSessionManagerOptions
    {
        MaxMessagesPerSession = 256,
        MaxSessions = 1024,
        ArchiveAfterIdleTimeout = TimeSpan.FromHours(24),
        LifecycleSweepInterval = TimeSpan.FromMinutes(1)
    });

AgentSession context = manager.ProcessMessage(envelope);
// 已有 Source ID 时：manager.ProcessMessage(envelope, sourceIds);
```

Manager 管理自己的定时器，Dispose 停止定时器；Store 不保持长期连接，不需要额外 Dispose。数据库路径与宿主选择不需要修改 Adapter。

## 编译与验证

使用仓库 `.tools/dotnet/dotnet.exe`（SDK 8.0.425）。先还原主工程和验证工程的依赖；本地日志保存在 `Output/ValidationV1330C/`。

| 命令 | 结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -t:Rebuild -v q` | **0 Warning / 0 Error** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |
| `dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |
| `ChangeDetectionChecks.exe --agent-session-only` | **38/38** |
| `ChangeDetectionChecks.exe --agent-session-hardening-only` | **79/79** |
| `ChangeDetectionChecks.exe --agent-session-persistence-only` | **91/91** |
| 完整 `ChangeDetectionChecks.exe` | **810/810**，包括原 719 项及新增 91 项 |
| `Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot` | **17/17**；六份上游源文件字节一致，ForbiddenBusinessCalls=0，QnbotCoreSourceUnmodified=true |

qnbot 独立 .NET Framework 4.8 兼容宿主仍保留固定上游 BuyerMessageBurstCoordinator.cs:744 的 **1 条既有 CS4014 Warning / 0 Error**。本次 Agent 工程为零警告；没有修改或屏蔽禁止改动的上游代码，不能把兼容宿主描述为零警告。

91 项新增验证覆盖身份优先级、fallback、字段/大小写/分隔符隔离、固定身份向量、默认入口并发去重、SQLite 三表快照、UTC/Unspecified/Local 时间类型、Active/Inactive/Archived 恢复、原生及生成 ID 的重启去重、独立进程恢复和写入、精确 idle / retention 边界、停机期限、恢复后 UTC 前跳后跳、负停机时间、消息/状态中途失败回滚、删除失败恢复、外键级联、恢复容量拒绝、恢复后最旧淘汰、InMemory 同接口恢复、数据库损坏及版本拒绝。

原 Session 检查共 38 项保留。由于默认入口由随机 GUID 改为稳定身份，其中 ID 格式断言同步更新；时钟活动及并发追加夹具改为不同消息，继续检验真正新消息的追加行为。重复流量另由新检查覆盖，不能通过重复同一消息来验证新消息追加。

日志：build-main.log、build-checks.log、build-runtime-observer.log、agent-session.log、agent-session-hardening.log、agent-session-persistence.log、regression.log、compatibility.log。兼容原始记录：`Output/Integration/run_20260930_161821_587/`。测试生成的 SQLite 文件位于 `Output/ValidationV1330C/databases/`，均被仓库忽略。

Session 隔离路径：**AI calls=0、Intent calls=0、Knowledge calls=0、Reply calls=0、SentMessages=0**。新增代码依赖仅为输入 DTO、Agent 接口、TimeProvider、集合、密码学哈希及 SQLite，没有调用业务服务。原 Adapter 合成验证继续确认无 ReplyPreview、发送关闭及 SentMessages=0。全量历史回归仍执行既有合成 AI/Intent/Knowledge mock 检查，这不代表本阶段接入真实业务调用。未执行 live-smoke。

## 当前限制

- 指纹只能识别可观察输入；同身份、同角色、同 ticks/Kind、同正文的两条真实消息无法区分。应优先交付原生 Source MessageId。观察时间或文本变动会产生不同指纹，不能保证 OCR 修订/显示时间精度变化的重试仍幂等。
- 无时间 fallback 也无法区分字段完全相同的独立消息；稳定 fallback key 应来自调用方。Manager 本阶段继续拒绝不完整消息。
- 去重范围仍是 Session；同一 Active Session 重启后保留去重。Session 过期后新 Session 可以接受原 ID，没有永久/跨 Session 去重墓碑。
- 每个数据库要求一个 owning Manager；单实例并发有锁及事务保护，未提供多 Manager / 多进程协同更新、乐观版本或租约。独立子进程验证在前一个 Manager 已停止后接管。
- 恢复依赖 UTC 墙钟估计停机时间；重启前后机器时钟校正无法精确还原真实 elapsed。向后差值按零处理；恢复之后使用单调时间。
- 保存采用完整有界快照重写，恢复会加载全部保留历史；没有分页或字节配额。恢复后的配置容量小于已有数据会拒绝启动。
- schema v1 只支持新数据库初始化和同版本读取；无迁移、加密、备份/恢复工具或永久审计保留。容量淘汰会级联删除历史。
- SQLite 属于显式宿主选项，尚未挂载真实输入流；默认内存存储继续可用。未扩展 Intent、Knowledge、AI Reply 或发送范围。

## 下一步建议

先由 Agent 宿主明确固定数据库路径、原生 MessageId 交付、启动恢复失败/背压处理及停止流程，在受控只读消息流中验证实际字段稳定性；随后单独设计 schema 迁移、备份及字节容量。Intent、Knowledge、AI Reply 保留为后续独立阶段。
