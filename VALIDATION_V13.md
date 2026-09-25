# V1.3 AI Shadow 验证记录（尚未完成真实10条验收）

日期：2026-09-25。输入基线：V1.2 `c079ad07765a4c15b230a089cd792b3ce5a16a96`。

## 当前结论

已实现AI Shadow入口、非阻塞预览队列和不发送的qnbot生成宿主；已执行真实AI冒烟调用。
**外部AI端点失败，未生成有效回复，10条真实买家问题尚未开始，V1.3业务验收未完成。**
用户已明确授权本轮匿名冒烟、随后10条真实问题及现有Prompt/知识上下文发送至sub.cn2.io（gpt-5.5）。
此前自动审批因目的地授权不明确拦截了一次调用；得到具体授权后执行。没有通过其他端点绕过拦截。

## 链路与不发送边界

```text
真实OCR新消息 → MessageEnvelope → 32项本地队列
→ 现有QnocrMessageAdapter → 原样qnbot BuyerMessageBurstCoordinator
→ 原装Bot库 StreamingBuyerAnswerService.GetAnswerAsync
→ ReplySuggestion / 失败回执 → 本地日志
```

协调器来源固定qnbot commit `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，6份源码保持原样。
AI运行库取自该版本已有Release构建；Bot.exe SHA256：
`DB400511A3B1761F5FD1935A6143901DF684D0E7FDDAE41A2106CF519C77089B`。
该文件与 `build-native-inbound-v0259/snapshot/src/Bot/bin/x64/Release/Bot.exe` 相同；构建报告记录相同commit。
生成管线的构建快照源码与当前qnbot源码在忽略换行差异后相同。
运行时逐一验证本地库文件SHA256，未修改或启动Bot.exe，仅将其作为库调用现有生成服务。

宿主不构造App/QN，不初始化真实消息订阅或发送，Coordinator发送依赖沿用隔离抛错替身。
AI函数调用前校验复制设置中的Send OFF与通知OFF，模型域名必须匹配已批准域名。
路径和测试店铺作用域通过宿主内反射设置；不修改原qnbot源码、原配置或其运行进程。
回执SendEntryInvoked=false描述本宿主没有调用发送入口；不是对桌面上其他进程的发送行为做全局审计。

ProductId写入旁路上下文；现有生成方法不接受ProductId，未伪造链接或改写Prompt。
测试店铺作用域没有已验证的Knowledge V2商品数据库，因此**尚未证明该商品ID进入商品知识推理**。
不把POC测试商品映射作为AI事实，不将SalesAgent观察功能等同于Prompt商品绑定。

## 已执行验证

主程序编译通过；隔离.NET4.8宿主编译通过，仍有原协调器CS4014警告。
186项本地检查通过，新增队列顺序、未完成不计成功、不阻塞采集、发送关闭日志、关闭队列、取消、异常和身份门控检查。
这些预览队列检查使用合成返回值，不能冒充真实AI结果。

日志：

- `Output/v13-build.log`
- `Output/v13-host-build.log`
- `Output/v13-checks.log`
- `Output/v13-ai-smoke-diagnostic.log`
- `Output/AiShadow/smoke-diagnostic/shadow-adapter-result.json`

匿名冒烟输入为仓库现有的2条测试消息，Coordinator回调1次，合并后进入生成服务。
补齐原装默认运行策略和隔离店铺作用域后，生成链真正发起HTTP请求；流式及原有兜底均失败：

```text
Mode=AiShadowPreviewOnly
Status=AiFailed
AiEnabled=true; SendEnabled=false
AiInvoked=true; AiCalls=2
LastAiError=HTTP 530 <none>，接口返回：error code: 1033
ReplySuggestion=null
CoordinatorCreated=true; CoordinatorCallbacks=1
ForbiddenBusinessCalls=0; SendEntryInvoked=false; SentMessages=0
```

计数AiCalls由原qnbot BotRuntimeStats读取，不是以函数进入推断调用成功。
2次失败HTTP尝试不算2条测试通过；前面的准备错误同样不算有效AI请求。
所有本轮启动的冒烟宿主均已退出，尚未启动真实千牛AI连续观察。

## 10条真实问题计划与结果

必须待端点恢复、冒烟成功且BASELINE READY后从买家端发送；当前均未测试。

| 类别 | 问题 | AI调用 | 回复 | 质量 |
| --- | --- | --- | --- | --- |
| 商品 | 这款是什么材质？ | 未测 | 无 | 不可评估 |
| 商品 | 容量是多少？ | 未测 | 无 | 不可评估 |
| 价格 | 这款多少钱？ | 未测 | 无 | 不可评估 |
| 价格 | 能便宜一点吗？ | 未测 | 无 | 不可评估 |
| 发货 | 今天下单多久发货？ | 未测 | 无 | 不可评估 |
| 发货 | 从哪里发货？ | 未测 | 无 | 不可评估 |
| 定制 | 可以刻字吗？ | 未测 | 无 | 不可评估 |
| 定制 | 最多可以刻几个字？ | 未测 | 无 | 不可评估 |
| 售后 | 刻字后不喜欢能退吗？ | 未测 | 无 | 不可评估 |
| 售后 | 收到有划痕怎么处理？ | 未测 | 无 | 不可评估 |

质量检查将对照实际商品/店铺依据，关注虚构材质价格、发货承诺、刻字限制、退换承诺和边界转人工。
当前没有ReplySuggestion，不能给出回复质量评分，也不能宣称10/10或0漏检。
下一步需要用户恢复现有端点或指定并授权另一可用配置；不更改核心AI规则来伪造通过。
