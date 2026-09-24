# qnocr-poc V0.4

独立的千牛视觉采集实验：发现千牛窗口 → 捕获完整窗口 → 固定坐标裁剪 → chat_region 单次 OCR。
使用 C# / .NET 8 / Windows；未接入或修改其他项目。

## V0.4 变化检测模式

新增 `Detection/ChatRegionChangeDetector.cs` 和 `Models/ChangeDetectionResult.cs`。
新的监测模式只在聊天图发生显著变化时提取变化区域并 OCR；默认不带参数的命令保留 V0.3 单次完整流程。

```powershell
# 持续监测，Ctrl+C 停止
dotnet run -- --watch

# 有限采样，含首帧基线；本次实测采用此模式，不遗留后台监测
dotnet run -- --watch --frames 3

# 两张聊天区域截图离线比较：有变化则裁剪并 OCR，无变化则跳过
dotnet run -- --compare previous_chat.png current_chat.png
```

监测绑定启动时找到的可见接待中心 HWND，使用 V0.2 的 1638×1100 固定布局。
每次处理完成后等待 1000ms 再取下一帧，不并行运行 OCR，不承诺固定每秒帧数。
首帧只建基线，不识别历史；随后比较相邻成功帧，每帧处理后更新上一帧。
窗口关闭、最小化、捕获失败、尺寸不符或 OCR 失败时停止并报错，不自动切换到其他窗口。
Ctrl+C 在当前不可取消的捕获/OCR 操作结束后停止；`--frames N` 按成功捕获帧数计数，N 必须大于 0。

像素差定义：对每个像素取 B/G/R 三通道绝对差的最大值，大于 **24** 的像素计入差异。
`DifferenceRatio = 差异像素数 / 全图像素数`，必须同时满足 **至少20像素** 和 **比例≥0.001** 才返回 Changed=true。
透明通道不参与比较，不缩放、不降采样。阈值可通过检测器构造参数调整，当前 CLI 使用上述默认值。
检测器返回 Changed、DifferenceRatio、ChangedRectangle、像素数、尺寸和耗时；
无显著变化时 ChangedRectangle=null，但仍报告实际差异比例。不同尺寸或无效图会抛错。

ChangedRectangle 为差异像素的最小包围框，相对聊天截图左上角。
实际 OCR 裁剪为包围框 Y 范围上下各扩12px、宽度覆盖整个聊天区域的横带，边界裁到图内，
以尽量保留完整文字行。精确差异框与 OCR 上下文框分开记录，不混淆两者。
OCR 文本框坐标相对裁剪图；加上 change.json 中 OcrBoundsOriginInChat 即可映射回聊天图。

变化样本保存到 `Output/Changes/change_*`（离线）或 `Output/Changes/session_*/change_*`（监测）：
`previous.png`、`current.png`、`changed_region.png`、`change.json`。
JSON 状态区分 PendingOcr / OcrCompleted / OcrFailed；OCR TXT/JSON 仍保存到 `Output/OCR`，路径打印在 Console。
无变化不保存事件图、不调用 OCR；监测只循环覆盖两张临时聊天帧，成功裁剪后删除临时完整窗口截图。
变化事件保留用于验收，长期运行时会占用磁盘；本版没有事件保留期限或后台服务。
每个监测会话还保存 summary.json，记录帧数、变化次数、OCR 调用次数及退出状态。

**像素变化不等于新消息。** 滚动、切换会话、头像/表情动画、已读状态都会触发；
同一横带的未变文字可能再次进入 OCR，多处分散变化会被一个包围框合并。
全局变化可能使横带覆盖整张聊天图，本版不尝试猜测哪部分属于新消息。
低于阈值的微小修改、逐帧渐变可能漏检；没有稳定帧去抖、滚动补偿、消息身份识别或文本去重。

验证命令及报告：

```powershell
dotnet run --project Validation/ChangeDetectionChecks/ChangeDetectionChecks.csproj
dotnet run -- --compare Output/ValidationV04/previous_message.png Output/ValidationV04/current_message.png
```

检查程序无额外测试框架依赖，生成确定性帧对并验证差异框、阈值、边界和 OCR 调用门控。
详见 [VALIDATION_V04.md](VALIDATION_V04.md)。

## 运行

在 Windows 交互桌面安装 .NET 8 SDK，展开 1638×1100 的千牛接待中心，在项目根目录运行：

```powershell
dotnet run
```

本次验证机器原先只有 .NET 10 SDK，因此另下载了微软官方 .NET 8.0.425 SDK 到
本项目 `.tools/dotnet`，并按微软发布元数据核对 SHA512。使用本地 SDK 的终端先执行：

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\cli-home'
dotnet run
```

`.tools` 不进入 Git；其他机器需要自行安装 .NET 8 SDK。
必须在千牛所在用户的交互桌面运行；受限沙箱或服务会话可能无法枚举窗口。
本次沙箱内 EnumWindows 失败，在允许访问用户桌面的运行环境中成功。

默认 `dotnet run` 保留 V0.3 行为，在捕获、裁剪成功后对整个 `chat_region.png` 执行一次 OCR。
需要仅识别变化区域时使用上面的 `--watch` 或 `--compare`。
直接识别图片，无需千牛运行，也不要求固定尺寸：

```powershell
dotnet run -- --ocr Output/Regions/chat_region.png
```

首次运行需联网还原 NuGet 依赖；依赖及本地模型缓存到项目 `.tools/nuget`，
不使用 Python，不上传图片，也不调用外部 OCR/AI 接口。提交的 `packages.lock.json` 锁定依赖，
可用 `dotnet restore --locked-mode` 重建。平台为 Windows x64；原生运行库需要 Visual C++ x64 运行时。

也可以仅裁剪已有完整截图（该模式不执行 OCR）：

```powershell
dotnet run -- --extract Output/qianniu_20260924_221542.png
```

## PaddleOCR 单图识别

`OCR/PaddleOcrService.cs` 使用 PaddleSharp（社区 C# 封装）的本地 PP-OCRv5 中文模型，
调用 Paddle Inference CPU MKL。`Models/OcrResult.cs` 返回文本行、置信度、边界框及性能数据。
固定依赖：Sdcb.PaddleOCR / Models.Local 3.3.1，PaddleInference 3.3.1，
Windows MKL runtime 3.3.1.70，OpenCvSharp 托管/原生包 4.11.0.20250507。

- 每次 OCR 调用新建引擎，处理一张图后释放；V0.4 通过外层检测避免无变化时调用，引擎暂不复用。
- 截图中的水平文字关闭旋转/180°分类；其他方向和复杂文档布局不在本版验收范围。
- 输出按检测框中心 Y、X 排序，不推断买家/客服角色，不补全或修正模型输出。
- 舍弃模型返回的空文本框；无效分数转为 null，平均置信度仅计算有效分数。
- 空白图正常返回 `No text detected`、Confidence `N/A`，仍表示推理成功；不是识别出了文本。
- Confidence 是模型分数，不是准确率；昵称、头像/表情附近、标点和空格仍可能识别错误。
- Console 输出结果，另存 `Output/OCR/<图片名>_<时间>.txt` 与 `.json`；JSON 保存逐行分数、
  边界框、丢弃空框数和原始毫秒/字节指标。
- 时间区分模型初始化、检测+识别推理、总耗时（读图/解码+初始化+推理+结果整理），
  不含构建、模型下载、Console/结果文件写入和资源释放。
- 内存为当前进程的 OCR 前/后工作集和进程生命周期峰值（MiB），包含 .NET/原生库；
  不是托管堆大小，也不是 OCR 独占内存。测量“后”在引擎释放前。

可复现的中英数字/型号/容量/符号测试图：

```powershell
./Validation/Create-OcrFixtures.ps1
dotnet run -- --ocr Output/OCR/Fixtures/mixed_characters_18px.png
dotnet run -- --ocr Output/OCR/Fixtures/mixed_characters_28px.png
./Validation/Measure-OcrAccuracy.ps1 -OcrJson Output/OCR/实际结果.json -GroundTruthJson Output/OCR/Fixtures/mixed_characters_18px.ground-truth.json
```

夹具使用 Microsoft YaHei 字体，18px/28px；脚本同时保存准确的参考文本。
字符错误率使用 Levenshtein 编辑距离，分别报告保留空格和仅去除空白的口径，
保留大小写及标点差异。实测、错误样例与性能详见 [VALIDATION_V03.md](VALIDATION_V03.md)。

技术来源：[PaddleSharp OCR](https://github.com/sdcb/PaddleSharp/blob/master/docs/ocr.md)、
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)。

## 固定区域布局

`Models/QianniuLayout.cs` 定义四个 Rectangle；`Capture/QianniuRegionExtractor.cs`
从完整 PNG 逐像素裁剪，不缩放、不做 OCR、AI 或视觉检测。

原任务的 2226×1253 是工作台尺寸。重新捕获接待中心发现实际为 1638×1100，
经用户明确选择，V0.2 改按该实际尺寸人工校准。坐标相对 PNG 左上角 (0, 0)，单位为物理像素；
右/下边界不包含在区域内，即 [X, X+宽) × [Y, Y+高)。

| 属性 | X | Y | 宽 | 高 | 输出 |
| --- | ---: | ---: | ---: | ---: | --- |
| BuyerListRegion | 80 | 120 | 300 | 980 | buyer_list_region.png |
| ChatRegion | 380 | 181 | 654 | 537 | chat_region.png |
| ProductRegion | 1034 | 120 | 604 | 924 | product_region.png |
| InputRegion | 380 | 718 | 654 | 382 | input_region.png |

买家列表包含搜索、列表筛选和会话列表，不含蓝色导航栏。
聊天区域包含当前可见的消息历史，不含聊天对象标题和输入工具栏。
商品区域包含右侧标签页和商品/订单面板，不含底部客服工具栏；只截当前显示内容。
输入区域包含编辑区、输入工具栏以及关闭/发送按钮；不触发任何交互。

四张 PNG 写入当前目录的 `Output/Regions/`，再次运行会替换同名区域图，原图不变。
提取前检查 PNG 格式、准确尺寸、全部区域边界以及源文件/输出路径冲突。
尺寸不匹配或坐标越界会失败，且不写入任何新区域图；上一次的区域图可能仍在目录中，
应以本次成功日志判断输出是否更新。布局变动或 DPI 变化后需人工重新校准，程序不自动识别页面。

## 窗口发现与选择

- 按 `AliWorkbench` / `AliRender` 进程名查找，不硬编码 PID 或 HWND。
- 记录进程名、PID、Process.MainWindowHandle；使用 EnumWindows 枚举其顶层窗口。
- 区分进程 MainWindowHandle 与实际捕获 HWND，因为一个进程可能有多个窗口。
- 记录窗口标题、物理像素边界、可见/最小化/DWM cloaked 状态。
- 自动选择可见、非最小化、非 cloaked 的窗口，优先标题包含“接待中心”的窗口，再按面积排序。
- 默认显示可见窗口；`dotnet run -- --list` 显示全部匹配窗口，不截图。
- 多店铺/多窗口时，可用枚举结果指定：`dotnet run -- --hwnd 0x实际句柄`。
  句柄仍须属于本次发现的千牛进程。

## 捕获方案与局限

本版采用用户允许的 Windows API 方案：`PrintWindow(hwnd, hdc, 0x2)`
（PW_RENDERFULLCONTENT），保存整个窗口，无桌面区域截取降级。
使用 PerMonitorV2 DPI 感知，Bitmap/Graphics/HDC 均有对应释放操作。
拒绝最小化窗口，不自动恢复、不置前、不点击；PrintWindow 超过 15 秒则返回失败并退出程序。

WGC 已评估，本版未实现或实测：其接入还需 WinRT/COM、Direct3D 设备、帧池和 GPU 帧读回，
为控制 V0.1 范围先验证 PrintWindow。官方资料：
[WGC CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)、
[Direct3D11CaptureFramePool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool)、
[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)。

PrintWindow 是同步调用，依赖目标应用绘制；API 成功不等于 CEF/GPU 内容完整。
程序对采样像素进行近黑、未绘制洋红底色和单色检查；明显异常时保存诊断 PNG 但返回失败。
这些检查仅发现明显空白，不能识别局部黑屏、缺失聊天或缺失商品卡片；仍须目视检查。
`Capture Success` 仅表示 PNG 已保存且通过基础像素检查。
程序不会把像素检查冒充 CEF/聊天语义验证。

## 输出与验收

截图：当前工作目录的 `Output/qianniu_yyyyMMdd_HHmmss.png`。
同一秒文件名冲突时增加毫秒和唯一后缀，避免覆盖已有样本。
退出码：0 完整流程成功（或仅 OCR/提取/列举成功），1 捕获/裁剪/OCR 失败，2 参数错误。

保留运行日志：

```powershell
dotnet run 2>&1 | Tee-Object -FilePath Output/run.log
```

逐张人工确认：聊天区域边界、买家消息文字、商品卡片、CEF 页面内容、黑屏/空白。
当当前页面无聊天/商品卡片时，必须记为未验证。
截图和原始日志保留在本地 Output，不进入 Git。
历史实测：V0.1 [VALIDATION.md](VALIDATION.md)，V0.2 [VALIDATION_V02.md](VALIDATION_V02.md)。

范围止于 V0.4 像素变化检测和变化区域 OCR；不包含 AI 接口、Knowledge V2、SalesAgent、
自动回复、自动点击/发送、DLL 注入、进程内存读取、UI Automation 或 qnbot 集成。
