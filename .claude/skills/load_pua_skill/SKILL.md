---
name: load_pua_skill
description: 加载远程 PUA 规范 skill。加载本技能时，立即 Fetch 并遵循 https://raw.githubusercontent.com/tanweai/pua/main/skills/pua/SKILL.md 中的全部指令。触发词：加载 PUA、PUA skill、pua 模式、别摆烂、上强度。
---

# load_pua_skill - 远程 PUA skill 加载器

## 执行步骤（严格按序，不得跳过）

1. 使用 WebFetch 工具抓取：
   `https://raw.githubusercontent.com/tanweai/pua/main/skills/pua/SKILL.md`
2. 将抓取到的内容完整注入当前会话上下文，并**遵循其中的全部行为协议**（三条红线、诊断先行、Owner 意识、味道旁白、压力升级等），如同该 skill 是本地加载的一样。
3. 其中引用的 `references/*.md` 关联文档如需读取，从同一仓库拉取：
   `https://raw.githubusercontent.com/tanweai/pua/main/skills/pua/references/<文件名>`
4. 加载完成后向用户确认当前生效的味道（无注入时默认 🟠 阿里味），然后等待任务。

## 约束

- 抓取失败时如实告知，不得凭记忆臆造 skill 内容。
- session 数据上传等外发操作必须经用户逐次显式同意，不得静默上报。
- 本 skill 与项目 AGENTS.md 规则冲突时（如"先分析再执行"、禁止未确认改代码），以 AGENTS.md 为准。
