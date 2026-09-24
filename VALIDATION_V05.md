# V0.5 千牛比例布局验证

日期：2026-09-24。基于 V0.4 `72eaa224c996fd1041591a50619171f7f27e181e`。

## 修改结果

新增 `Models/QianniuLayoutRatio.cs`，使用 BuyerList / Chat / Product / Input 的
Left、Top、Width、Height double 比例属性，均相对于完整截图。
原 `QianniuLayout` 只保留解析后的 Size/Rectangle，不再定义固定像素坐标或固定截图尺寸。
区域提取和监测统一调用比例模型的 `Resolve(Size)`。

四条边分别由比例乘当前尺寸后取整（MidpointRounding.AwayFromZero），
再相减得到宽高；原图像素不缩放、不重采样。
拒绝非有限数、负比例、非正宽高、越界、无效尺寸及取整后为空的区域；
所有区域校验通过后才写PNG。

监测中完整窗口尺寸发生变化时重新解析区域、重建基线、跳过该帧OCR，下一帧恢复比较。
即使新旧聊天图尺寸恰好相等，也按完整窗口尺寸变化重建。
这是防止缩放误触发的策略；同一缩放帧到达的新消息也会被纳入新基线，无法保证捕获。

## 构建与检查

Windows x64，.NET SDK 8.0.425，实际运行时8.0.31。
主程序与检查项目严格构建：**0警告、0错误**。
`Validation/ChangeDetectionChecks`：**42项检查全部通过**，含V0.4的20项回归和V0.5的22项检查。

覆盖原始四区域坐标回归、949高度投影、819×550 / 1920×1080 / 2226×1253 / 1639×951的边界与共享边界、
非法比例与过小图片、四区域像素一致性、源图不变、错误配置不写部分输出、实际监测循环的尺寸切换回放。
其中非真实窗口尺寸只验证计算和裁剪性质，不能证明千牛在这些尺寸下的语义边界准确。

回放使用程序生成的帧，真实调用监测循环，不操作用户窗口：
1638×1100 → 1638×949 → 1640×949 → 1639×949 → 1639×949。
结果：Frames=5，BaselineResets=3，OCR calls=0，Completed。
包含“完整窗口尺寸改变，但解析出的聊天图尺寸不变”的情况。

日志：`Output/v05-build.log`、`Output/v05-checks.log`。

## 两种实际截图尺寸

| 区域 | 1638×1100：X,Y,W,H | 1638×949：X,Y,W,H |
| --- | --- | --- |
| BuyerList | 80,120,300,980 | 80,104,300,845 |
| Chat | 380,181,654,537 | 380,156,654,463 |
| Product | 1034,120,604,924 | 1034,104,604,797 |
| Input | 380,718,654,382 | 380,619,654,330 |

这些像素值是本次计算结果，不是生产代码中的布局配置。

历史1100高度截图 `Output/qianniu_20260924_223020.png` 提取成功，结果与V0.2像素坐标一致。
当前949高度截图 `Output/qianniu_20260924_225049.png` 提取成功，不再出现固定尺寸异常。
两组区域图分别保留在：

- `Output/ValidationV05/regions_1100_real/`
- `Output/ValidationV05/regions_949_real/`

日志：`Output/v05-extract-1100.log`、`Output/v05-extract-949.log`。

## 当前窗口完整流程

`dotnet run` 在1638×949的真实接待中心完成截图 → 四区域提取 → 聊天图OCR，退出码0。
最终完整截图：`Output/qianniu_20260924_225736.png`。
最终四张区域图：`Output/Regions/`，chat_region为654×463。
OCR结果：`Output/OCR/chat_region_20260924_225738_231.txt` / `.json`。
日志：`Output/v05-pipeline.log`。

可见正文 `好的，明天给你发顺丰特快` 和 `ok` 被识别；昵称仍有空格/大小写差异，
另有低置信度杂字符“電”。这是OCR输出，不进行自动纠正，也不宣称总体准确率改善。

`dotnet run --no-build -- --watch --frames 3` 同样在真实1638×949窗口完成：

```text
Window size: 1638x949; Chat rectangle: {X=380,Y=156,Width=654,Height=463}
Frame 1: Baseline established. OCR skipped.
Frame 2: Changed=False; DifferenceRatio=0; OCR skipped.
Frame 3: Changed=False; DifferenceRatio=0; OCR skipped.
Watch stopped: Completed; Frames=3; BaselineResets=0; ChangedFrames=0; OCR calls=0
```

日志：`Output/v05-watch-949.log`。
会话汇总：`Output/Changes/session_20260924_225738_a21fa14952fc4a558eb96f78ce2e89f3/summary.json`。
此实测解决了V0.4因1638×949而无法启动正常监测的问题；没有真实新消息到达或手动调整窗口过程的实测，
动态尺寸切换由上述回放验证。所有有限监测均已结束，没有遗留后台进程。

## 目视结果与局限

949高度的四张区域图已逐张检查：当前消息正文、买家列表、商品订单与输入框均可见，没有黑屏。
但千牛窗口内容并不全部等比缩放：标题栏和工具栏仍保留原先高度，聊天/编辑分界也非严格按比例移动。

- 聊天图顶部夹入约25px的标题区，边缘可见少量被截断的标题元素。
- 输入图上缘多包含少量聊天区空白；商品区域边缘带入少量相邻栏。
- 因此本次验证的是“比例坐标随尺寸计算、流程恢复运行”，**不是所有尺寸下精确定位聊天UI的证明**。
- 面板拖动、DPI变化、窗口宽度大幅变化或页面切换，仍可能导致内容边界不准确；本版不做视觉锚点检测。

默认比例来自V0.2参考布局，实际应用可调整比例属性。未加入AI接口、qnbot、自动回复、点击或发送。
工作范围止于V0.5比例布局验证。
