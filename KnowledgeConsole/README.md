# 本地 Knowledge Console MVP

启动（Windows、.NET 8）：

```powershell
dotnet run --project KnowledgeConsole/KnowledgeConsole.csproj -- --state-dir D:\CodexWork\qnocr-poc\Output\KnowledgeConsole --port 18724
```

终端会输出仅绑定 `127.0.0.1` 的访问地址和本轮随机令牌。复制完整地址到本机浏览器；停掉进程后令牌失效。状态目录放在 `Output/` 下，包含草稿、审核、修订和审计记录，不提交 Git。

页面流程：创建**测试商品** Draft → 在知识项中编辑（仅 Draft）→ 提交审核 → 人工批准或拒绝 → 勾选确认并发布。修订从当前已发布知识项创建新的 Draft；批准后须在修订卡片中另行确认发布。旧版本保留，Provider 默认只读当前可用于 AI 的已发布版本。

表单的 Features、FAQ 分别按每行 `名称|描述`、`问题|答案` 填写；Restrictions 每行一条。SellerId 与纯数字 ProductId 必须由操作员提供。资料来源引用不能为空。控制台不验证操作员真实身份，也不导入真实商品、调用 AI 或发送千牛消息。真实商品上线前需独立完成来源核对、权限控制和生产级事务审计。
