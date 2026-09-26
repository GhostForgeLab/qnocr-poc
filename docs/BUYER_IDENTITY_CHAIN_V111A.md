# V1.11-A Buyer 身份链路分析

## 当前输入来源

千牛聊天截图经 PaddleOCR 与 `BuyerMessageStructurer` 获得买家气泡、完整显示时间及聊天头部显示名。
`SessionRegistry.ReadBuyer` 只用截图/OCR头部文本提取唯一买家标签；UIA在本项目中只提供控件几何位置，不读取买家平台ID、昵称文本或消息内容。
该显示名可能被OCR误读，也可能是店铺中的别名，不能等同于千牛平台买家主键。

`SessionRegistry.Resolve` 将卖家配置ID、配置BuyerId和OCR标签组合为稳定SessionId。
当前真实千牛Shadow使用的是显式测试映射，生成 `buyer-test:<SessionId前缀>` 及隔离测试昵称。
`OcrMessageSource` 把这些显式ID写进原MessageEnvelope；独立的`QnocrMessageAdapter`再按测试映射生成qnbot输入对象。
这个链路证明多买家隔离与消息路由结构，不证明买家平台身份。
商品区OCR所得ProductId只作为独立商品引用，与买家强身份无关。

## V1.11-A 身份状态

- `Unknown`：缺少可用身份证据。
- `Observed`：仅有OCR聊天头部显示名，没有平台BuyerId。
- `Mapped`：有操作者配置或隔离测试映射，但尚未由运行中qnbot独立核对平台会话。`buyer-test:*`固定标记`IsTest=true`。
- `Verified`：仅允许可信运行时身份源同时证明SellerId、平台BuyerId、平台ConversationId、OCR会话显示名与SessionId的精确关系，并留存证据ID、捕获与验证时间。

`BuyerIdentity`随Session持久化，并单独写入`buyer-identity.json`；Context可携带它，但现有AI问题文本不读取这些新字段。
每次新的OCR观察最多重新得到Observed/Mapped，不自动沿用先前Verified；可信来源再次核验失败也会撤销先前Verified。
`CanSendToBuyer`要求非测试Verified、精确SessionId和BuyerId、可信来源、完整会话证据及5分钟内的新鲜验证；不满足即拒绝。

## 当前真实验证边界

本轮检查时，交互式Windows会话有千牛，但没有运行中的qnbot/Bot进程，也没有可调用的可信平台身份读取接口。
现有会话的BuyerId仍为测试映射，因此真实买家身份目前只能到`Mapped`，不能宣称`Verified`。
正向Verified检查使用注入的合成可信来源与虚构平台ID，只证明状态机、持久化与发送前置拒绝规则的实现，不代表任何真实买家已验证。
真实发送继续关闭；不能凭OCR昵称、操作员自报映射或合成证明绕过该边界。
