# V1.1 真实千牛 OCR Shadow 验收

日期：2026-09-25。基于 V1.0 `d32e2fc3cd0ff2bd080f7e31cb9a8fbd94274630`。
结论：真实千牛截图 → 本地OCR → 确认买家消息 → MessageEnvelope → 隔离qnbot Adapter → Shadow日志已通过。
按用户确认使用现有测试身份映射；不是真实账号映射，也没有连接运行中的qnbot。

## 实现

- `Shadow/QnocrShadowRunner.cs`：串行循环、窗口/布局解析、商品像素缓存、聊天变化判断、消息结构化、事件留档。
- `Shadow/ShadowAdapterClient.cs`：只启动独立兼容宿主 `--shadow-convert` 分支；子进程20秒超时，取消时结束本任务创建的子进程。
- `Validation/QnbotCompatibility/Program.cs`：新增纯转换分支，复用V1.0 Adapter和原样编译的qnbot模型/消息键代码。
  不构造Coordinator、不执行普通入口、不注册消费回调；运行时和业务调用计数必须为0。
- `Program.cs`：`--shadow` 连续模式，支持 `--frames`、`--interval-ms`、`--identity` 和Ctrl+C。
- `CaptureResult.TimedOut` 明确传递PrintWindow超时；Shadow立即终止，避免不断创建挂起的捕获任务。
  普通帧失败仍记录、清空状态，并允许下一帧重建基线。

首帧是历史快照，后续变化使用完整聊天区域OCR以保留时间/角色证据。
本版不把快照称为新消息，统一记录 `IsNewMessage=false`；不进行跨快照去重或买家切换验证。

## 编译与检查

| 验证 | 结果 | 本地日志 |
| --- | --- | --- |
| .NET8 主程序 `dotnet build --no-restore -warnaserror` | 0警告、0错误 | `Output/v11-build.log` |
| .NET4.8兼容宿主 Rebuild | 0错误，1条原qnbot源码CS4014警告（Coordinator第744行，与V1.0相同） | `Output/v11-host-build.log` |
| 回归检查 | 163项通过（既有148项 + 新增15项Shadow检查） | `Output/v11-checks.log` |

Shadow自动检查包括：四帧A-A-B-B仅两次聊天OCR/一次商品OCR；真实隔离Adapter转换；
首帧/变化/静止标记；测试身份/非新消息标记；六份源码校验与零运行时初始化；
未知身份拒绝且不猜ID；失败重建基线和商品缓存；聊天矩形变化重建基线；
连续循环取消及摘要保存；非法帧数、预取消和捕获超时停止。
测试截图与OCR行证据为明确标注的合成数据，不作为Paddle准确率证明。

## 真实千牛有限帧运行

最终版本运行命令（退出码0）：

```powershell
dotnet run --no-build -- --shadow --frames 3 --identity Validation/QnbotCompatibility/identity_mapping.json
```

运行日志：`Output/v11-live-shadow.log`。
验收目录：`Output/Shadow/shadow_20260925_105323_932_354133470d1e44f99d217cc87944b8c1/`。
原始账号标题、买家文字和截图仅存本地Output，本报告不复制这些内容进Git。

```text
frame=1 BaselineSnapshot; product=NotFound; productCache=False; messages=2; adapter=Converted
frame=2 Unchanged; product=NotFound; productCache=True; messages=0; adapter=
frame=3 Unchanged; product=NotFound; productCache=True; messages=0; adapter=
Status=Completed; Attempts=3; SuccessfulFrames=3; FailedFrames=0
ProductOcrCalls=1; ChatOcrCalls=1; Envelopes=1; ConvertedEnvelopes=1; DeferredEnvelopes=0
```

静止帧的messages=0表示未再运行聊天OCR，不表示屏幕没有消息。
首帧识别出2条具有完整时间证据的买家消息，生成1个Envelope并转成2个真实 `QNChatMessage` 对象。
消息时间仍保留截图中的历史时间，没有改为观察时间。
商品OCR执行成功但未发现符合规则的商品ID，因此Envelope的ProductId为空，ProductBinding=None；未虚构映射成功。

截图1638×949；聊天裁剪655×459。BuyerList/Chat/Input采用视觉分隔线，Product采用UIA Document几何边界。
目视核对聊天样本，两条确认买家文本与截图一致，蓝色客服气泡未被输出为买家消息；
其他未确认内容保存在ReviewItems。该小样本不代表一般准确率，也没有观察到真实新消息到达。
真实变化路径由确定性回放检查覆盖，本次真实3帧主要证明首帧贯通和静止帧跳过OCR。
聊天OCR总耗时约1714ms（含初始化），识别约1121ms；该调用结束时进程历史峰值约706MiB。
OCR平均置信度约0.83，仅为模型输出分数，不是准确率。

核验文件（均在上述验收目录）：

- `session.json`：启动窗口及身份标签。
- `shadow.jsonl` / `summary.json`：逐帧诊断及最终计数。
- `frame_000001/window.png` / `chat_region.png` / `product_region.png`：本轮截图。
- `frame_000001/chat-ocr.json` / `buyer-messages.json` / `envelope_001.json`：完整证据链。
- `frame_000001/adapter_001/qnbot-messages.json`：转换后的qnbot原生消息对象。
- `frame_000001/adapter_001/shadow-adapter-result.json`：转换回执。

```json
{
  "Mode": "ShadowConversionOnly",
  "Status": "Converted",
  "ConvertedMessages": 2,
  "CoordinatorCreated": false,
  "ForbiddenBusinessCalls": 0,
  "RuntimeInitializers": 0,
  "IsTestIdentity": true,
  "SourceFilesVerified": 6
}
```

## 边界与完成状态

qnbot仍为 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，工作区干净；SalesAgent、Knowledge V2、Prompt和Reply Chain未改动。
UIA只读取布局几何，不读消息。无AI接口、自动点击、自动发送、注入或消息传输。
未知身份的实测回放回执为Rejected，循环摘要CompletedWithIssues，原Envelope的身份字段仍为空。
测试映射并未验证当前买家身份，所有转换仅用于隔离日志；商品与历史消息的关联同样未经确认。
V1.1完成后停止，所有本轮有限帧进程已退出。未进入真实业务消费或自动回复阶段。
