---
name: git_svn_commitlog_generator
description: Git+SVN 改动由脚本采集；模型默认打开本机可见 PowerShell 做限时交互，超时自动生成日志并确认提交推送。
---

# git_svn_commitlog_generator - Git+SVN 改动分析与提交日志生成

目标：扫描当前目录下**所有 Git 改动与 SVN 改动**（未提交的工作区改动，包含新增/修改/删除/重命名），并生成符合约定格式的提交日志：

- `type(scope1、scope2): 简要描述`
- `- 具体条目1`
- `- 具体条目2`

其中：

- `type`：优先使用 conventional 类型（图1那组），允许扩展但要克制
- `scope`：一般是功能名称；若能定位到模块/项目，优先使用项目/模块名

## 触发词建议

- 生成提交日志
- 根据当前改动生成 commit message
- 统计 Git + SVN 改动并生成提交说明
- 帮我写提交日志（支持排除文件）

## 核心约束（必须遵守）

1. **脚本 JSON 输出是唯一数据源**：模型不得自行递归扫描目录来决定「改动范围」。
2. **交互在脚本内完成且必须可超时**：是否将 `NeedsAdd` 纳入版本库、是否按 `Id` 排除项，由 `get-working-changes.ps1` 在 **Windows 可交互控制台** 内处理，窗口会显示剩余秒数；若在超时时间（`-PromptTimeoutSeconds`，默认 30）内无按键，则采用 **第 1 个选项**（不加入未跟踪 / 不排除）。若选择“按 Id 输入”，输入行同样按 `-PromptTimeoutSeconds` 超时，超时或空输入视为不选择任何 Id。**禁止**由模型在聊天里用 `Start-Sleep`、阻塞式原生选项菜单或「伪后置步骤」替代脚本交互。
3. **模型默认限时交互调用**：生成提交日志时，模型默认必须打开本机可见 PowerShell 运行脚本 **`-Interactive -PromptTimeoutSeconds 30`**，给用户一次加入/排除机会；无人操作则脚本自动按默认项继续。只有用户明确要求“非交互 / CI / 直接生成 / 不要弹窗”时，才使用 **`-NonInteractive`**。
4. **一次只生成一条提交日志**：即使涉及多个项目，也不要拆成多条提交；多项目只在 `scope` 中写清楚，并在正文条目中说明各项目关键改动。
5. **不要粘贴大段 diff**：提交日志只输出抽象后的变更点，不直接贴 patch 内容。
6. **长日志必须主动硬换行**：标题与正文按后文「长文本换行规则」处理。
7. **最终提交/推送也必须限时交互**：模型生成最终提交日志后，默认必须打开本机可见 PowerShell 询问是否提交并推送；`-PromptTimeoutSeconds` 默认 30 秒，超时自动选择 **第 1 个选项：提交并推送**。只有用户明确要求“只生成日志 / 不提交 / 不推送”时才跳过这一步。

## 执行流程

### Step 1) 获取 Git/SVN 待提交改动（脚本唯一来源）

**模型默认（推荐）**：调用 wrapper 脚本打开本机可见 PowerShell，让脚本完成中文限时交互，并把 JSON 回传给模型；窗口退出后模型读取该 JSON 生成提交日志。**不要**把多行 `Start-Process -Command` 直接粘到 Bash/WSL 执行器里，避免引号被 Bash 解析导致 `unexpected EOF`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-working-changes-interactive.ps1 -PromptTimeoutSeconds 30
```

**人类在 Windows 本机终端**：需要脚本内 `choice` 时，使用 **`-Interactive`**（且 stdin 未重定向），脚本会依次（若存在）提示：① 未跟踪/未版本管理候选是否 `git add`/`svn add`；② 是否按 `Id` 排除。每一步都会在 `-PromptTimeoutSeconds` 后自动落默认值，因此无人值守时不会卡住；仍可用 `-AddIds`、`-ExcludeIds`、`-ExcludePaths` 跳过对应提问（与脚本实现一致）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -Interactive -PromptTimeoutSeconds 30
```

交互窗口中的含义：

- 步骤 1/2：未纳入版本管理的候选文件。直接按 `1` 不加入（默认），按 `2` 全部加入，按 `3` 输入编号选择加入；这一步按键立即生效，不需要回车。
- 步骤 2/2：会进入本次提交日志的改动项。直接按 `1` 全部保留（默认），按 `2` 输入编号排除；这一步按键立即生效，不需要回车。
- 未在步骤 1 选择加入的未跟踪文件不会出现在步骤 2，也不会进入提交日志。
- 步骤 2/2 显示的是脚本启动时的 Git/SVN 状态快照；如果刚保存文件或 Git 面板刚刷新，发现列表缺文件，关闭窗口后重新运行 skill。
- 输入编号时可写 `3,5,8` 或 `3 5 8`；窗口会显示倒计时，超时或空输入表示不选择任何编号。

**非交互 / CI / 用户明确要求不要弹窗**：无控制台提问，直接按默认策略取 JSON（不加入未跟踪 / 不排除）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive
```

实现要点：SVN 已跟踪用 `svn status --xml -q`；`NeedsAdd` 仍可由第二次全树 `svn status --xml` 得到；超大库可用 `-ScanUntrackedForNeedsAdd false`。

常用参数：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -IncludeDiff false
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -MaxFilesWithDiff 60 -MaxDiffBytesPerFile 20480
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -Svn false
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -UseDefaultExcludes false
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -ScanUntrackedForNeedsAdd false
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -PromptTimeoutSeconds 20
```

JSON 关键字段（与脚本一致）：

- **`ItemsIncludedDefaultLog[]`**：已纳入版本库、将写入「默认/最终」叙事主线的路径（不含 `??` / svn `unversioned`）
- **`ProjectsDefault[]`**：由上一字段聚合
- `ItemsAll[]`、`ItemsIncluded[]`、`ItemsExcluded[]`、`NeedsAdd[]`、`Projects[]`、`Diffs`：含义同脚本输出；`Defaults` 中含 `NonInteractive`、`PromptTimeoutSeconds`、`ConsoleChoiceUsed`

`NeedsAdd[]` 候选类型规则（脚本侧 `Is-CommonAddCandidate` 与历史技能一致）：常见源码、前端、Markdown、脚本、工程配置、接口/schema 等文本；二进制与产物目录不进入。

### Step 2) 模型生成提交日志

仅凭 Step 1 的 JSON：以 **`ItemsIncludedDefaultLog`**、**`ProjectsDefault`**、**`Diffs`**（及必要时 **`ItemsExcluded`**）归纳一条提交说明；**不得**再发起聊天内「加入未跟踪 / 排除」流程（已在脚本的限时交互或显式 `-NonInteractive` 中落定）。

### 提交日志正文要点

- 只输出一条 `type(scope): summary`；多项目 scope 用顿号 `、` 连接。
- 正文 2–8 条 `- ` 条目；若 `ItemsExcluded` 非空，附 `本次排除清单`（`#Id Path`）。

### Step 3) 限时确认是否提交并推送

生成最终提交日志后，模型默认调用提交/推送 wrapper。它会打开本机可见 PowerShell，显示提交日志、Git/SVN 文件数量和倒计时：

- 按 `1`：提交并推送（默认，超时自动执行）。
- 按 `2`：暂不提交。
- Git 文件：脚本会按 `ItemsIncludedDefaultLog` 暂存对应 Git 路径，执行 `git commit -F <message>`，然后用非交互凭据模式 `git push`。
- SVN 文件：若 `ItemsIncludedDefaultLog` 中包含 SVN 路径，脚本会执行非交互 `svn commit -F <message>`；SVN 没有 push。

模型需要把 Step 1 原始 JSON 和 **Step 2 生成的最终提交日志原文** 分别写入临时文件，再调用。`$finalCommitLog` 必须就是模型最终给用户展示的提交日志标题+正文，不得使用占位符、核对表、说明文字，也不得让提交脚本重新生成另一份日志。不要在 Bash/WSL 执行器里拼多行 `powershell -Command`，也不要直接调用 `run-commit-push-choice.ps1`；统一调用 `-File` wrapper，wrapper 会阻塞等待提交推送窗口结束：

```powershell
$changesFile = Join-Path $env:TEMP ("git_svn_changes_{0}.json" -f ([guid]::NewGuid()))
$messageFile = Join-Path $env:TEMP ("git_svn_commit_message_{0}.txt" -f ([guid]::NewGuid()))
Set-Content -LiteralPath $changesFile -Value $changesJsonRaw -Encoding UTF8
Set-Content -LiteralPath $messageFile -Value $finalCommitLog -Encoding UTF8
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-commit-push-interactive.ps1 -ChangesJsonFile $changesFile -CommitMessageFile $messageFile -PromptTimeoutSeconds 30
```

若用户明确要求“只生成日志 / 不提交 / 不推送”，模型跳过 Step 3。

## 输出要求（最终给用户的内容）

1. **默认提交日志**：基于 `ItemsIncludedDefaultLog` / `ProjectsDefault` / 相关 `Diffs`。
2. **推荐**在默认日志前附 **`### 待提交文件核对`** Markdown 表（`| # | 状态 | 路径 | 来源 | 项目 |`，行与 `ItemsIncludedDefaultLog` 一一对应；Git `状态` 为 `GitIndexStatus`+`GitWorktreeStatus` 两字符拼接；SVN 为 `SvnItem`）。
3. **提交/推送结果**：若执行 Step 3，简要说明 `completed` / `skipped` / `failed`，失败时列出失败命令摘要。
4. **收尾**：在回复末尾再附 **`### 最终版提交日志（可直接复制）`**，用 ```text 完整贴一遍标题+正文（可与默认版相同，须全文便于从底部复制）。
5. 条目换行、scope/type 细则见下文「## type / scope / 条目生成规则」。

## type / scope / 条目生成规则（落地细则）

### type 推断（优先级从高到低）

- **docs**：只改 `*.md`/文档目录/注释类内容\n
- **test**：只改测试项目/测试文件\n
- **ci**：只改 CI 配置/流水线脚本\n
- **build**：改构建脚本、依赖、打包产物引用、工程配置（如引入 DLL/nuget 更新）\n
- **fix**：修复 bug、兼容性问题、异常/边界条件\n
- **feat**：新增可见功能/新增流程\n
- **refactor**：重构（不改变外部行为）\n
- **perf**：性能优化\n
- **style**：格式化/不影响语义的改动\n
- **chore**：杂项维护（不属于以上）\n

> 允许扩展类型，但务必保证：扩展类型对团队有稳定含义，并避免滥用。

### scope 选择

- 默认使用 `Projects[].Scope`（脚本给出的项目名；来自就近 `*.csproj`，否则顶层目录名）。\n
- 若一个项目内改动明显集中在某功能子模块，可输出 `Project-Module` 形式的 scope，但不要过长。
- 若涉及多个项目，将 scope 用中文顿号 `、` 连接，例如 `MftScanner.Core、MftScanner`；scope 只负责说明范围，不表示要拆成多条提交。

### summary 写法

一句话写清楚：**改了什么** + **解决什么问题/为什么要改**。\n
例：\n
- `fix(埋管绘制): 修复生成埋管与绘制线型不一致导致的 L 线方向错误问题`
- `feat(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化IPC共享内存序列化`

### 条目写法

- 建议 2–8 条，围绕“关键变更点/行为差异/兼容性/风险点”。\n
- 使用 `- ` 在内容前，不要使用 `1、2、3` 编号。\n
- 避免“改了 A 文件/改了 B 文件”这类低信息量条目，优先写行为与影响。

### 长文本换行规则

提交日志必须按下面规则主动换行，尤其是包含多个模块名、长类型名、方法名、IPC/诊断字段、路径或多段分号描述时：

- 标题行优先压缩 summary，目标不超过 120 个显示列；标题过长时缩短描述，不要把标题拆成多行。
- 正文每个 `- ` 条目的首行目标不超过 100 个显示列；超过时必须硬换行。
- 条目续行使用两个空格缩进，不再重复 `- `，例如：
  `- 第一段较长内容，按语义换行`
  `  第二段续行内容`
- 优先在中文标点和语义边界处断行：`，`、`；`、`、`、`：`、`与`、`并`、`通过`、`新增`、`支持` 等。
- 不要把同一个长条目塞满多个独立变更点；如果一个条目里出现多个 `；` 或多个“新增/支持/优化/修复”，优先拆成 2 条。
- 代码标识符、方法名、协议名、路径、诊断字段名尽量保持完整；只有单个 token 极长且无法避免时才在 token 内断开。
- 不要在正文列表前额外加两个空格；顶层 bullet 必须从行首 `- ` 开始，只有续行缩进两个空格。

长日志改写示例：

```text
feat(MftScanner.Core、MftScanner、PackageManager): 增加软删除覆盖层与异步搜索调度

- MemoryIndex 新增 _deletedOverlayKeys 软删除覆盖层，
  MarkDeleted/IsDeleted/HasDeletedOverlay 替代物理数组移除
- Insert 时自动清除覆盖标记，所有搜索函数统一增加
  IsSearchVisible 过滤，避免已删除项继续参与匹配
- IndexService 将路径前置过滤拆分为 postings-first-drive、
  postings-first、drive-filter、path-first、post-filter 五级策略
- EverythingSearchWindow 删除操作先本地移除结果，
  再异步调用 NotifyIndexDeletedAsync 通知索引
```

示例格式：

```text
feat(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化IPC共享内存序列化

- MftEnumerator 新增 _childDirectoryFrnsByParent 父子目录映射，
  TryGetDirectorySubtree 可将路径前缀定位到目录子树 FRN 集合
- MemoryIndex 新增 ParentSortedArray 按父目录排序数组，
  GetSubtreeCandidates 通过二分查找高效提取子树候选集
- IndexService 搜索路径前缀时优先使用目录子树前置过滤，
  解析失败时回退到原有后置过滤，并输出 PATH PREFILTER 诊断日志
- SharedIndexMemoryProtocol 移除多余 Flush()，
  用 ThreadStatic 缓冲区优化字符串序列化并返回请求/响应字节数
- SharedIndexServiceClient 将 WaitForResponseAsync 从阻塞式
  Task.Run+WaitHandle 改为 RegisterWaitForSingleObject+TaskCompletionSource
- MFT 全卷枚举失败时保留现有索引继续服务；Contains 桶预热可配置跳过短查询桶
```
