# V1.34.0-C Real Multi-Buyer Context 验收入口与回归

日期：2026-10-03（Asia/Shanghai）。基线：669688573003e7fbe9ade9e17c19c23aff9bdc4b。

## 判定

真实验收尚待完成。已准备同一 Context 组件内的双买家实时采集入口。合成检查、截图预检与历史 V1.34.0-A 证据不能作为本阶段通过的依据。

保持 Context-only / Shadow / Observed / No Send。业务代码修改数为 0；既有 SQLite schema、MessageEnvelope、Adapter、qnbot Core 与 AI/Send Pipeline 均未修改。

## 修改文件

| 文件 | 作用 |
| --- | --- |
| Validation/RealMultiBuyerContextChecks.cs | 同一 Pipeline 的真实 A/B/A 采集、原 Envelope 重放、受控昵称别名回放、组件重建、恢复后 A/B 追加、证据审计与合成入口检查。 |
| Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj | 链接验证文件；不编入业务主项目。 |
| Validation/ChangeDetectionChecks/Program.cs | 三个独立验收入口、参数失败处理与全量回归注册。 |
| Validation/Run-RealMultiBuyerV1340C.ps1 | 独立新库启动脚本；采集和证据核对都成功才报告通过。 |
| docs/VALIDATION_V1340C.md | 验证范围、命令、回归与边界。 |
| docs/REAL_MULTI_BUYER_ACCEPTANCE_V1340C.md | 脱敏真实验收记录。 |

原有两份未跟踪 V1.32.2 文档保持原状，不属于本次修改。业务修改原因/影响/风险/回滚不适用。验证代码和文档可通过独立提交回滚。本地 Output/ValidationV1340C/ 证据保留，不将含昵称/截图的输出加入 Git。

## 验收流程

1. 操作者确认两个不同受控测试账号，将其千牛显示昵称保存在 Git 忽略目录 Output/ValidationV1340C/buyers.local.json，字段为 BuyerA、BuyerB。报告只使用 A/B。
2. 只允许一个可见、未最小化、未 cloaked 的接待中心窗口；每帧重核 HWND/PID/店铺标题。此入口不点击 UI 或发送消息。
3. 每轮使用全新输出目录，SQLite 固定为其下 sessions.db，不接收生产数据库路径，不复用已有库。
4. 按 STAGE_READY 从对应买家发送准确的 A1/A2 → B1/B2 → A3。A/B/A 使用同一 Pipeline。身份来自真实 OCR 标题经原 Normalizer 与观察 Hash；正文只作为受控消息验证标记。
5. 重放已捕获真实 Envelope，比较完整 Context/SQLite、MessageId、LastActiveTime。另将真实已捕获输入用于明确标注的受控别名回放，不改生产采集输入。
6. Dispose 并重新创建 Context Pipeline，打开同一 SQLite。比较两边完整快照，重放旧消息验证去重索引恢复。
7. A、B 各发一条新消息 A4/B3，分别追加到原 Session。
8. 沿用原 30 分钟 idle timeout、容量与 schema。每阶段最多 4 分钟；超时判 Incomplete。
9. 保留 PNG、OCR worker JSON、BuyerMessageBatch、原始/Normalized 昵称、Envelope、Context、阶段快照、重放/恢复快照。关闭组件后 checkpoint SQLite，计算包括数据库在内的 SHA-256 清单。
10. 独立审计从保留 OCR JSON 经原 Structurer/OcrMessageSource 重新生成 Envelope，核对 Stable Identity、A/B 历史、SessionId 连续性、SQLite 2/7、Observed 和完整性。

昵称标准化碰撞在采集/建库前停止。不做模糊匹配。不能确认当前阶段买家时 Held。Context 出现额外 Session、跨买家历史、同文不同时间的重复身份时停止并保留真实失败数据库，不以过滤或合并掩盖。

旧证据审计不通过 Context Manager 打开数据库，避免当前墙钟时间引起生命周期迁移；直接检查持久化快照。Schema 始终为 v1。

## 命令

仓库根目录执行：

~~~powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
& ./.tools/dotnet/dotnet.exe build qnocr-poc.csproj --no-restore -t:Rebuild -v q
& ./.tools/dotnet/dotnet.exe build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false
& ./Validation/Run-RealMultiBuyerV1340C.ps1 -BuyersPath ./Output/ValidationV1340C/buyers.local.json
~~~

启动脚本生成本轮数字 prefix 和全新目录。依次发送：prefix+01规格测试、prefix+02容量测试、prefix+11颜色测试、prefix+12发货测试、prefix+03切换测试、prefix+04恢复测试、prefix+13恢复测试。每条均带完整 prefix，不提前发送下一阶段。

~~~powershell
$checks = './Validation/ChangeDetectionChecks/bin/Debug/net8.0-windows/win-x64/ChangeDetectionChecks.exe'
& $checks --real-multi-buyer-only
& $checks --real-multi-buyer-evidence <本轮真实目录>
& $checks --real-message-context-only
& $checks --identity-normalization-only
& $checks
& ./Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot
~~~

创建采集目录下 stop.request 可停止下一帧；保留证据，不判通过。

## 已执行检查

| 检查 | 结果 |
| --- | --- |
| 主项目 .NET 8 Rebuild | 0 Warning / 0 Error |
| 验证项目 .NET 8 Rebuild | 0 Warning / 0 Error |
| RuntimeObserver .NET 8 Rebuild | 0 Warning / 0 Error |
| 新增入口检查 | 25/25，明确为合成输入 |
| 原 Context 集成 | 24/24 |
| 原 Identity Normalization | 90/90 |
| 全量回归 | 949/949 = 原 924 + 新增 25 |
| qnbot 兼容 | 17/17；ForbiddenBusinessCalls=0；六份上游源文件字节不变 |
| 实时截图预检 | PrintWindow 成功；像素检查通过，不等同于 Context 验收 |
| 无效新命令参数 | 返回 2，不进入实时采集 |
| 双买家真实 Context 证据 | 待执行 |

兼容 .NET Framework 4.8 宿主保留既有上游 CS4014 警告 1 条（BuyerMessageBurstCoordinator.cs:744），0 Error；未修改上游或屏蔽警告。

新增检查覆盖五阶段 2/2/3/4/3 协议、时间前进后的重放、完整恢复与各自追加、受控别名、错误买家/文本在写入前拒绝、昵称碰撞在建库前拒绝、禁止已有库复用、时间 OCR 漂移产生重复时停止且保留证据、Incomplete 不能通过。未运行 provider-live-smoke 或真实业务 AI。

日志保留在 Output/ValidationV1340C/。兼容原始产物：Output/Integration/run_20261003_204351_419/。

## 安全与限制

Context 验收路径 AI / Intent / Knowledge / Reply / SentMessages 为 0。依据是该路径没有业务服务依赖、分发或发送入口；计数字段是此结构边界的回执，不是 Provider 遥测。历史回归含业务 mock 测试，不能将 mock 与真实业务调用混为一谈。

昵称仍仅为 Observed 候选，不证明平台 Buyer ID。两个账号标签若经原规则相同必须停止，不能升级 Verified 或扩大规则。正文/时间 OCR 波动仍可能改变 MessageId，新入口保留失败证据。组件重建不等于千牛/qnbot 进程重启、多进程写入或生产容量验收。

只有真实七条消息、两位买家、五阶段、重放、恢复后追加及原回归全部通过，才可评审将基础 Context 链判为稳定。后续单独评审 JEV Intent / Knowledge / AI Reply Preview，不在本任务中启用。
