# V0.3 PaddleOCR 接入验证

日期：2026-09-24（Asia/Shanghai）。基于 V0.2 commit
`c52ef0e087f3308f80efef3280a75ba6980bddf0`，范围为单张图片 OCR。

## 实现与环境

- C# / .NET 8，Windows 10 x64；SDK 8.0.425，实际运行时 8.0.31。
- `OCR/PaddleOcrService.cs`：PaddleSharp 的 C# PaddleOCR 封装，本机 CPU MKL 推理。
- 模型：PP-OCRv5 中文（LocalFullModels.ChineseV5），支持中英数字混合识别。
- Sdcb.PaddleOCR / Models.Local / PaddleInference 3.3.1；LocalV5 模型资源 3.0.0；
  native MKL runtime 3.3.1.70；OpenCvSharp 托管/Windows runtime 4.11.0.20250507。
- 所有依赖通过 NuGet 下载到项目 `.tools/nuget`，版本及内容散列由 `packages.lock.json` 记录。
  本地模型随包提供，推理不调用 HTTP 服务、不上传截图。
- `dotnet build --no-restore -warnaserror`：**0 警告、0 错误**，见 `Output/v03-build.log`。
- 原生 Paddle runtime 输出 oneDNN 初始化信息和旧配置 API 弃用警告；保留在运行日志中，未导致推理失败。

技术来源：[PaddleSharp OCR 文档](https://github.com/sdcb/PaddleSharp/blob/master/docs/ocr.md)、
[PaddleOCR 项目](https://github.com/PaddlePaddle/PaddleOCR)。

## 两种运行方式

```powershell
# 按 README 设置本地 SDK 的 PATH/DOTNET_ROOT 后
dotnet run
dotnet run -- --ocr Output/Regions/chat_region.png
```

默认流程实测成功：窗口截图 → 固定区域裁剪 → chat_region OCR，退出码 0。
指定图片模式实测使用 `dotnet run --no-build -- --ocr Output/Regions/chat_region.png`，
退出码 0，不依赖千牛窗口发现或区域布局。旧 `--list` / `--extract` 模式仍只执行原操作。

本次默认流程原图：`Output/qianniu_20260924_223020.png`（1638×1100）。
聊天区域：`Output/Regions/chat_region.png`（654×537）。
为避免未来裁剪覆盖验收样本，保留副本 `Output/OCR/Inputs/chat_region.png`，SHA256：
`76CEAB5199BB40A6152FBF10471058386598C367F83A1CDBF6914EA15AF79F25`。

## 真实聊天截图识别结果

完整结果：`Output/OCR/chat_region_20260924_223023_265.txt` 与同名 `.json`。
指定图运行日志：`Output/v03-chat-ocr.log`；默认流程日志：`Output/v03-pipeline.log`。

以下是原样输出中的三条正文（程序没有添加角色标签）：

```text
上海的
好的，明天给你发顺丰特快
ok
```

逐项人工对照：

| 测试项 | 结果 |
| --- | --- |
| 中文正文 | 两条中文正文均正确 |
| 英文正文 | `ok` 正确 |
| 数字 | 用户名数字与四个可见时间戳的数字均正确 |
| 英文昵称 | `Whispering` 被输出为 `Whis pering`，多一个空格 |
| 头像/昵称附近 | 一处昵称前多出 `9`，属于误识别，未自动纠正 |
| 特殊符号 | 可见正文中文逗号、时间的 `-` / `:` 保留；表情未作为正文验证 |

三条可见文字正文逐条完全匹配 **3/3**。这是人工选定正文的局部评估，
不代表整张图片或未来聊天的准确率；整图仍存在昵称空格和额外字符错误。
本次输出 12 个非空文本框，丢弃 2 个空文本框；平均有效识别置信度 **0.9844**。
Confidence 是模型识别分数，不等于 98.44% 准确率。
这里只按坐标排序 OCR 行，没有买家/客服分类、消息切分、去重或新消息提取。

## 混合字符与容量单位

使用 `Validation/Create-OcrFixtures.ps1` 生成有明确参考文本的 950×420 PNG，
Microsoft YaHei，分别 18px 与 28px。测试图是人工构造的清晰文本样本，不是真实千牛消息。

- `Output/OCR/Fixtures/mixed_characters_18px.png`
- `Output/OCR/Fixtures/mixed_characters_28px.png`
- 参考文本：对应 `.ground-truth.json`，由绘图使用的同一文本数组生成。
- OCR 日志：`Output/v03-mixed-18px.log`、`Output/v03-mixed-28px.log`。
- 结果：`Output/OCR/mixed_characters_18px_20260924_222857_099.json`、
  `Output/OCR/mixed_characters_28px_20260924_222859_066.json`（各有 TXT）。

18px 样本实际输出：

```text
买家：这个200ml还有货吗?
客服：有现货，型号TA1。
材质：304不锈钢，型号F2U。
English: TA1 F2U 200ml 304
容量: 200ml 500mL 1L 1.5L
数量：2件，价格：￥39.90
符号: + -/()%# @
```

| 重点字符串 | 每张参考出现次数 | 18px 完整识别次数 | 28px 完整识别次数 |
| --- | ---: | ---: | ---: |
| TA1 | 2 | 2 | 2 |
| 200ml | 3 | 3 | 3 |
| 304 | 2 | 2 | 2 |
| F2U | 2 | 2 | 2 |

两张图中重点字符串共 **18/18 次**完整识别，保留了大小写。
`500mL`、`1L`、`1.5L`、数字价格 `39.90` 与中文字符也正确。
符号主体 `+ - / ( ) % # @ ￥ . ?` 可见且正确；空格有丢失，5 处参考半角冒号被识别为全角。

## 准确率计算口径

`Validation/Measure-OcrAccuracy.ps1` 使用 Levenshtein 编辑距离，
CER =（插入 + 删除 + 替换）/ 参考字符数。按输出行拼接，不计换行。
原始口径保留空格、大小写与标点；第二口径仅移除空白，不归一化全/半角标点。
此处所有字符均在 BMP 范围，因此脚本 UTF-16 字符计数与这些样本的字符数一致。

| 指标 | 18px | 28px |
| --- | ---: | ---: |
| 原始参考字符数 | 132 | 132 |
| 原始编辑距离 | 15 | 18 |
| 原始 CER | 11.36% | 13.64% |
| 1 − 原始 CER | 88.64% | 86.36% |
| 去空白参考字符数 | 111 | 111 |
| 去空白编辑距离 | 5 | 5 |
| 去空白 CER | 4.50% | 4.50% |
| 1 − 去空白 CER | 95.50% | 95.50% |
| 整行原样完全匹配 | 2/7 | 1/7 |

去空白后的 5 个错误均为冒号宽度差异。上述值仅描述这两张小型夹具，不能当作总体准确率。
机器可读评估：`Output/v03-accuracy-18px.json`、`Output/v03-accuracy-28px.json`。

## 耗时与内存

以下均为单次新建进程/引擎的实测，依赖和模型已在本地，未进行实时循环或吞吐压测。
机器及缓存状态影响结果，不报告平均值或稳态吞吐。

| 输入/模式 | 初始化 ms | 推理 ms | OCR 总耗时 ms | OCR 前/后工作集 MiB | 进程峰值工作集 MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| chat_region 指定图 | 628.6 | 1169.2 | 1812.2 | 24.9 / 367.8 | 461.8 |
| chat_region 默认完整流程中的 OCR | 672.5 | 1579.8 | 2264.5 | 31.5 / 374.0 | 468.1 |
| 混合字符 18px | 644.5 | 804.5 | 1461.5 | 24.9 / 373.7 | 451.2 |
| 混合字符 28px | 627.5 | 810.9 | 1450.6 | 24.9 / 343.5 | 467.3 |

推理包含文本检测和识别；总耗时包含读图/解码、初始化、推理、结果整理，
不含截图/裁剪、构建/下载、Console/文件写入、资源释放。
内存由 Process.WorkingSet64 / PeakWorkingSet64 取得，包含 .NET 与原生库；
峰值覆盖整个进程生命周期，并非 OCR 独占内存。OCR 后工作集在引擎释放前读取。

## 边界行为验证

- 空白 PNG：0 行、Confidence=null / Console N/A，保存合法 JSON/TXT，退出码 0。
- 不存在的路径：FileNotFoundException，退出码 1。
- README.md 作为输入：不可解码图片，退出码 1。
- `--ocr` 缺少路径：显示用法，退出码 2。
- 中文文件名 `混合字符18像素.png`：识别成功。
- 真实聊天图中的空检测框/NaN：不作为有效文本输出，不影响 JSON 序列化；
  非空框若返回无效分数则保留文本、分数为 null，不计入均值。
- 日志：`Output/v03-blank.log`、`v03-missing-file.log`、`v03-invalid-image.log`、
  `v03-invalid-args.log`、`v03-unicode-path.log`、`v03-error-paths.log`。

## 结论与范围

V0.3 单图 PaddleOCR 路径通过。当前样本上的聊天正文和重点型号/容量字符串可读，
但昵称、图形附近文字、标点宽度和空白仍有误差，不能把输出直接当作可靠业务字段。
未接入 qnbot、Knowledge V2、SalesAgent、AI 接口、自动回复、自动点击或自动发送；
未实现实时循环、变化检测或新消息提取。工作停止在 V0.3。
