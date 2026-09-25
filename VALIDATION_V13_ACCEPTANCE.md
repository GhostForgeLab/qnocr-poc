# V1.3 真实买家问题 AI Shadow 验收

日期：2026-09-25；开始版本 `067d8a45d2ee6fcc34262598512f7e66a972292e`。
Provider：OpenRouter，模型 `openai/gpt-5.5`。沿用已经验证的加密配置，未更换Prompt或模型参数。

## 本轮实现范围

仅增强qnocr-poc隔离宿主的验收日志：

- `AiShadowRuntime.Observations.cs` 调用原装SalesAgentShadowObserver/SalesAgentService，记录原始观察结果。
- 在复制的测试店铺作用域启用原装Knowledge V2的shadow开关，保留阈值；调用原装Resolve记录Matches、CandidateCount、Reason和耗时。
- 读取原生成服务的内存追踪队列，保存实际知识路由证据；不启动追踪上传定时器。
- 记录原生成服务耗时AiDurationMilliseconds及总协调器耗时，不把排队时间混入模型响应时间。
- `Summarize-AiShadow.ps1` 汇总本地消息、回执、命中和发送边界计数。

qnbot源码与核心逻辑没有修改；基线仍为 `4802807f26a6abf4cfb4457d5b158b2f51f4ff18`，工作区干净。
运行库继续验证既有SHA256。原配置未改动；新增开关仅作用于`.tools/ai-shadow`中的隔离测试作用域。

必须区分：SalesAgent和Knowledge V2是按次执行的**Shadow观察**，其结果没有替换原生成服务的Prompt或路由。
OCR ProductId作为外部商品ID提示传给观察服务并留档，未伪造已确认的内部ProductProfile映射。
当前可证明组件执行与AI预览，不因此宣称商品知识已进入AI推理或生产账号已接通。

## 实测方式与证据

运行命令：

```powershell
dotnet run --no-build -- --shadow --ai-shadow --identity Validation/QnbotCompatibility/identity_mapping.json
```

会话：`Output/Shadow/shadow_20260925_133136_882_c3351197b48d46399db3815e064ddaa3`。
控制台日志：`Output/v13-live-acceptance.log`。
原始OCR、截图、指纹判断、Envelope和AI回执均保留在本地Output，不提交账号或真实截图。
验收要求消息先经过实际截图和OCR，由用户在基线就绪后从买家端发送；不通过离线JSON冒充真实输入。
运行结束后审计文件为会话目录的 `acceptance-audit.json`。

主程序编译0警告/0错误；隔离宿主0错误，保留原qnbot协调器CS4014警告；186项回归检查通过。
日志：`Output/v13-acceptance-build.log`、`v13-acceptance-host-build.log`、`v13-acceptance-checks.log`。
增加观察日志后的匿名冒烟成功：原装SalesAgent已分析，Knowledge V2无候选，AI返回中文回复。
冒烟记录 `Output/AiShadow/acceptance-preflight/shadow-adapter-result.json`；不计入真实10条结果。

## 真实10条结果

本轮已建立基线并提示用户发送，随后连续251帧捕获成功、0帧失败，截图保持静止。
只有1条历史基线消息被抑制，新增消息0，Envelope 0，真实AI预览0，发送0。
未收到用户的发送完成确认，也未观察到新问题；已通过stop.request正常停止，退出码0。
这不是10条测试通过，也不能判断漏检率；尚缺实际买家发送和逐条对照证据。

| 类型 | 预定问题 | 实际输入/AI/质量 |
| --- | --- | --- |
| 商品规格 | 这款有哪些规格？ | 未捕获，未验收 |
| 容量 | 容量是多少？ | 未捕获，未验收 |
| 材质 | 是什么材质的？ | 未捕获，未验收 |
| 价格 | 这款多少钱？ | 未捕获，未验收 |
| 发货 | 今天下单多久发货？ | 未捕获，未验收 |
| 定制 | 可以刻字吗？ | 未捕获，未验收 |
| 使用场景 | 适合户外露营使用吗？ | 未捕获，未验收 |
| 对比咨询 | 跟不锈钢款有什么区别？ | 未捕获，未验收 |
| 售后边界 | 刻字后不喜欢能退吗？ | 未捕获，未验收 |
| 模糊咨询 | 这个怎么样？ | 未捕获，未验收 |

## 回复质量及调整建议

10条真实回复尚不存在，不能给出逐条质量评分。
仅匿名冒烟返回“刻字需看该款是否有定制选项，发货时间以商品页显示为准哦。”：
未编造具体刻字规格或发货时限，但没有提供商品特定答案。SalesAgent状态Analyzed，商品绑定Unknown；
Knowledge V2 CandidateCount=0、Matches为空；实际生成追踪为AiGeneral/candidates=0。
该次生成服务耗时10417ms；流式无文本后由原有非流式兜底成功，AiCalls=2，不应解释成两条成功回复。

基于目前证据的建议（未实施）：

1. 先建立OCR外部商品ID到真实内部ProductProfile的可核验映射，并向测试作用域加载真实商品/店铺知识。
2. 验证知识候选确实进入现有Prompt上下文；当前SalesAgent/V2观察结果没有替换原生成路径，仅开观察开关不足以证明知识被使用。
3. Prompt应明确区分已核实事实和缺失信息；价格、发货、定制与售后无证据时先澄清，不生成具体承诺。
4. 真实10条跑完后再按回答相关性、事实依据、澄清是否必要、风险边界和耗时逐项评级，不用匿名冒烟代替。

当前状态：代码与观察日志准备完成，真实10条验收等待用户准备买家端。所有本轮进程已停止。
