# V0.7 商品ID提取与ProductProfile映射验证

日期：2026-09-24。基于 V0.6 `3fda03e6a2f370aee1a6f27f061d0835c178620b`。
用户明确选择 **先用本地测试映射验证**，本次没有接入真实商品知识库、qnbot或Knowledge V2。

## 实现

- ProductIdExtractor从PaddleOCR逐行结果中提取有标签的连续数字ID，保留证据行号/原始文本/分数。
- ProductContextResolver读取本地JSON目录，按ProductId字符串精确查询ProductProfile。
- ProductContext输出ProductId、Confidence、状态、Profile、测试目录标记、来源路径及提取证据。
- 默认样例Profile仅为ID查表测试，不包含真实商品参数、价格、库存或销售知识。
- `--product 图片 [--profiles 本地JSON]` 单独执行商品流程；默认截图流程在聊天OCR后执行商品流程。
- UIA继续仅定位区域，商品ID只来自截图OCR，没有使用UIA Name/Value/TextPattern。

## 构建和检查

.NET SDK8.0.425 / runtime8.0.31，Windows x64；PaddleOCR依赖沿用V0.3。
主程序及验证项目严格编译：**0警告、0错误**。
检查程序：**102项通过**，包含以前58项与本版44项。

本版检查覆盖：中英文标签、全角字符、订单/会员/SKU等排除、金额/长数字/搜索占位符、
同一行分框与跨行拒绝、低分/空分/NaN/Infinity、多个ID、重复ID、保留前导零、
精确映射、未知ID、知识库不能替多卡片消歧、重复/非法目录、测试标记、空目录。
这些检查使用构造的OCR证据；下文另记录真实图片经过PaddleOCR的端到端结果。

日志：`Output/v07-build.log`、`Output/v07-checks.log`。

## 实际商品截图

首次单图输入：`Output/ValidationV07/product_region.png`（保留V0.6截图副本）。
命令：`dotnet run --no-build -- --product Output/Regions/product_region.png`。
实际识别行：`ID 1051087533910`，分数 **0.9774869680**，命中样例Profile，退出码0。
日志：`Output/v07-product.log`。
结果：`Output/Product/product_context_20260924_233308_441.json`。

随后运行 `dotnet run`，完整链路成功，退出码0：

```text
千牛截图 → UIA/视觉布局 → product_region.png → PaddleOCR → ProductIdExtractor → 本地测试映射
```

完整截图：`Output/qianniu_20260924_233659.png`（1638×949）。
右侧区域：UIA Document Bounds=(1035,120,600,827)。
本次输入副本：`Output/ValidationV07/live/product_region.png`。
SHA256：`83D59B163A21E6D2A15AC0D0458CF7CD6A73BF88CF4D594914FCC2675BF564E5`。

逐张目视确认，卡片显示ID为 **1051087533910**，OCR的13位数字与其一致。
本帧OCR证据为第28行，实际输出：

```json
{
  "Status": "Matched",
  "ProductId": "1051087533910",
  "Confidence": 0.9880003929138184,
  "Profile": {
    "ProfileKey": "poc-sample-1051087533910",
    "IsTestData": true
  },
  "IsTestCatalog": true
}
```

以上为摘要，完整结果同时保留DisplayName、测试用途说明、候选和原始OCR。
ProfileKey来自已配置JSON，不是根据图片标题生成的。

- 完整流程日志：`Output/v07-pipeline.log`。
- 最终上下文：`Output/Product/product_context_20260924_233705_667.json`。
- 商品OCR文本：`Output/OCR/product_region_20260924_233705_650.txt`，同名JSON保留边界框与分数。
- 样例目录：`Product/product-profiles.sample.json`，由项目复制到程序输出目录。

## 负面场景

| 输入/配置 | 状态 | 退出码 | 结果 |
| --- | --- | ---: | --- |
| 真实历史订单页区域，仅有订单号/金额等 | NotFound | 3 | ProductId=null，Profile=null，没有误认订单号 |
| 真实商品图 + 合法空目录 | Unmapped | 3 | 保留1051087533910与分数，Profile=null |
| 缺少图片参数 | 参数错误 | 2 | 显示用法 |
| 映射文件不存在 | 执行错误 | 1 | 明确报错，不伪装成成功或未命中 |

订单页输入：`Output/ValidationV06/regions_1100/product_region.png`。
日志：`Output/v07-order-negative.log`、`v07-unmapped.log`、`v07-invalid-args.log`、
`v07-missing-catalog.log`，汇总 `Output/v07-cli-checks.log`。
对应未识别/未映射结果：`Output/Product/product_context_20260924_233639_505.json`、
`product_context_20260924_233643_356.json`。

多ID/低置信度分支通过确定性OCR证据验证，未伪称为真实多卡片截图实测。
多个不同ID不会因只有一个ID存在于目录中而被强行消歧。

## 分数、性能和限制

Confidence取支持ID的OCR行分数；分框时取标签/数字框分数中的较低值，最低门槛0.80。
该数值不是“98.8%识别准确率”，更不是商品知识真实性或业务匹配概率。
这次同一商品的两帧均识别正确，样本量不足以给出总体准确率。

首次单图商品OCR：初始化622.9ms、推理2485.2ms、总3121.7ms，进程峰值工作集683.4MiB。
完整流程中的商品OCR：初始化594.9ms、推理2549.7ms、总3148.2ms，进程峰值984.7MiB。
完整流程的进程此前已执行聊天OCR，因此峰值不表示商品OCR独占内存；均为单次记录，不是吞吐基准。

接受10–16位连续数字是POC策略，不是平台全部商品ID规范。字母误识别、不带标签、断开的数字、
低分、多ID均保守处理，不猜测、不自动纠正、不按标题兜底。
当前截图可见唯一ID，不等于确认它就是买家本轮咨询的商品；分页、未展示卡片与遮挡内容不读取。
布局回退或误传其他区域图片仍可能影响上下文，映射命中本身不能校验区域是否正确。
本次只验证本地测试Profile查表；没有真实知识库覆盖率、商品知识正确性或销售回答验证。

未修改qnbot，未接入Knowledge V2、SalesAgent、AI接口、自动回复、自动点击或自动发送。
没有新增实时商品OCR循环，工作停止在V0.7。
