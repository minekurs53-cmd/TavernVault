<!-- 欢迎贡献。功能类 PR 建议先开 Issue 对齐意向（个人学习项目，是否合入取决于作者的自用优先级）；bug 修复 / 文档勘误 / 功能改进均可 -->

## 改动说明

<!-- 做了什么、为什么；关联的 Issue 编号 -->

## 自查清单

- [ ] `taskkill -IM TavernVault.exe -F` 后重新构建，`dotnet test` 全绿（单测 + 集成）
- [ ] 改动 API 时：隔离临时数据目录跑 `smoke_api.py` 全绿
- [ ] 未触碰真实用户资源库；写测试只进临时目录
- [ ] 冒烟/UI 验证不含桌面副作用动作
- [ ] 未移除安全中间件（会话令牌 / Host 白名单 / 内容清洗 / 写前备份）
- [ ] 文档按维护约定同步：README 功能行 → development-handoff §3 + §9.1 → quick-reference
