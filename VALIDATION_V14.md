# V1.4完成报告

## 1. 修改原因

V1.3真实测试发现：首条显示时间早于本机基线导致漏触发；全角/半角问号导致4条重复入队；
原去重不跨进程保存，缺少每个买家的独立Session和派发恢复状态。
V1.4关注输入可靠性，不处理AI质量，不修改qnbot核心或真实客服行为。

当前实际工作区为qnocr-poc，起点 `ce4544eb5f03b12e73e9c234b6d50ca20a4e3ecf`。
未找到另一个独立qnbot-agent仓库或指定的STATUS V1.2文件，本次沿用已验证的外部宿主入口。
开始修改前已输出原因、范围、风险、回滚方式及文件计划。

## 2. 修改文件

- `Models/MessageFingerprint.cs`：宽度与空白归一化，原文保留；每Session稳定MessageId。
- `Messages/BuyerMessageStructurer.cs`：可靠消息头OCR框轻微重叠容差，保留原安全证据约束。
- `Models/MessageSession.cs`：外层会话和逐消息生命周期记录。
- `Shadow/NewMessageTracker.cs`：2分钟有界时差容忍、初始源时间基线与内存上限。
- `Shadow/SessionRegistry.cs`：OCR买家标签分Session；隔离测试身份命名空间；当前商品上下文。
- `Shadow/DurableShadowDispatch.cs`：原子持久待处理记录、单写者锁、成功去重、安全重试及不确定结果保留。
- `Shadow/NativeWorkerClient.cs`：截图/OCR子进程超时隔离，不结束Agent。
- `Shadow/QnocrShadowRunner.cs`：将上述扩展接入原管线；重发现窗口、UIA降级、退避、健康和生命周期日志。
- `Shadow/ShadowAdapterClient.cs`：可证明未启动的失败分类，以及原协调器Shadow入口回执验证。
- `Program.cs`：原生工作进程入口、持续/限时运行、状态目录选项。
- `Validation/QnbotCompatibility/Program.cs`：原样协调器的无AI/无发送记录回调入口。
- `Validation/ChangeDetectionChecks/MessageChecks.cs`、`ShadowChecks.cs`、`StabilizationChecks.cs`、`Program.cs`：回归与故障注入。
- `Validation/QnbotCompatibility/Summarize-Stability.ps1`：实测计数审计。
- `Validation/Replay-V13ForV14.ps1`、`Dispatch-ReplayV14.ps1`：上轮真实OCR证据的离线去重及实际协调器回归，不冒充本轮实时消息。
- `README.md`、本报告：运行、边界和回滚说明。

## 3. 架构影响

既有MessageEnvelope schema v1、OCR/布局算法和qnbot内部业务流程不替换。
SessionId、MessageId和交付状态作为外层元数据保存；原始文本、源时间和当前商品ID仍按原契约进入Adapter。
原qnbot去重算法不改，新增规范化与持久记录只阻止重复进入Adapter。

默认Shadow现在进入原样编译的BuyerMessageBurstCoordinator，再到仅记录回调，AI OFF/Send OFF。
测试BuyerId/昵称按OCR买家标签隔离并明确标记为测试身份，不伪装真实平台身份。
原生操作通过本任务自建子进程执行，超时只终止该工作进程；目标千牛窗口不自动点击、发送、恢复或关闭。

安全恢复：窗口丢失/最小化等待相同标题目标恢复；UIA失败降级；OCR失败重建图像基线但保留会话去重；
Adapter明确未启动时退避重试，超时/未知结果保留Uncertain，不自动重放。状态损坏也不覆盖后重发。
不宣称跨任意崩溃窗口的恰好一次投递；Unknown/Uncertain需要可核查的后续处理。

回滚：停止本项目Shadow，回滚下方实现提交并重建；报告可保留。保留Output/State审计记录，不修改或删除qnbot数据。

## 4. Commit ID

实现Commit ID：`1b8137b3b98629b5de94a58dfb607a10700e3261`（19个文件）。本报告独立提交。
运行二进制哈希记录在 `Output/v14-soak-build-hashes.json`，最终验收结束后复核未变化。

## 5. 测试结果

### 编译与自动检查

主程序构建0警告/0错误；隔离net48宿主0错误，保留原协调器CS4014警告。
`Output/v14-build.log`、`Output/v14-checks.log`：213项检查通过。
其中包括标点宽度归一化、型号不模糊修正、1秒时差、不同源秒重复文本、买家隔离、商品变化不重发、
持久完成项重启不重发、宿主不可用后重试成功、未知结果不盲重放、损坏/崩溃中间状态保留、OCR失败恢复、UIA降级和截图超时后继续。
故障注入测试与真实窗口实测分别记录，模拟超时不冒充真实千牛发生过挂起。

### 连续Shadow

运行命令：

```powershell
dotnet run --no-build -- --shadow --minutes 31 --identity Validation/QnbotCompatibility/identity_mapping.json --state-dir Output/State/V14-acceptance
```

最终会话：`Output/Shadow/shadow_20260925_145530_752_810b515f7b034726b8c552ffa22671a0`。
日志：`Output/v14-live-final.log`。14:55:30开始，实际运行 **1861.6708753秒（31分1.67秒）**，自动完成，退出码0。
用户已确认“最终版十条已发送”，10条均经真实窗口/OCR进入原协调器，未用离线JSON替代。

| 项目 | 最终结果 |
| --- | --- |
| 采集尝试 / 成功 / 失败 | 1021 / 1021 / 0 |
| 商品OCR / 聊天OCR | 各28次，静止帧不重复OCR |
| 受控测试发送 / 新增 / Adapter成功 / Dispatch成功 | 10 / 10 / 10 / 10 |
| 受控测试重复 / 误触发 / 漏检 | 0 / 0 / 0（仅本轮可见窗口样本） |
| 其他可见买家真实新消息 | 2条，进入另一独立Session，并非测试消息重复 |
| 总新增 / Adapter成功 / 原协调器接收 | 12 / 12 / 12 |
| 每条派发尝试 | 均为1次 |
| 已抑制重复观察 | 34次，没有变成第二次Adapter调用 |
| 最终积压 / 不确定结果 / 日志写入异常 | 0 / 0 / 0 |
| 运行期捕获/派发异常恢复事件 | 0；故障恢复另由注入测试覆盖 |
| AI调用 / 发送 / 发送边界违规 | 0 / 0 / 0 |
| 买家Session | 2个，测试买家10条始终同一个Session |
| 健康日志 | 31条；主Agent工作集采样约75.5–90.4 MiB |

工作集只针对主Agent采样，不含OCR子进程峰值；31分钟不等于无限期稳定证明。
原生qnbot对象逐条核对保留了原始OCR正文，原MessageEnvelope schema v1未改。
每份回执均为ShadowDispatchOnly、CoordinatorCreated=true、CoordinatorCallbacks=1、SourceFilesVerified=6，禁止业务调用0。

### 10条受控实时消息

全部ProductId为 `1051087533910`，归属同一外层Session；完整ID、CaptureTime和原生对象保存在本地生命周期日志及outbox。
价格问题的半角问号按NFKC等价核对，未改写OCR原文。

| 买家消息 | 源显示时间（2026-09-25） | 新增帧 | Adapter / Dispatch | 尝试 |
| --- | --- | ---: | --- | ---: |
| 这款有哪些规格？ | 14:59:04 | 154 | Converted / Accepted | 1 |
| 容量是多少？ | 14:59:09 | 155 | Converted / Accepted | 1 |
| 是什么材质的？ | 14:59:26 | 161 | Converted / Accepted | 1 |
| 这款多少钱? | 14:59:38 | 165 | Converted / Accepted | 1 |
| 今天下单多久发货？ | 14:59:54 | 172 | Converted / Accepted | 1 |
| 可以刻字吗？ | 15:00:08 | 177 | Converted / Accepted | 1 |
| 适合户外露营使用吗？ | 15:00:22 | 183 | Converted / Accepted | 1 |
| 跟不锈钢款有什么区别？ | 15:00:43 | 192 | Converted / Accepted | 1 |
| 刻字后不喜欢能退吗？ | 15:00:59 | 199 | Converted / Accepted | 1 |
| 这个怎么样？ | 15:01:09 | 202 | Converted / Accepted | 1 |

额外2条来自另一个实际可见买家，SessionId、测试路由昵称及商品ID与受控买家分开，未串入受控10条统计。
其原文、账号和截图只保留在Git忽略的Output，不在报告公开。
状态目录还包含先前诊断轮2条已完成消息，它们未被重启重发，不能把目录总数14误报为本轮新增14。
证据：最终会话 `summary.json`、`health.jsonl`、`lifecycle.jsonl`、`stability-audit.json`；
`Output/State/V14-acceptance/outbox/<MessageId>/attempt_0001/` 保存原生消息和回执。

前一轮诊断会话 `shadow_20260925_141911_282_b04a75b84e4b45d1807671ac3f42b37b` 运行573.21秒、380帧，没有进程崩溃，
但窗口缩放变化暴露子进程DPI问题：通过dotnet.exe启动DLL未使用本应用PerMonitorV2清单，截图1310×857截断了物理窗口。
因此该轮**不计入30分钟通过时长**。工作进程改用本应用apphost后，2帧复验恢复1638×1071完整截图，UIA尺寸匹配。
然后冻结二进制重新开始最终31分钟测试。证据：`Output/v14-dpi-diagnostic.log`、`Output/v14-dpi-check.log`。

后续诊断会话 `shadow_20260925_143004_882_10239ba3fa99420783e393df14172914` 运行1420.07秒、764帧，进程无异常退出，
但首次消息测试时画面主要停留在历史，未覆盖早期新问题；最终可见3条中2条派发，1条因姓名/时间OCR框重叠9像素被保守拒绝。
不把未可见的消息算作已捕获，也不把该轮凑入最终31分钟。
仅将外层头框容差从固定3像素改为最多半行高度，保留唯一灰色姓名、同一行、完整时间和原置信度要求；
增加9像素可接受、30像素仍拒绝的正反例。真实失败截图复验后3条均可结构化，见 `Output/v14-header-recheck.log`。
随后再次从零运行最终31分钟，期间不再修改运行二进制。

### V1.3真实证据回放回归

`Output/v14-v13-replay.json`：V1.4比较器对上轮真实OCR快照返回11条独立消息（原10条+1次真实规格补发），
不是原来的14条带标点重复的已派发条目；早于当时本机基线1秒的首条也得到保留。
`Output/ValidationV14Replay/dispatch_20260925_143814/summary.json`：这11条回放消息全部进入原协调器记录回调，AI/发送均0。
这一结果验证已知回归缺陷修复，不等于本轮重新从买家端发送了11条。

### qnbot回归

`Output/v14-core-regression.log`：既有17项Adapter检查通过，2条输入进入实际协调器，
重复重放后的回调总数仍为1，Session generation为1/2，禁止业务调用0。
真实qnbot源码仍为 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，工作区未修改。
这确认了所覆盖的入口/去重回归；遵守Shadow限制，没有运行真实自动客服或发送，不能称为全部生产功能实发验收。

## 6. 当前状态

V1.4外层稳定化实现与本轮验收完成：最终31分钟运行及10条受控实时测试通过，所有本轮进程已停止。
新增元数据位于外层Session/生命周期/状态文件，原MessageEnvelope和qnbot核心不改。

边界：验证的是原样编译的隔离qnbot协调器记录回调，不是生产Bot完整收发链。
OCR标签不是平台身份，首次Session快照只建基线；未显示在当前聊天区域或证据不可靠的消息不能假定已捕获。
不承诺全量后台消息获取或任意故障下恰好一次投递。前两轮诊断结果保留，不混入最终验收计数。

## 7. 下一步建议

本次不扩展架构或AI质量。后续应单独处理可信平台身份、同秒同文消息区分、昵称OCR漂移、
最近历史滚入的人工核查、Uncertain回执对账，以及持久状态/截图的磁盘归档策略。
2分钟容差是受限POC策略，不是对平台时钟的标定；未确认身份不自动路由。
