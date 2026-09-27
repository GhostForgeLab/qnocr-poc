# V1.28 真实 JEV Agent Pipeline 验证报告

## 结论与边界

已在真实运行中的千牛接待中心，以测试买家会话完成10条新消息的Shadow验收：OCR采集10/10、单Session绑定10/10、JEV意图判断10/10、外层Knowledge状态记录10/10、qnbot适配与AI回复预览10/10。全部为本地预览，SentMessages=0、SendEntryInvoked=0。本轮没有商品引用，Knowledge对9条返回Missing、1条NotRequired；未验证已发布商品知识命中或商品事实支撑的回答，AI建议仍须人工核实。

## 实现与架构

外层Intent增加INTENT_PROVIDER_RESULT（SessionId、Provider、Intent、LatencyMilliseconds、FallbackUsed），每轮输出intent-stats.json、健康日志与summary：TotalIntentRequests、JEVSuccess、JEVFailure、RuleFallbackCount、AverageLatencyMilliseconds。新增--intent-review-list与--intent-review-mark，只记录人工IntentCorrect/IntentWrong，不会自动训练、调整规则或选择发送。输出目录下的临时烟测C#源码已从主项目编译项排除。

实现提交：0c70bbc98bc83ab8a263c0cc1093ff6e89d93249；构建修复：691c443d8d55eddfa20d831a85ec68d7fbd94ed9。新增Decision/IntentRuntimeStatistics.cs、Decision/IntentQualityReviewService.cs、Models/IntentRuntimeSnapshot.cs、Models/IntentHumanReview.cs及检查；修改Intent/Agent Trace、Shadow Runner、CLI与测试入口；删除文件无。MessageEnvelope、qnbot核心、Knowledge流程、AI回复生成与发送逻辑未修改。

## 编译与回归

检查项目和Knowledge Console均编译成功，0警告、0错误。回归542/542通过（V1.27基线531，新增11），原入口离线回归17/17、ForbiddenBusinessCalls=0。日志：Output/ValidationV128/regression.log。隔离故障测试验证缺Key及HTTP错误由RuleBased接管；真实10条中JEV失败0、降级0。用一条合成规格问题经默认工厂真实调用TypeSafe，JEV成功1次，分类耗时852.1ms；该合成调用不计入下述10条真实千牛测试。

## 真实千牛测试统计

- 授权范围：仅测试买家Session；TypeSafe JEV作意图判断，OpenRouter openai/gpt-5.5仅生成本地预览；AI ON，Send OFF。
- 会话：A66A2D8E64F64A1A1A8990EEC5743AE21CFBCED6D2D2DB1734C574A4E259BC12；历史基线3条未派发。
- 时间：2026-09-27 22:16:26—22:20:33（约247秒）；97/97帧成功；新增消息10、转换10、未处理异常0、错误0。34次重复观察被抑制，未产生重复Envelope。
- Intent：TotalIntentRequests=10，JEVSuccess=10，JEVFailure=0，RuleFallbackCount=0，平均分类耗时402.4ms。
- Knowledge：Missing 9，NotRequired 1；ProductReferences=0，KnowledgeUsedByAi=0。没有伪造ProductId或商品事实。
- AI：OpenRouter收据10/10 Converted、AiInvoked10、AiCalls10、Reply Preview10；每条SendStatus=Shadow。SendEntryInvoked=0、SentMessages=0、ForbiddenBusinessCalls=0。
- 人工Intent质量标记：用户逐条确认10条均正确；记录IntentCorrect=10、IntentWrong=0。这是本轮测试者判断，不代表总体模型准确率。

## 逐条结果

| # | 测试买家问题 | JEV Intent | 耗时ms | 降级 | Knowledge | AI Reply Preview |
| --- | --- | --- | ---: | --- | --- | --- |
| 1 | 这款有200ml规格吗？ | PRODUCT_SPEC_QUERY | 751.58 | False | Missing | 这款以页面可选规格为准，若没看到200ml选项，当前页面就暂无哦。 |
| 2 | 杯身是什么材质？ | MATERIAL_QUERY | 374.7 | False | Missing | 杯身材质以商品详情页标注为准，您也可以发下商品链接我帮您确认。 |
| 3 | 今天下单多久发货？ | SHIPPING_QUERY | 369.99 | False | Missing | 今天下单会按页面承诺时效安排发货，具体以付款后订单页显示为准哦。 |
| 4 | 可以刻字吗？ | CUSTOMIZATION_QUERY | 341.9 | False | Missing | 刻字要看这款是否支持定制，规格里没有的话我帮您转人工确认。 |
| 5 | 这款现在多少钱? | PRICE_QUERY | 349.92 | False | Missing | 这款价格以页面显示为准，您可看下规格选择后的到手价哦。 |
| 6 | 刻字后不喜欢能退吗? | AFTER_SALES_QUERY | 371.85 | False | Missing | 刻字属于定制服务，退换规则需按页面说明和订单情况，建议联系人工客服确认哦。 |
| 7 | 送朋友合适吗？ | GIFT_QUERY | 373.77 | False | Missing | 送朋友挺合适的，实用度高；如要刻字，建议先确认好内容哦。 |
| 8 | 适合收藏展示吗？ | COLLECTION_QUERY | 350.43 | False | Missing | 可以做日常摆放展示，收藏用途建议以页面材质和款式细节为准哦。 |
| 9 | 防水等级是多少？ | PRODUCT_SPEC_QUERY | 411.65 | False | Missing | 防水等级以商品详情页标注为准，发下具体款式我帮您核对。 |
| 10 | 这个怎么样? | OTHER | 328.04 | False | NotRequired | 这款日常用或送朋友都比较合适，规格和价格以页面选项为准哦。 |

原始逐条数据（MessageId、CaptureId、SessionId、Intent、耗时、Knowledge缺失字段、AI ResponseId、PreviewId、发送状态）位于Output/ValidationV128/real-results.json；本轮完整Shadow审计位于Output/Shadow/shadow_20260927_221626_424_1425db7c36e747be966c21aea43fc4b3/，人工判断位于该目录的intent-review.jsonl及intent-feedback/。运行摘要状态为StoppedByRequest，在10条预览处理完成后正常停止。以上Output文件不提交Git。

## 回复风险与下一步

由于当前会话没有可见商品引用、没有已发布知识进入模型输入，关于规格、材质、价格、送礼、收藏、防水等回复均只是AI建议。尤其“送朋友挺合适”等判断缺少商品证据，不应直接对买家承诺。下一阶段应先验证商品引用与已发布知识的绑定，再评估Hybrid Intent Strategy；若继续接入外部知识源，应先明确来源权限与审核边界。保持人工辅助与Send OFF，不能根据本轮10/10分类判断开启自动发送。
