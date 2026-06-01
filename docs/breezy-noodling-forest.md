# PingCode 工作项详情页 AI 实现/修复方案

## Context
范围最终收敛为：只在 PingCode 工作项详情页增加“AI 实现”或“AI 修复”入口，不改 PingCode 看板卡片，也不跳转到代码工作区页面。点击后从当前工作项详情提炼 prompt，在 PingCode 侧弹出待执行窗口；用户在该窗口中选择代码仓库、编辑 prompt、选择 Claude 或 Codex，然后直接启动 CLI 执行。

工作项中的链接必须被提炼出来，因为这些链接可能指向详细方案、设计文档、接口说明、复现截图、日志页面或其他关键上下文。生成的 prompt 不能直接发给 CLI，必须先允许用户查看和修改。

## 核心交互

1. 用户在 PingCode 工作项详情页查看某个工作项。
2. 详情页根据工作项类型展示：
   - 用户故事/需求/任务：`AI 实现`；
   - 缺陷/Bug：`AI 修复`。
3. 点击按钮后，系统从当前 `WorkItemDetails` 提炼结构化 prompt。
4. 提炼内容包含工作项正文、评论、子任务、附件文字信息，以及所有可识别链接。
5. 在 PingCode 侧弹出 `AI 执行` 窗口。
6. 用户在弹窗中选择目标代码仓库。
7. 用户查看并编辑 prompt。
8. 用户点击 `Claude 执行` 或 `Codex 执行`。
9. 复用代码工作区现有 CLI 启动方式，在选中仓库目录中直接注入用户确认后的 prompt 执行。

## 工作项详情页入口

### 修改文件
- `Features/PingCode/Views/WorkItemDetailsWindow.xaml.cs`
- 若详情页按钮布局在 XAML 中定义，则同步修改 `Features/PingCode/Views/WorkItemDetailsWindow.xaml`

### 设计要点
- 只在详情页增加入口，不动 `WorkItemKanbanWindow` 卡片。
- 不跳转代码工作区页面。
- 入口文本按类型变化：
  - 缺陷、Bug、故障类：`AI 修复`；
  - 用户故事、需求、任务类：`AI 实现`。
- 按钮点击时使用当前窗口已持有的 `Details`，必要时再调用 `PingCodeApiService.GetWorkItemDetailsAsync(Details.Id)` 刷新一次。
- 点击后直接打开 PingCode 侧 AI 执行弹窗。

## PingCode 侧 AI 执行弹窗

### 建议新增文件
- `Features/PingCode/Views/PingCodeAiExecutionWindow.xaml`
- `Features/PingCode/Views/PingCodeAiExecutionWindow.xaml.cs`

### UI 内容
- 工作项标题、类型、动作：实现/修复。
- 从工作项提炼出的关键链接列表。
- 仓库选择下拉框或选择器。
- 可编辑 prompt 文本框。
- 操作按钮：
  - `恢复初始 Prompt`
  - `复制 Prompt`
  - `Claude 执行`
  - `Codex 执行`
  - `取消`

### 行为
- 弹窗打开时加载当前配置中的代码仓库列表。
- 默认可选最近使用仓库或与当前工作项/产品包有关联的仓库；如果无法推断，则不默认选择。
- 用户未选择仓库时，不允许启动 CLI。
- 用户可以编辑 prompt；实际传给 CLI 的必须是编辑后的最终文本。
- 用户点击取消时，不启动 CLI，不修改代码工作区状态。

## 仓库选择

### 复用现有模型与服务
- `Features/CodeWorkspace/Models/CodeRepository.cs`
- `Services/DataPersistenceService.cs`

### 设计要点
- 弹窗直接读取持久化配置中的 `CodeRepositories`。
- 选择仓库只用于确定 CLI 工作目录，不需要切换到代码工作区页面。
- 可展示仓库名称、路径、已关联产品包、最近使用时间。
- 用户点击执行后，可更新仓库 `LastUsed` / `UsageCount`，以便后续默认排序。

## Prompt 提炼服务

### 新增文件
- `Features/PingCode/Services/PingCodeWorkItemPromptService.cs`
- `Features/PingCode/Models/PingCodeAiPromptRequest.cs`

### 职责
- 判断工作项是“实现类”还是“修复类”。
- 从 `WorkItemDetails` 提炼 AI 执行 prompt。
- 不把原始 JSON 直接塞给 AI，而是结构化整理为：
  - 工作项基本信息；
  - 背景/目标；
  - 验收标准或期望结果；
  - 复现步骤；
  - 评论/子任务补充摘要；
  - 工作项内识别到的链接；
  - 执行约束；
  - 汇报要求。

### 链接提炼要求
- 从工作项描述、富文本 HTML、评论、子任务描述、附件说明中识别 URL。
- 链接应保留原始地址和附近文本上下文，例如“详细方案”“接口文档”“复现截图”“日志地址”。
- 对同一链接去重。
- 不在应用内自动访问未知链接，避免把私有网页内容错误当作已知事实；prompt 中应明确要求 AI 在执行时按需打开或让用户确认链接内容。
- 如果链接文本像是方案文档，应在 prompt 中单独列为“必须优先阅读的参考资料”。
- 如果链接是图片、附件或日志，应在 prompt 中列为“辅助排查资料”。

### 用户故事/需求 prompt 重点
- 当前任务是实现 PingCode 用户故事/需求。
- 先阅读当前仓库，定位相关代码。
- 优先阅读工作项中提到的方案/设计/接口链接。
- 复用现有架构、组件和交互模式。
- 按验收标准逐项实现。
- 不扩展无关功能，不做无关重构。
- 实现后运行必要构建、测试或 UI 验证。
- 最后汇报修改文件、验证结果、验收标准覆盖情况。

### 缺陷/Bug prompt 重点
- 当前任务是修复 PingCode 缺陷。
- 先根据问题描述、复现步骤和相关链接定位根因。
- 优先查看工作项中提供的日志、截图、复现页面或详细说明链接。
- 最小化修改，避免无关需求扩展。
- 修复后运行针对性验证。
- 最后汇报根因、修复点、影响范围、验证结果。

## CLI 注入实现

### 复用现有模式
现有 `Features/CodeWorkspace/Views/CodeWorkspacePage.xaml.cs` 中 `DoAiCommitAsync` 已采用：

- `EnsureCommandExists(commandName)` 检查 CLI；
- `TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, title)` 打开终端；
- `PsQuote(prompt)` 将 prompt 作为命令参数传给 Claude/Codex。

新功能应把可复用的 CLI 启动逻辑抽到服务中，避免 PingCode 视图直接依赖代码工作区页面私有方法。

### 建议新增服务
- `Features/CodeWorkspace/Services/AiCliLaunchService.cs`

### 服务职责
- 检查 `claude` / `codex` 命令是否存在。
- 拼接 PowerShell 命令。
- 调用 `TerminalHelper.LaunchTerminalWithCommand(workingDirectory, command, title)`。
- 对外提供：
  - `LaunchClaudeAsync(CodeRepository repo, string prompt, string title)`
  - `LaunchCodexAsync(CodeRepository repo, string prompt, string title)`

### 命令形态

Claude：
```powershell
Set-Location -LiteralPath '<repo.Path>'
Write-Host 'PackageManager PingCode AI 执行入口' -ForegroundColor Cyan
Write-Host '工作项：<title>' -ForegroundColor DarkCyan
claude --dangerously-skip-permissions '<editedPrompt>'
```

Codex：
```powershell
Set-Location -LiteralPath '<repo.Path>'
Write-Host 'PackageManager PingCode AI 执行入口' -ForegroundColor Cyan
Write-Host '工作项：<title>' -ForegroundColor DarkCyan
codex --sandbox danger-full-access --ask-for-approval never '<editedPrompt>'
```

### prompt 长度处理
- 第一版按用户要求直接注入用户确认后的 prompt。
- 如果 prompt 超过安全长度，再降级为写入临时文件并注入“读取该文件执行”的短 prompt。
- 这个降级只用于避免命令行长度限制，不改变“先预览可编辑再执行”的主流程。

## 关键修改文件

- `Features/PingCode/Views/WorkItemDetailsWindow.xaml(.cs)`
  - 增加 `AI 实现/AI 修复` 入口。
- `Features/PingCode/Services/PingCodeWorkItemPromptService.cs`
  - 新增，负责工作项信息和链接提炼。
- `Features/PingCode/Models/PingCodeAiPromptRequest.cs`
  - 新增，承载生成后的 prompt、链接、动作类型、工作项基本信息。
- `Features/PingCode/Views/PingCodeAiExecutionWindow.xaml(.cs)`
  - 新增，负责仓库选择、prompt 编辑、Claude/Codex 执行。
- `Features/CodeWorkspace/Services/AiCliLaunchService.cs`
  - 新增或抽取，复用现有 Claude/Codex CLI 启动方式。
- `Features/CodeWorkspace/Services/TerminalHelper.cs`
  - 继续复用，不需要改变主职责。

## 分阶段落地

### 一期
- 在工作项详情页增加 `AI 实现/AI 修复`。
- 实现 `PingCodeWorkItemPromptService`。
- 提炼工作项正文、评论、子任务与链接。
- 弹出 PingCode AI 执行窗口，支持 prompt 编辑和复制。

### 二期
- 在弹窗中加载代码工作区仓库列表。
- 用户选择仓库后，复用现有 Claude/Codex 提交方式直接注入 prompt 并启动 CLI。
- 抽出 `AiCliLaunchService`，避免重复代码。

### 三期
- 完善链接分类：方案文档、接口文档、截图/附件、日志、外部系统页面。
- 超长 prompt 自动降级为文件读取模式。
- 根据仓库使用记录或产品包关联推荐默认仓库。

## Verification

- 打开 PingCode 工作项详情页，需求/故事类显示 `AI 实现`，缺陷类显示 `AI 修复`。
- 点击按钮后不跳转代码工作区，而是在 PingCode 侧弹出 AI 执行窗口。
- 工作项描述或评论中包含链接时，生成 prompt 应列出链接和附近上下文。
- 弹窗中应能选择代码仓库。
- 未选择仓库时不能启动 CLI。
- 用户能编辑 prompt，Claude/Codex 注入的必须是修改后的版本。
- 选择仓库后点击 Claude/Codex，应在该仓库目录打开 CLI。
- CLI 命令应直接注入工作项 prompt，行为与现有 Claude/Codex 提交流程一致。
- 缺陷 prompt 应包含复现步骤、实际结果、期望结果、根因定位要求和相关链接。
- 实现 prompt 应包含业务目标、验收标准、实现约束、验证要求和方案/设计链接。
- 取消弹窗后，不应启动 CLI，也不影响 PingCode 详情页或代码工作区现有功能。
