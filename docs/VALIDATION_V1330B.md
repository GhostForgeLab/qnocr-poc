# V1.33.0-B Agent Session Production Hardening 验证

日期：2026-09-30（Asia/Shanghai）。Agent 基线：`1cd601f4205eaa77232ce3b74b3b3cfb3f43f60d`。固定 qnbot 离线源码快照：`4802807f26a6abf4cfb4457d5b158b2f51f4ff18`。

## 实现范围

强化 `Extension/AgentContext/AgentSessionManager` 的消息幂等、生命周期、存储接口和容量边界。继续使用已有 `MessageEnvelope → AgentSessionManager → AgentSession` 路径；Session 独立于 qnbot Session、私有对象及其生命周期。`IdentityLevel` 始终为 `Observed`，没有身份升级入口。

未改动 MessageEnvelope、Adapter、qnbot Core、Bridge 或 RuntimeObserver；未接入 AI、Intent、Knowledge、Reply 或自动发送。背景定时清理由 Manager 自己拥有，不依赖 qnbot 宿主。没有生产挂载或真实消息发送。

## 修改文件

| 文件 | 变化 |
| --- | --- |
| `Extension/AgentContext/AgentSessionManager.cs` | 稳定 ID 去重、生命周期推进、存储写入/删除、容量拒绝及非 Active 淘汰、可选定时清理和 Dispose。 |
| `Extension/AgentContext/IAgentSessionManager.cs` | 新增稳定 ID 重载、SweepLifecycle；继承 IDisposable；保留原调用入口。 |
| `Extension/AgentContext/AgentSession.cs` | 增加只读 StateHistory。 |
| `Extension/AgentContext/AgentSessionMessage.cs` | 明确 MessageId 可由调用方提供，也可由 Agent 生成。 |
| `Extension/AgentContext/SessionState.cs` | Active、Inactive、Archived，以及不可变 AgentSessionStateChange 记录。 |
| `Extension/AgentContext/AgentSessionManagerOptions.cs` | 容量、归档保留时间、可选清理周期；AgentSessionCapacityException。 |
| `Extension/AgentContext/IAgentSessionStore.cs` | 保存完整 Session/消息/状态历史，读取和删除 Session 的接口。 |
| `Extension/AgentContext/InMemoryAgentSessionStore.cs` | 线程安全内存实现，保存时复制集合，读取只读快照。 |
| `Validation/ChangeDetectionChecks/AgentSessionHardeningChecks.cs` | 新增 79 项幂等、生命周期、容量、存储失败及调度验证。 |
| `Validation/ChangeDetectionChecks/Program.cs` | 新增 --agent-session-hardening-only，纳入完整回归。 |
| `docs/VALIDATION_V1330B.md` | 本轮设计、命令、证据、结果与限制。 |

## Session Hardening 设计

### 消息幂等

`ProcessMessage(envelope, messageIds)` 要求 ID 数量与 envelope 消息数量相同，且每个 ID 非空。ID 按消息在 envelope 内的位置对应，采用精确、区分大小写的比较，不修剪或归一化。

同一 Session 中，每个 ID 最多追加一次；同一批次内重复 ID 也只接受第一次。重复 ID 携带不同内容时忽略后续内容，绝不覆盖旧消息。重复和新消息混合的批次只追加新 ID，保持接收顺序。相同正文配不同 ID 仍是不同消息。去重集合只保存已成功提交的 ID，大小受消息数量限制，没有无限增长的去重墓碑。

整批重复时返回原快照，不写 Store，也不更新 LastActiveTime 或单调活动时间；重复流量不能无限延长 Session。出现有效新 ID 才刷新活动时间。完整输入先验证，非法批次不会追加、清理生命周期或写 Store。

由于禁止修改 v1 MessageEnvelope/Adapter，原 `ProcessMessage(envelope)` 继续生成 Agent 本地 GUID，保留 V1.33.0-A 的接收语义。需要重试幂等时，调用方必须使用新重载并提供稳定 ID；不按正文/时间猜测平台 ID，也不错误合并合法的相同文本。

去重范围为单个 Session。超时后，同一观察身份的新输入创建新的 SessionId，不借用旧消息或去重集合；新 Session 可以接受旧 Session 用过的 ID。

### 生命周期

| 状态 | 条件与行为 |
| --- | --- |
| Active | 第一次有效输入创建；只接收属于当前观察身份的新消息。 |
| Inactive | 距最后一次成功追加的单调时间 `elapsed >= idleTimeout`；移除当前 Active 身份映射，保留历史。 |
| Archived | `elapsed - idleTimeout >= ArchiveAfterIdleTimeout`；从空闲截止点计算保留期，保留只读历史，永不再次变为 Active。 |

默认空闲超时 30 分钟，归档保留期为空闲截止点之后 24 小时。晚到的 sweep 会同时记录 Active → Inactive → Archived，不因清理延迟重新计时。无需额外 Expired 状态：Inactive 表示不再接收，Archived 表示保留期已结束。

StateHistory 记录 From、To、ChangedTime、Reason。创建记录 From=null/To=Active；后续原因分别是 IdleTimeout 和 RetentionElapsed。ChangedTime 是观察到变化的 UTC 时间，不冒充精确发生时刻；时钟向后校正时不会早于上次状态时间或 LastActiveTime。真正的截止时间用 TimeProvider 单调时间判断。状态历史最多三条，旧快照不随清理改变。

ProcessMessage/GetSession/ExpireInactiveSessions/SweepLifecycle 都应用生命周期。兼容入口 ExpireInactiveSessions 返回新离开 Active 的 Session 数量；SweepLifecycle 返回状态转换数量，晚 sweep 可能对同一 Session 返回两次转换。归档旧 Session 不会移除同身份的新 Active 映射。

设置 LifecycleSweepInterval 可启动独立 TimeProvider 定时器，无流量也能清理；默认 null 保持原宿主行为，避免暗中创建后台资源。使用者应以 using/Dispose 管理生命周期。Dispose 停止定时器，忽略排队的晚回调，并拒绝后续业务操作。后台 Store 失败保存到 LastLifecycleError，下一次成功的定时清理清除错误；显式 API 的存储异常直接传播。

### 容量保护

默认 MaxMessagesPerSession=256，MaxSessions=1024；均要求正数。MaxSessions 包含 Active、Inactive 和 Archived，归档不会绕过容量上限。

- 消息超限：只计算去重后的新 ID；整批新消息拒绝，抛 AgentSessionCapacityException，已有消息和活动时间不变。不截断旧历史，所以已接受 ID 仍能去重。纯重复在满容量时仍可忽略。
- Session 超限：生命周期检查后，只在非 Active 中按最后活动单调时间、创建顺序选择最旧项，从 Store 和 Manager 删除。所有保留 Session 都 Active 时，拒绝新 Session，绝不淘汰当前 Active。
- 新 Session 的首批消息超限时，先拒绝，不创建空 Session，也不为了这批消息腾出容量。
- 对全部操作加同一实例锁，防止并发创建、重试或清理突破边界。不同买家、店铺、来源和大小写仍保持隔离。

超限和存储异常可以伴随此前已经成功的生命周期清理；不存在整个 ProcessMessage 的跨 Session 事务。淘汰为独立操作：淘汰成功后，如果新 Session 保存失败，被淘汰的非 Active 历史不会回滚。

## 新增接口说明

```csharp
public interface IAgentSessionStore
{
    void SaveSession(AgentSession session);
    AgentSession? GetSession(string sessionId);
    bool DeleteSession(string sessionId);
}
```

SaveSession 原子 upsert 完整 Session 快照，包含 Messages 和 StateHistory；GetSession 返回快照或 null；DeleteSession 返回是否存在被删除项。实现必须保证单次操作失败不改变存储值。默认 InMemoryAgentSessionStore 自带锁，保存时复制集合，拒绝通过读取结果修改集合。

Manager 注入独占、初始为空的 Store，成功保存后才提交内存索引、活动时间及消息 ID。失败的追加不消耗 ID，重试可恢复；失败的状态保存不提交对应状态；删除失败不移除 Manager 索引。读取最新生命周期快照由 Manager 内存索引提供；Store 读取接口供独立读取和未来恢复逻辑使用。本阶段没有枚举、恢复、跨操作事务或数据库接入。

使用示例（envelope 由既有入口提供；stableIds 必须由调用方维护，重试复用）：

```csharp
using IAgentSessionManager manager = new AgentSessionManager(
    idleTimeout: TimeSpan.FromMinutes(30),
    store: new InMemoryAgentSessionStore(),
    options: new AgentSessionManagerOptions
    {
        MaxMessagesPerSession = 256,
        MaxSessions = 1024,
        ArchiveAfterIdleTimeout = TimeSpan.FromHours(24),
        LifecycleSweepInterval = TimeSpan.FromMinutes(1)
    });
AgentSession snapshot = manager.ProcessMessage(envelope, stableIds);
AgentSession? latest = manager.GetSession(snapshot.SessionId);
int transitions = manager.SweepLifecycle();
```

## 编译

使用仓库已有 `.tools/dotnet/dotnet.exe`，没有安装新包或修改项目依赖。

| 命令 | 结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -t:Rebuild -v q` | **0 Warning / 0 Error** |
| `dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0 Warning / 0 Error** |

后两条使用已完成重编译的主工程，避免重复构建。独立 qnbot .NET Framework 4.8 离线兼容宿主保留固定上游 BuyerMessageBurstCoordinator.cs:744 的 **1 条既有 CS4014 Warning / 0 Error**。本次 AgentContext 编译没有警告；不能把兼容宿主也描述为零警告，未修改或屏蔽禁止改动的上游代码。

## 幂等测试

稳定 MessageId 重复进入只保存一次；冲突重试不覆盖旧内容；批内重复、混合批次、相同正文不同 ID、大小写、跨身份和跨 Session 范围均符合策略。64 个并发重试只形成一个 Session 和一条消息。纯重复不更新活动时间，在原空闲截止点正常进入 Inactive。非法 ID/消息不造成部分追加或生命周期副作用。

## 生命周期测试

创建 Active、idleTimeout 前一 tick、恰好截止、归档前一 tick、恰好归档和清理幂等均通过。延迟 sweep 完整记录两次状态转换。旧 Session 归档不影响同身份新 Active Session。UTC 前跳/后跳不改变实际截止，状态时间不回退，旧快照及只读集合保持隔离。可选独立定时器可无流量推进状态，失败可诊断和恢复，Dispose 及晚回调符合预期。

## 容量测试

覆盖首批超限、已有 Session 批量超限及无部分提交、去重后容量计算、满消息容量仍可忽略重复、全 Active 时拒绝新建、非 Active 淘汰及 Store 删除、最旧优先、连续 20 次归档淘汰保持上限、并发 32 条新消息仅接受 8 条、并发 16 个身份仅创建 3 个 Session。Store 保存和删除失败的恢复路径也通过。

## 回归

| 检查 | 结果 |
| --- | --- |
| `ChangeDetectionChecks.exe --agent-session-only` | **38/38**；原 Session 检查未修改。 |
| `ChangeDetectionChecks.exe --agent-session-hardening-only` | **79/79**。 |
| 完整 `ChangeDetectionChecks.exe` | **719/719**；原有 **640/640** 全部保持通过，新增 **79/79**。 |
| `Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot` | **17/17**；SourceFilesVerified=6，QnbotCoreSourceUnmodified=true，ForbiddenBusinessCalls=0。 |

本地忽略日志位于 `Output/ValidationV1330B/`：build-main.log、build-runtime-observer.log、build-checks.log、agent-session.log、agent-session-hardening.log、regression.log、compatibility.log。兼容原始记录：`Output/Integration/run_20260930_155006_325/`。

## 安全检查

Session 路径确认：**AI 调用 0、Intent 调用 0、Knowledge 调用 0、Reply 生成 0、SentMessages 0**。Manager 依赖仅为 MessageEnvelope、独立 Store、TimeProvider 和本地集合；新增验证使用合成消息和内存/故障 Store，不含业务服务调用。原 Session 的 Adapter 合成夹具仍验证 ReplyPreviewId=null、SendEnabled=false、SentMessages=0。SessionIdentity.cs 未修改，所有状态下 IdentityLevel 都保持 Observed。

上述数字针对本轮隔离 Session 路径，不是新造的业务运行时计数器。完整历史回归仍运行既有合成 Intent/Knowledge/AI mock 检查，不代表本阶段接入业务服务。未运行任何可选 live-smoke 命令。qnbot 外部工作树保持干净，兼容检查的六份源文件与固定基线 SHA256 一致。

## 当前限制

- 默认仍是单进程内存 Store，无 SQLite/文件/数据库或启动恢复。Store 接口为未来实现预留，不能通过重新注入旧 Store 恢复 Manager 的活动索引和单调截止。
- 幂等依赖调用方提供稳定 ID；未携带 ID 的原入口保留 GUID 语义。不能声称它实现了平台原生消息幂等，也不跨 Session/进程去重。
- 容量按消息/Session 数量控制，没有正文/ID 字节配额；外部持有的旧快照不在 Manager 配额内。满消息容量的 Active Session 对新 ID 返回明确异常，宿主应处理背压，不自动生成回复。
- Archived 历史在容量允许时可读，容量淘汰后无法恢复；没有永久审计保存或跨操作回滚。独立使用 MemoryStore 自身不施加 Manager 容量策略。
- 定时调度需显式设置周期并由宿主 Dispose；默认依靠读取/输入/显式 sweep。LastLifecycleError 只提供最近一次定时失败，没有外部告警接入。
- 每次追加复制有限消息快照，生命周期 sweep 遍历有限 Session；Store 是同步调用，单实例串行，无跨进程锁、恢复冲突或分布式一致性。
- 仍未挂载真实消息流；Source/SellerId/BuyerId 仅是观察键。CurrentProductContext/IntentHistory 仍为空占位。

## 下一步建议

先在受控输入中制定稳定 MessageId 交付契约和重试/背压处理，并由 Agent 宿主管理定时器及停止流程；随后独立设计 SQLite/文件 Store 的枚举恢复、恢复后期限计算、事务与字节配额。完成这些验证后再按独立阶段评审 Intent、Knowledge、AI Reply，继续保持 Observed 信任边界与发送冻结。
