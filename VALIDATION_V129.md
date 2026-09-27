# V1.29 Product Context Binding 验证报告

## 结论与实际边界

已建立Session级当前商品绑定：人工选择或同帧高置信度OCR识别可产生一个BoundProductContext，带SellerId、ProductId、ProductName、Source（OCR/UI/Manual）、Confidence、CapturedAt、BoundBy与CaptureId。已有Models/ProductContext.cs继续表示OCR/测试目录候选结果；新增BoundProductContext专门表示可用于当前Session的绑定，避免混用。

当前没有可核对的真实商品资料，用户明确回复“暂时没有”。因此没有给V1.28的真实测试会话绑定任何历史ProductId。本轮完成的是隔离测试商品的端到端验证，以及真实千牛一帧只读预检；**尚不能声称真实商品知识命中已验收**。

## 实现与安全边界

- IProductContextResolver由ManualProductResolver和OcrProductResolver实现。人工绑定要求已存在Session且SellerId匹配，换商品前须显式清除，超过2小时不再视为当前；OCR只接受当前商品区域唯一、带标签、置信度至少0.80的10–16位商品ID，名称仅在单一高置信度名称标签可读时填写，不猜测。
- SessionProductContextResolver在人工与当前OCR一致时保留一个活动商品；两者不同标记Conflict，低置信度或多个OCR候选标记Uncertain，无证据标记Unknown。历史ProductReferences仅供时间线审计，不参与当前商品选择。
- MessageSession/SessionContextView增加可选CurrentProductContext和ProductContextBindingStatus。MessageEnvelope结构及其ProductId不变；手工绑定通过独立Session sidecar进入KnowledgeRequest。旧测试上下文未带绑定状态时保留原有行为，新Shadow运行始终写明确状态。
- Bound时KnowledgeRequestBuilder使用当前Session商品；Unknown/Uncertain/Conflict时ProductReference为空、标记MissingProductContext，DurableShadowDispatch跳过KnowledgeProvider查询，仅保留缺失状态和人工核对预览。日志记录PRODUCT_CONTEXT_BOUND或PRODUCT_CONTEXT_CONFLICT，含SessionId、ProductId（仅绑定）、Source、Confidence、Timestamp；无自动发送。
- 隔离qnbot宿主的AgentContextWire与上下文安全校验接受受控商品sidecar，但不修改qnbot核心、MessageEnvelope、Knowledge仓储或AI回复生成策略。UI来源仅预留枚举，本轮未实现UI解析器。

人工测试CLI：dotnet run -- --product-bind STATE_DIR SESSION_ID SELLER_ID PRODUCT_ID PRODUCT_NAME OPERATOR；清除：dotnet run -- --product-clear STATE_DIR SESSION_ID OPERATOR。操作员标签仍不是生产鉴权。

## 文件与提交

实现提交：33d16ebf523a8e254ea8999d600ef9db581847d8；JEV同链路回归补强：9eec27f933894a4a26968ee62951eba00980dc28。新增Models/BoundProductContext.cs、Product/IProductContextResolver.cs、Product/ManualProductResolver.cs、Product/OcrProductResolver.cs、Product/SessionProductContextResolver.cs、Validation/ChangeDetectionChecks/ProductContextBindingChecks.cs；修改Session模型/注册表/ContextBuilder、KnowledgeRequest及Writer、Shadow Runner/Trace/Dispatch、隔离宿主Wire与校验、CLI及回归检查；删除文件无。

## 测试结果

- 编译：主项目、检查项目与Knowledge Console成功，0警告、0错误；隔离qnbot宿主成功（引用的未改动qnbot源码曾有现存CS4014警告，本轮宿主源码自身无新错误）。
- 回归：560/560检查通过（V1.28基线542，新增18）；原入口离线回归17/17、ForbiddenBusinessCalls=0。日志：Output/ValidationV129/regression.log。
- 人工绑定：测试Session人工绑定Product A=1051087533910，SellerId校验、换商品需清除、2小时过期、持久化重启及PRODUCT_CONTEXT_BOUND审计均通过。KnowledgeRequest含A与Capacity；CLI绑定/清除实测通过。
- OCR绑定：模拟当前商品区域唯一标签商品ID与名称，Session绑定Source=OCR、Confidence=0.99；低置信度与多ID候选均阻断。人工A与当前OCR B冲突时不查询知识；历史商品B存在但当前无证据时也不查询。
- 知识与预览：隔离ProductKnowledgeProvider对A返回测试Capacity=100ml,200ml；隔离派发注入JEV Choice测试响应，Intent=PRODUCT_SPEC_QUERY，模拟Agent生成Shadow预览，KnowledgeStatus=Available、KnowledgeUsedByAi=true、SentMessages=0。无商品上下文时计数Provider调用0、状态MissingProductContext、模型事实为空、仅生成待人工核对预览。
- qnbot隔离宿主：Envelope.ProductId仍为空，当前商品仅在Session/model sidecar；--agent-context-check确认KnowledgeFactsPresent=true、Capacity进入模型问题、AiCalls=0、SentMessages=0；MissingProductContext检查确认无事实且SentMessages=0。
- 合成真实AI烟测：以一条合成买家问题和本地测试商品事实调用现有OpenRouter Shadow宿主，Status=Converted、AiCalls=1、Reply Preview为“有的，这款规格里有200ml可选，您下单时选200ml规格就行。”；ProductContextUsage显示有界测试事实进入模型问题，SendEntryInvoked=false、SentMessages=0、ForbiddenBusinessCalls=0。另以同一合成SessionContext真实调用TypeSafe JEV，返回PRODUCT_SPEC_QUERY，JEV成功1、降级0、耗时770.8ms；隔离派发使用同等JEV Choice测试响应，随后OpenRouter宿主从同一知识sidecar生成上述预览。两次外部调用均不包含真实客户数据，也不能证明真实商品资料正确。
- 真实千牛只读预检：一帧截图成功，当前聊天区域无可解析买家消息，BuyerSessions=0、ProductReferences=0、AI Calls=0、SentMessages=0；未产生可用于真实商品绑定的证据。日志：Output/ValidationV129/real-unbound-check.log。

## 当前状态与下一步

已达到“测试Session → 人工或OCR当前商品上下文 → ProductReference → 测试Knowledge命中 → Shadow AI预览”，同时验证Unknown/Uncertain/Conflict安全阻断。真实千牛会话到可信商品的绑定仍待当前商品页或操作者可核对的SellerId/ProductId/商品名；收到资料后应先在Send OFF下复验同会话当前商品，再评估更强UI来源定位，不得从历史卡片或聊天文本猜测商品。
