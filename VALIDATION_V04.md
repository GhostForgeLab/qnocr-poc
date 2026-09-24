# V0.4 变化检测与变化区域 OCR 验证

日期：2026-09-24。基于 V0.3 `9a949c7c42f0fa39720bb3246be7eb3ec393bdfd`。

## 实现范围

- `ChatRegionChangeDetector.Detect(previous, current)` 比较相同尺寸的两张图，返回
  Changed / DifferenceRatio / ChangedRectangle、差异像素数、图像尺寸及检测耗时。
- 单像素最大 BGR 通道差必须大于24；至少20像素且差异比例≥0.001才触发。
  无显著变化时 ChangedRectangle=null，不进行 OCR。
- 变化包围框映射为全聊天宽度、上下各12px上下文的横带，对当前帧的该横带执行 PaddleOCR。
- `--compare previous.png current.png` 用于离线双图验证。
- `--watch [--frames N]` 用于接待中心监测：首帧建基线、随后相邻帧比较，
  处理完等待1000ms再采样，可用 Ctrl+C 停止。没有并发 OCR 或后台自动任务。
- 无参数 `dotnet run` 保留 V0.3 单次全聊天区域 OCR，避免默认启动无限监测。

## 构建与确定性检查

.NET SDK 8.0.425，运行时 .NET 8.0.31，Windows x64。
主程序与 `Validation/ChangeDetectionChecks` 一起严格编译：**0 警告、0 错误**。
新增检查项目首次离线还原出现 NuGet 审计连接警告，联网强制刷新还原后严格编译通过。
最终日志 `Output/v04-build.log`、`Output/v04-checks.log`。

20项检查全部通过，覆盖：完全相同帧、已知100像素差异及精确包围框、通道阈值24/25、
单像素噪声、20像素与0.001比例边界、全图变化、低于比例阈值、右下边缘、
尺寸不一致、缺失/损坏图片、非法阈值、无变化不生成事件且不调用 OCR、
上下文裁剪边界、A→B→B仅调用一次OCR、OCR异常传播/保存失败状态、
新增文字区域不含历史消息。

其中调用次数检查使用回调计数，没有模拟成真实 OCR；下一节另用实际 PaddleOCR 验证。

## 双图 → 变化横带 → PaddleOCR

测试图为程序绘制的受控样本，**不是实际收到的新消息**：

- 前帧：`Output/ValidationV04/previous_message.png`，654×537，仅含历史文字。
- 当前帧：`Output/ValidationV04/current_message.png`，保留历史文字，在下方增加
  `新消息: TA1 200ml 304 F2U 还有货吗?`。
- 模式：`dotnet run --no-build -- --compare 前帧 当前帧`，退出码0。

```text
Changed: True
DifferenceRatio: 0.006256
DifferentPixelCount: 2197 / 351198
ChangedRectangle: X=27, Y=184, Width=361, Height=19
OCR context rectangle: X=0, Y=172, Width=654, Height=43
```

变化事件目录：
`Output/Changes/change_20260924_224230_136_29ebb040ed2842b2b43cbc536b053751/`。
其中 `previous.png` / `current.png` 保留输入，`changed_region.png` 是实际 OCR 输入，
`change.json` 保存差异数据、两种坐标框、坐标偏移及 OcrCompleted 状态。
裁剪图已目视检查，包含新增文字、不包含历史文字。

真实 PaddleOCR 输出（不做文本补全/纠正）：

```text
新消息:TA1200ml304F2U还有货吗?
```

型号和中文可读，空格丢失仍是 V0.3 已知误差。
OCR 结果：`Output/OCR/changed_region_20260924_224231_494.txt` / `.json`。
运行日志：`Output/v04-changed-ocr.log`。

再次比较相同当前帧：Changed=false、DifferenceRatio=0、
`OCR skipped: no significant pixel change.`，退出码0。
日志：`Output/v04-unchanged.log`。

## 面积与性能

| 项目 | 全图基准 | 变化横带 |
| --- | ---: | ---: |
| OCR 输入 | 654×537 | 654×43 |
| OCR 输入像素 | 351198 | 28122 |
| 模型初始化 | 616.0 ms | 652.7 ms |
| OCR 推理 | 759.9 ms | 628.6 ms |
| OCR 总耗时 | 1387.2 ms | 1283.8 ms |
| 进程峰值工作集 | 414.1 MiB | 393.0 MiB |

横带比全图减少约91.99%的像素；该次变化检测耗时18.4ms（含解码），相同帧比较14.2ms。
全图基准通过 `--ocr current_message.png` 取得，会同时输出历史和新增文字；
日志 `Output/v04-full-fixture-ocr.log`。
这些是各一次运行，不是性能统计；模型仍每次初始化，不能将面积减少等同于同等幅度的加速。
主要收益是无变化时完全跳过OCR，以及局部新增文字时不再识别框外的历史内容。

## 真实窗口监测状态

已尝试 `dotnet run --no-build -- --watch --frames 3`，发现接待中心实际变为 **1638×949**，
不符合 V0.2 经确认的 **1638×1100** 固定布局。程序在首帧验证时停止，退出码1：

```text
Watch stopped: Failed; Frames=0; ChangedFrames=0; OCR calls=0
Watch layout requires 1638x1100; captured 1638x949. Recalibration required.
```

这验证了尺寸变化不会触发错误区域OCR，但不是成功的实时监测验收。
日志：`Output/v04-watch-size-mismatch.log`。
已请求用户手动恢复尺寸；正常尺寸下的有限帧实时监测及真实消息到达效果尚待补验。
本次没有遗留运行中的监测进程。`--watch --frames 0` 参数检查返回退出码2。

## 局限

像素变化不等于新消息。滚动、切换会话、头像/表情动画和已读状态变化也会触发；
多处变化合并为单一包围框，全局变化可能再次识别整张聊天图。
OCR 横带为保留文字上下文包含少量未变化像素，不能保证其中所有文本都是新增内容。
没有去抖、滚动补偿、文本去重或消息角色判定；小变化/逐帧渐变可能被阈值忽略。
监测会覆盖两张临时聊天帧，完整变化事件保留用于核验，长期运行需另行管理磁盘空间。
未接入 qnbot、知识库、AI接口、自动回复、点击或发送。
