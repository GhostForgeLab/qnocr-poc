# V1.34.0-B Identity Normalization & Multi Buyer Isolation PoC 验证

日期：2026-09-30（Asia/Shanghai）。项目：qnbot-agent / qnocr-poc。

基线提交：`5958de770fe13ab7ca2117e3e90f2a427f9dc43d`（V1.34.0-A）。

## 验收结果

新增 **90/90** 检查通过；完整回归 **924/924**，保留并通过原有 834 项；原 Context 集成检查 **24/24**；qnbot 兼容检查 **17/17**。

买家 A（张三）的 A1/A2 和买家 B（李四）的 B1/B2 交错进入同一个 Context Pipeline，最终为 **2 个不同 Session / 4 条消息**。空白与末尾句读别名进入各自原 Session；重复输入不写 SQLite，不刷新 Context；重建 Pipeline 后隔离、MessageId 与去重继续成立。

本轮隔离使用合成 MessageEnvelope，未开展两位真实买家实时切换验收，未启动实时采集或发送消息。身份始终为 **Observed**，标准化相同不构成同一真实账号或 Verified Buyer 的证明。

## 修改文件

| 文件 | 改动 |
| --- | --- |
| `Extension/AgentContext/IIdentityNormalizer.cs` | Observed 买家标签标准化接口。 |
| `Extension/AgentContext/IdentityNormalizer.cs` | NFKC、Unicode 空白、末尾句读和 invariant 小写规则。 |
| `Extension/AgentContext/AgentContextPipeline.cs` | 在原 Manager 的默认 Stable Identity 路径之前标准化 BuyerId；同步覆盖 ProcessMessage / ReadAsync。 |
| `Extension/AgentContext/AgentContextCaptureCommand.cs` | 在原买家观察 Hash 前标准化原始 OCR 昵称；测试买家标签按同一规则比较；保留原始与标准化标签证据。 |
| `Validation/IdentityNormalizationChecks.cs` | 新增 90 项标准化、别名幂等、多买家隔离和 SQLite 恢复检查。 |
| `Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj` | 链接新验证文件。 |
| `Validation/ChangeDetectionChecks/Program.cs` | 增加独立验证入口，纳入完整回归。 |
| `docs/VALIDATION_V1340B.md` | 本报告。 |

保持原目录结构；没有新包依赖。工作区原有两份未跟踪的 V1.32.2 文档不纳入本次提交。

## Identity Normalizer 设计

```text
MessageEnvelope（原对象保留）
  → AgentContextPipeline / IdentityNormalizer（Observed BuyerId）
  → 新 Envelope 副本（仅替换买家候选键）
  → 原 AgentSessionManager.ProcessMessage
  → 原 AgentMessageIdentityProvider（精确字段、版本化 SHA-256）
  → 用同一个标准化 SessionIdentity 完成 Session 匹配及消息去重
  → 原 SQLiteAgentSessionStore
  → Agent Context
```

规则顺序：

1. 拒绝 null、空白输入；执行 Unicode `NormalizationForm.FormKC`（NFKC），统一全角兼容字符及组合/分解字符。
2. 按 Unicode Rune 删除 `Rune.IsWhiteSpace` 字符，包括空格、Tab、CR/LF、NBSP、全角空格；补充平面字符与 emoji 保留。
3. 只移除末尾句读集合：`.`、`,`、`!`、`?`、`:`、`;`、`。`、`、`；全角句读先由 NFKC 统一。保留内部/开头标点、连字符与下划线，避免额外折叠用户名。
4. 使用 `ToLowerInvariant()` 统一大小写。标准化后为空或 Unicode 不合法时拒绝输入，不创建或更新 Session。

例如：`张三` / `张 三` / `张三.` / `张三！` / `张三,` → `张三`；`Tom` / `tom` / `TOM` / `Ｔｏｍ！` → `tom`；`Cafe\u0301` / `CAFÉ` → `café`。

不进行 `0 → O`、`1 → l`、模糊相似匹配、字符猜测或音译。`user0` 与 `userO`、`user1` 与 `userl` 仍进入不同 Session。

Source 和 SellerId 保持精确、区分大小写；标准化只针对此 Context 边界的 Observed 买家候选输入。消息 Role、正文、Timestamp ticks/Kind、ProductId 和原调用者 DTO 保留原值。原 Provider、Manager、SQLite schema 与旧精确身份 API 均未修改。标准化后的候选键由原 Manager 同时交给 Provider 和 Session 查找，避免只改变 MessageId 却仍创建多个 Session。

实时采集入口原先先 Hash 买家昵称，Hash 后无法修复昵称别名。本轮将同一 Normalizer 放到该原观察 Hash **之前**，并按标准化结果比较人工指定的测试买家标签。`identity.json` 同时保留 BuyerLabel、NormalizedBuyerLabel；原 `observed:buyer:<SHA256>` 格式保留。无法读取、为空或非法的 OCR 候选仍 Held。SellerId 的观察 Hash 规则保持原样。

`SessionIdentity.IdentityLevel` 继续固定为 Observed；没有新增身份升级路径，也没有将候选标签接入 Adapter 的平台账号映射。

## 编译

SDK：仓库 `.tools/dotnet/dotnet.exe`，8.0.425；DOTNET_CLI_HOME 为仓库 `.tools/cli-home`。

| 命令（dotnet 为上述 SDK） | Warning | Error |
| --- | --- | --- |
| `dotnet build qnocr-poc.csproj --no-restore -t:Rebuild -v q` | **0** | **0** |
| `dotnet build Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0** | **0** |
| `dotnet build RuntimeObserver/RuntimeObserver.csproj --no-restore -t:Rebuild -v q -p:BuildProjectReferences=false` | **0** | **0** |

本轮 .NET 8 工程达到 **0 Warning / 0 Error**。原 .NET Framework 4.8 兼容宿主仍有 **1 条既有 CS4014 / 0 Error**，来自禁止修改的上游 `BuyerMessageBurstCoordinator.cs:744`；未修改或屏蔽该警告，不能把全部兼容宿主描述为零警告。

日志：`Output/ValidationV1340B/build-main.log`、`build-checks.log`、`build-runtime-observer.log`、`compatibility.log`。

## Normalization / Alias 检查

新增 90 项检查覆盖：

- 空格、Tab、换行、NBSP 与全角空格的昵称别名生成相同 Stable MessageId。
- ASCII / 全角 / 中文末尾句读差异归入相同候选键；内部标点仍区分。
- 英文大小写、全角英文字母、组合/分解 Unicode 经原 Provider 得到一致身份。
- 规则幂等；Unicode emoji 保留；数字与字母形似不自动纠错。
- 空白、句读组成的空候选、非法 surrogate 与 null 被拒绝，Context 和 SQLite 保持原状。
- Pipeline 默认入口和 IMessageSource 流式入口都应用规则。
- 原消息正文中的空格、全角逗号和大小写保持；原 Envelope 不被就地修改。

对别名重放，在合成数据库安装拒绝 Sessions UPDATE 的触发器。所有重放仍返回同一个 Context 实例，证明无新消息时不调用 SaveSession；完成后移除触发器。

## Multi Buyer Isolation 检查

| 输入顺序 | 标准化候选 | 归属 | Session 内容 |
| --- | --- | --- | --- |
| 张三 / A1 | 张三 | Session A | A1 |
| 李四 / B1 | 李四 | Session B | B1 |
| 张 三！ / A2 | 张三 | 原 Session A | A1、A2 |
| 李 Tab 四, / B2 | 李四 | 原 Session B | B1、B2 |

验证结果：Session A 与 B 的 SessionId 不同；每条消息的 SessionId 归属正确；SQLite 恰好两行 Sessions、四条 Messages；别名重放及重建 Pipeline 后重放仍是 **2 / 4**；两边身份均为 Observed。

还验证：相同正文和时间在 A/B 身份下生成不同指纹；同一个标准化买家跨 Source / Seller（包含大小写变化）保持不同 Session；数字/字母形似输入也保持不同 Session。

样本数据库路径记录在独立验证日志的 `NORMALIZATION_ISOLATION` 行；数据库位于 `Output/ValidationV1340B/checks/<run-id>/isolation.db`，保留在本地忽略目录。

## Regression

| 命令 | 结果 |
| --- | --- |
| `ChangeDetectionChecks.exe --identity-normalization-only` | **90/90** |
| `ChangeDetectionChecks.exe --real-message-context-only` | **24/24**（原集成检查未改动） |
| `ChangeDetectionChecks.exe` | **924/924 = 原 834 + 新增 90** |
| `Validation/QnbotCompatibility/Run-Compatibility.ps1 -QnbotRoot D:/CodexWork/qnagent/qnbot` | **17/17**，ForbiddenBusinessCalls=0，QnbotCoreSourceUnmodified=true |
| `git diff --check` | 通过 |

兼容测试重新检查六份上游源文件与锁定 manifest 字节一致，qnbot 基线 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18` 保持。

V1.34.0-A 的 **26/26 实时证据**属于已完成的历史验收。本轮未重新采集实际消息，也未复跑受 30 分钟 idle timeout 约束的旧实时证据入口，不将旧样本冒充本轮实时多买家验证。完整默认回归中的独立进程恢复检查正常通过。

日志：`Output/ValidationV1340B/normalization-checks.log`、`context-checks.log`、`regression.log`、`compatibility.log`。兼容产物：`Output/Integration/run_20260930_173757_377/`。

## 安全检查

| 本轮 Normalization / Context-only 路径 | 结果 |
| --- | --- |
| AI 调用 | **0** |
| Intent 调用 | **0** |
| Knowledge 调用 | **0** |
| Reply 调用 | **0** |
| SentMessages | **0** |
| 自动回复 / 自动发送 | 未接入 |
| 身份自动升级 Verified | 无，始终 Observed |

这些零调用结果来自本轮路径没有业务服务依赖或发送入口，以及独立检查回执；没有把常量字段宣称为业务 Provider 的遥测。完整历史回归仍运行原有合成业务 mock 测试，未执行 provider-live-smoke 或任何真实 AI 服务调用。

检查基线 diff 确认：未修改 qnbot Core、Models/MessageEnvelope.cs、Models 契约、Adapter、Integration、Extension/QnbotAgentAdapter.cs、原 AgentMessageIdentityProvider、AgentSessionManager、SQLite Store。未新增 AI、Intent、Knowledge 或 Reply 接入。

## 当前限制

- 标准化是一种 Observed 候选折叠规则。两个实际账号若仅以空白、末尾句读、大小写或 NFKC 兼容字符区别，可能得到相同候选键；此 PoC 无法证明或保证这类真实账号隔离，必须依靠可信原生身份解决。
- 标点仅处理指定末尾句读；内部标点、零宽非空白字符及其他 OCR 替换不纠错。消息正文和时间识别波动仍可能产生不同 MessageId。
- 标准化应用在 AgentContextPipeline 与 Context 采集昵称 Hash 前。直接使用底层 Manager / Provider 的旧 API 仍是精确身份行为；不应绕过 Context 入口来摄取待标准化昵称。未来原生平台 ID 必须保持 opaque，不应套用昵称规则。
- 未迁移或合并旧库中的未标准化候选与旧昵称 Hash。规则改变的既有标签可能形成新观察 Session；本轮使用独立合成数据库。切换版本前需单独设计显式迁移/冲突方案，不能自动猜测合并。
- 真实采集仍受原 SessionRegistry.ReadBuyer 的一致性判定限制：若它先返回 null，标准化层不会把混合头部猜测成一个买家；标准化不能解决上游全部识别问题。
- 本轮是合成多买家隔离验证；两位真实买家、跨店铺和多进程协同仍未验收。继续沿用既有 Session 有界容量、生命周期和单 Manager 持久化限制。

## 下一步建议

在独立测试库中安排两位受控买家的真实 OCR 切换验收，覆盖昵称空白/末尾句读波动、重放和恢复。后续单独引入可信 Buyer/Conversation ID 与规则版本/冲突处理；继续保持 Observed 和 Context-only，AI / Intent / Knowledge / Reply 由后续明确阶段授权。
