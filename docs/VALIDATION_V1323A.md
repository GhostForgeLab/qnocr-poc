# V1.32.3-A External Runtime Observer PoC 验证

日期：2026-09-28。Agent 仓库基线 7f10eda6dddfa67e7e9c32894303e332b632220b；qnbot 源码固定快照 4802807f26a6abf4cfb4457d5b158b2f51f4ff18。

## 实现边界

RuntimeObserver/RuntimeObserver.csproj 是可独立启动的 .NET 8 Windows 进程，只使用 Process.GetProcessesByName 取得目标是否存在及 PID。IRuntimeObserver、ExternalProcessObserver 和 RuntimeObservedEvent 不引用 qnbot 内部对象。事件只有 ObserverStarted、ObserverStopped、TargetAbsent、TargetRunning、TargetStarted、TargetExited；Source=ExternalProcessObserver、IdentityStatus=Observed 为固定值。元数据仅有进程名与 PID。

Runner 将事件映射到既有 AgentEventEnvelope 的 RuntimeObserved 类型，经同用户 Named Pipe 发送。Agent 侧 AgentRuntimeObservationReceiver 从 IAgentBridge 读取并校验。此接收入口不调用 IAgentExtension.ProcessMessageAsync，也不创建 MessageEnvelope。旧 BuyerMessageObserved 校验保持独立；运行事件的 SessionId、BuyerId、Content、ProductId 必须为空，Verified 和额外元数据均被拒绝。

独立程序用法：

~~~powershell
dotnet run --project RuntimeObserver/RuntimeObserver.csproj -- --target Bot --pipe qn_runtime_demo --polls 10 --interval-ms 1000
~~~

每个事件各占一次 Pipe 连接，需有 Agent 侧客户端逐个接收。--target 为进程名，不是路径。--polls 省略时持续轮询，默认间隔 1 秒。该命令仅用于有受控接收端的诊断实验。

## 编译和检查

| 检查 | 结果 |
| --- | --- |
| dotnet build qnocr-poc.csproj --no-restore -v q | 0 Warning / 0 Error |
| dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -v q | 0 Warning / 0 Error |
| dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -v q | 0 Warning / 0 Error |
| 完整 ChangeDetectionChecks | 602/602；原有 591/591 保持通过，新增 11 项 |
| Agent Bridge + Runtime Observer 定向检查 | 16/16（含原 Bridge 5 项） |
| 独立 RuntimeObserver.exe + 外部 Pipe 客户端 | 3 个事件：ObserverStarted、TargetAbsent、ObserverStopped；全部 Observed |
| qnbot 离线兼容检查 | 17/17，SourceFilesVerified=6，QnbotCoreSourceUnmodified=true，ForbiddenBusinessCalls=0 |

完整回归日志：Output/ValidationV1323A/regression.log；兼容日志：Output/ValidationV1323A/compatibility.log。两者均为本地忽略产物。离线 .NET Framework 4.8 兼容宿主重编译仍有原 qnbot BuyerMessageBurstCoordinator.cs:744 的 1 条 CS4014 警告、0 错误；这不是本次新增模块，未修改或屏蔽上游代码。

Observer 检查先用可控 PID 快照确认启动、目标不存在、目标启动、状态不变、目标退出和 Observer 停止；再启动独立 Observer 子进程，让它观察仍在运行的验证进程，由 Agent 侧 Named Pipe 客户端逐条接收至停止事件。所有收到的事件均为 Observed，仅含进程元数据；Verified 或买家字段在传输校验时拒绝。进程存在只说明目标进程运行，不能证明 qnbot 已连接千牛或已有买家消息。

## 安全结果与限制

本 PoC 链路 AI 调用 **0**、SentMessages **0**、自动发送 **0**。这些数值来自 Observer/Receiver 不存在 AI、消息、Intent、Knowledge 或发送入口，且本阶段独立进程验证没有触发这些路径；完整历史回归中的合成 AI 检查不计入本 PoC。没有读取聊天消息、买家、商品或发送状态；没有 Hook qnbot 内部事件或修改 qnbot Core、Message/AI/Send Pipeline。

验证时未发现运行中的 Bot/qnrobot 进程，因此尚未实测真实 qnbot 实例的启动/退出时序、进程名及权限差异。当前链路只证明外部进程健康观察 → Observed 事件 → Agent Bridge → Agent 侧只读接收的隔离通信可行；**没有** Verified Buyer Message 或客服处理能力。短于轮询间隔的进程生命周期可能被漏检；Named Pipe 需要接收方连接，每条事件只传一次，没有持久化和重试语义。

下一步应在受控的真实 qnbot 运行环境核对实际进程名、启动/退出、权限与漏检率；任何消息身份核验或业务接入须另行评审，继续保持 Observed 和发送冻结。
