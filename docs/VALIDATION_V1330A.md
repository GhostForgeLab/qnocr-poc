# V1.33.0-A Agent Session Manager PoC 验证

日期：2026-09-30（Asia/Shanghai）。Agent 仓库基线：4651bbd。固定 qnbot 离线源码快照：4802807f26a6abf4cfb4457d5b158b2f51f4ff18。

## 实现范围与结论

本阶段建立 `MessageEnvelope → IAgentSessionManager.ProcessMessage → AgentSession`，只负责创建、消息归属、活动时间更新和失效。新模块位于 `Extension/AgentContext/`，由 Agent Extension 自己拥有；没有复用 qnbot Session、私有对象、生命周期或原 Shadow SessionRegistry。

既有 `MessageEnvelope`、`QnbotAgentAdapter`、`IAgentExtension` 和 Agent Bridge 源码与契约保持原样。新增验证夹具实现 `IAgentExtension`，把现有 Adapter 交付的 MessageEnvelope 交给 Manager，并返回没有回复或发送的 receipt；这个夹具只存在于 Validation，没有安装到真实 qnbot 或生产采集入口。RuntimeObserved 仍然只表示进程元数据，不映射为消息或 Session。

## 模块与数据模型

| 文件/类型 | 作用 |
| --- | --- |
| `Extension/AgentContext/IAgentSessionManager.cs` | `ProcessMessage` 返回 Session 快照；`GetSession` 按本地 ID 返回最新状态；`ExpireInactiveSessions` 返回本次新失效数量。 |
| `Extension/AgentContext/AgentSessionManager.cs` | 内存管理器，串行保护创建/追加/失效；默认空闲超时 30 分钟，可传入正数 TimeSpan 和 TimeProvider。 |
| `Extension/AgentContext/AgentSession.cs` | SessionId、CreatedTime、LastActiveTime、Messages、Identity、Status；CurrentProductContext 固定 null，IntentHistory 固定空只读列表。Status 为 Active/Inactive。 |
| `Extension/AgentContext/AgentSessionMessage.cs` | MessageId、Timestamp、Content、Source、SessionId，并保留原 Role。MessageId 为新生成的 Agent 本地 GUID，不代表平台原生消息 ID。 |
| `Extension/AgentContext/SessionIdentity.cs` | Source、SellerId、BuyerId 组成精确键；IdentityLevel 定义 Observed、SessionBound、VerifiedCandidate，本阶段只输出 Observed，不提供升级操作。 |
| `Validation/ChangeDetectionChecks/AgentSessionChecks.cs` | 合成生命周期、隔离、输入边界、并发和 Adapter 到 Context 检查。 |
| `Validation/ChangeDetectionChecks/Program.cs` | 新增 `--agent-session-only` 定向入口，并纳入完整历史回归。 |
| `docs/VALIDATION_V1330A.md` | 本轮设计、验证结果、边界和下一步。 |

SessionId 使用随机本地 GUID，不复用上游标识。CreatedTime 和 LastActiveTime 使用接收时的 UTC 时间；消息 Timestamp 原样保留 DateTime 和 Kind，不推断时区、不用历史/未来消息时间维持活跃。消息顺序为接收顺序和 envelope 内部顺序，不重排历史。

## 生命周期规则

1. 第一个有效 envelope 创建 Active Session，所有消息分配同一 SessionId。
2. 同一 `(Source, SellerId, BuyerId)` 的新 envelope 追加到当前 Active Session，更新 LastActiveTime；不同买家、店铺、来源或大小写不合并，也不修剪或归一化标识。
3. 自最近一次有效输入的单调时钟经过时间达到超时值（`elapsed >= timeout`）时，Session 转为 Inactive。`ProcessMessage` 和 `GetSession` 会先检查过期，也可以显式调用 `ExpireInactiveSessions`；没有后台定时器。
4. Inactive Session 的历史和活动时间保留，再来同一身份消息时创建新的 SessionId，从空历史开始。按旧 ID 仍能读取旧 Inactive 快照，不向新 Session 注入旧消息。
5. 返回值为不可变记录和独立只读消息集合。之前取得的快照不会自行变化；需要最新状态时调用 `GetSession`。外部不能修改 Manager 内部历史。
6. 缺少 Source/SellerId/BuyerId、非 v1 Schema、空消息批次、空消息项、空角色/正文或缺时间戳均拒绝。整个批次验证后才改变状态，不创建通用 Unknown 身份桶。
7. 超时用 TimeProvider 的单调时间；UTC 时钟跳变不会提早失效或延长实际空闲期限。UTC 时间向后校正时 LastActiveTime 不回退。

使用示例（仅内存生命周期）：

```csharp
IAgentSessionManager manager = new AgentSessionManager(TimeSpan.FromMinutes(30));
AgentSession session = manager.ProcessMessage(envelope);
AgentSession? latest = manager.GetSession(session.SessionId);
int newlyInactive = manager.ExpireInactiveSessions();
```

## 编译与检查

本仓库使用已有 `.tools/dotnet/dotnet.exe`；不增加依赖或修改项目结构。命令从仓库根目录执行。

| 检查 | 结果 |
| --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -t:Rebuild -v q` | **0 Warning / 0 Error** |
| `dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q` | **0 Warning / 0 Error** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -v q` | **0 Warning / 0 Error** |
| `ChangeDetectionChecks.exe --agent-session-only` | **38/38** |
| 完整 `ChangeDetectionChecks.exe` | **640/640**；原有 **602/602** 保持通过，新增 Session 检查 **38/38**。 |
| `Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot` | **17/17**；SourceFilesVerified=6，QnbotCoreSourceUnmodified=true，ForbiddenBusinessCalls=0。 |

新增 38 项检查覆盖首次创建、连续追加及活动时间、消息归属和原字段保存、超时前一 tick/恰好超时、失效幂等、超时后新会话及历史隔离、跨买家/店铺/来源/大小写/分隔符隔离、未知 ID、批量原子拒绝、快照只读、输入完整性、默认与非法超时、读取/输入触发失效、UTC 时钟前后校正、32 个并发输入不丢消息，以及既有 Adapter 的合成 envelope 到 Agent Context。

.NET Framework 4.8 离线兼容宿主重编译仍有固定上游 `BuyerMessageBurstCoordinator.cs:744` 的 **1 条既有 CS4014 Warning / 0 Error**；这是独立兼容宿主的历史警告，不来自 AgentContext。未修改或屏蔽 qnbot 上游代码，因此不能把所有回归工程合计描述为零警告。

本地忽略产物：`Output/ValidationV1330A/build-main.log`、`build-checks.log`、`agent-session.log`、`regression.log`、`compatibility.log`。兼容宿主原始记录：`Output/Integration/run_20260930_151123_871/`。

## 安全检查

本 Session PoC 路径：**AI 调用 0、Intent 调用 0、Knowledge 调用 0、Reply 生成 0、SentMessages 0**。Manager 只依赖 MessageEnvelope、内存集合、GUID 和 TimeProvider；构造器及实现没有 AI/Intent/Knowledge/Reply/Send 服务，也没有网络、文件持久化或 qnbot 调用入口。数值是对这条隔离路径及依赖的检查结论，没有新增虚构的运行时业务计数器。

Adapter 合成夹具只将 envelope 交给 Manager；验证 receipt 的 ReplyPreviewId=null、SendEnabled=false、SentMessages=0，IdentityLevel 仍为 Observed。没有真实买家身份验证，没有生产挂载。完整历史回归仍执行原来的隔离/合成 Intent、Knowledge、AI 路径检查，其测试调用不属于本次 Session-only PoC，也没有执行可选 live-smoke 命令。

## 当前限制

- 仅单实例内存状态；重启不恢复，不跨进程共享，没有持久化、清理配额或历史窗口。Inactive 历史与 Active 消息持续保留，长期运行前需设计容量边界；追加会复制快照，过期检查会遍历 Active Session。
- 未挂载真实千牛/qnbot 消息流；集成只由合成 Adapter 夹具验证。Bridge 没有新增 Session 事件或消息映射。
- MessageEnvelope 没有平台 MessageId/ConversationId；同一有效消息重复输入仍会追加，本地 MessageId 不提供原生去重。上游须只交付新消息，后续需另行设计幂等。
- 观察身份只能按给定的 Source/SellerId/BuyerId 精确区分，不能发现同一观察标识在平台上发生身份切换，也不证明它是可信身份。缺失键直接拒绝；不会猜测、合并或借历史。
- 超时通过 API 调用或显式 sweep 生效；无后台自动任务或关闭状态。旧快照的 Status 不随时间自行变化，读取最新快照会应用过期。
- CurrentProductContext 和 IntentHistory 只是占位；没有商品归属、意图、知识检索、AI 回复或自动发送能力。

## 下一步建议

下一阶段先在受控消息输入上核对 Source/SellerId/BuyerId 的稳定性和 envelope 是否只包含新增消息，补充原生消息证据与幂等、容量及持久化方案；明确过期 sweep 的宿主调度方式。完成 Context 隔离与恢复验证后，再分别评审 Intent、Knowledge 和 AI Reply 的接口，继续保留独立身份信任边界和发送冻结。
