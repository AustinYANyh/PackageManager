---
name: git-svn-commitlog-generator
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

1. **脚本 JSON 输出是改动范围与用户选择的唯一数据源**：模型不得自行递归扫描目录来决定「纳入/排除哪些文件」。但生成提交日志时必须理解具体变更；若 `Diffs` 缺失、为空或不足以判断行为变化，模型必须只针对 `ItemsIncludedDefaultLog` 中未排除的路径补取只读差异或读取文件内容，不能只凭路径和项目名编造日志。
2. **交互在脚本内完成且必须可超时**：是否将 `NeedsAdd` 纳入版本库、是否排除提交项，由 `get-working-changes.ps1` 在 **Windows 可交互控制台** 内处理。脚本优先打开勾选表格；GUI 不可用时才回退编号输入。窗口会显示剩余秒数；点击“确定”或超时都会进入下一步。若超时时当前有勾选，则按当前勾选结果继续；若当前没有任何勾选，则采用默认值（不加入未跟踪 / 全部保留不排除）。回退到编号输入时，输入行同样按 `-PromptTimeoutSeconds` 超时，超时或空输入视为不选择任何 Id。**禁止**由模型在聊天里用 `Start-Sleep`、阻塞式原生选项菜单或「伪后置步骤」替代脚本交互。
3. **模型默认限时交互调用**：生成提交日志时，模型默认必须打开本机可见 PowerShell 运行脚本 **`-Interactive -PromptTimeoutSeconds 30`**，给用户一次加入/排除机会；无人操作则脚本自动按默认项继续。只有用户明确要求“非交互 / CI / 直接生成 / 不要弹窗”时，才使用 **`-NonInteractive`**。
4. **一次只生成一条提交日志**：即使涉及多个项目，也不要拆成多条提交；多项目只在 `scope` 中写清楚，并在正文条目中说明各项目关键改动。
5. **不要粘贴大段 diff**：提交日志只输出抽象后的变更点，不直接贴 patch 内容。
6. **长日志必须主动硬换行**：标题与正文按后文「长文本换行规则」处理。
7. **最终提交/推送也必须限时交互**：模型生成最终提交日志后，默认必须打开本机可见 PowerShell 询问是否提交并推送；`-PromptTimeoutSeconds` 默认 30 秒，超时自动选择 **第 1 个选项：提交并推送**。只有用户明确要求“只生成日志 / 不提交 / 不推送”时才跳过这一步。

## 执行流程

### Step 1) 获取 Git/SVN 待提交改动（脚本唯一来源）

**模型默认（推荐）**：调用 wrapper 脚本打开本机可见 PowerShell，让脚本完成中文限时交互，并把 JSON 回传给模型；窗口退出后模型读取该 JSON 生成提交日志。Claude Code 当前只有 Bash 工具时，允许用 Bash 调 `powershell.exe` 启动 wrapper，但命令行不得包含中文提交日志正文。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-working-changes-interactive.ps1 -PromptTimeoutSeconds 30
```

若当前工具显示为 `Bash(...)`，只允许执行这种固定短命令；不要在 Bash 中拼接多行 `powershell -Command`。

默认生成提交日志时不得主动传 `-IncludeDiff false`。脚本会先完成未跟踪加入与排除交互，再只对最终 **`ItemsIncludedDefaultLog`** 范围读取 diff，避免对所有候选文件读 diff。只有用户明确要求“只看文件列表 / 快速跳过 diff / 不需要精准日志”时才允许关闭 diff；如果关闭了 diff，Step 2 必须按 `ItemsIncludedDefaultLog` 补充只读差异或文件内容后再写日志。

**人类在 Windows 本机终端**：需要脚本内交互时，使用 **`-Interactive`**（且 stdin 未重定向），脚本会依次（若存在）提示：① 未跟踪/未版本管理候选是否 `git add`/`svn add`；② 是否排除本次提交项。每一步都会在 `-PromptTimeoutSeconds` 后自动落默认值，因此无人值守时不会卡住；仍可用 `-AddIds`、`-ExcludeIds`、`-ExcludePaths` 跳过对应提问（与脚本实现一致）。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -Interactive -PromptTimeoutSeconds 30
```

交互窗口中的含义：

- 步骤 1/2：未纳入版本管理的候选文件。默认打开勾选窗口；**勾选表示加入本次提交，未勾选表示不加入**。点击“确定”或超时都会进入下一步；超时时如果有勾选就按当前勾选加入，如果没有勾选就按默认不加入任何未跟踪文件；点击“使用默认”同样表示不加入。若 GUI 不可用则回退到编号输入：直接按 `1` 不加入（默认），按 `2` 全部加入，按 `3` 输入编号选择加入。
- 步骤 2/2：会进入本次提交日志的改动项。默认打开勾选窗口；**勾选表示排除，未勾选表示保留**。点击“确定”或超时都会进入下一步；超时时如果有勾选就按当前勾选排除，如果没有勾选就按默认全部保留；点击“使用默认”同样表示全部保留。若 GUI 不可用则回退到编号输入。
- 未在步骤 1 选择加入的未跟踪文件不会出现在步骤 2，也不会进入提交日志。
- 步骤 2/2 显示的是脚本启动时的 Git/SVN 状态快照；如果刚保存文件或 Git 面板刚刷新，发现列表缺文件，关闭窗口后重新运行 skill。
- 输入编号时可写 `3,5,8` 或 `3 5 8`；窗口会显示倒计时，超时或空输入表示不选择任何编号。

**非交互 / CI / 用户明确要求不要弹窗**：无控制台提问，直接按默认策略取 JSON（不加入未跟踪 / 不排除）。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive
```

实现要点：Git 已跟踪用 `git status --porcelain=v1 -z --untracked-files=no`；SVN 已跟踪需要用 `svn status --xml -v` 建立完整路径集合，再用第二次全树 `svn status --xml` 采集 `unversioned` 候选；超大库可用 `-ScanUntrackedForNeedsAdd false`。

常用参数：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -IncludeDiff false
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -MaxFilesWithDiff 60 -MaxDiffBytesPerFile 20480
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -Svn false
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -UseDefaultExcludes false
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -ScanUntrackedForNeedsAdd false
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -NonInteractive -PromptTimeoutSeconds 20
```

JSON 关键字段（与脚本一致）：

- **`ItemsIncludedDefaultLog[]`**：已纳入版本库、将写入「默认/最终」叙事主线的路径（不含 `??` / svn `unversioned`）
- **`ProjectsDefault[]`**：由上一字段聚合
- `ItemsAll[]`、`ItemsIncluded[]`、`ItemsExcluded[]`、`NeedsAdd[]`、`Projects[]`、`Diffs`：含义同脚本输出；`Defaults` 中含 `IncludeDiff`、`MaxDiffBytesPerFile`、`MaxFilesWithDiff`、`NonInteractive`、`PromptTimeoutSeconds`、`ConsoleChoiceUsed`

`NeedsAdd[]` 候选类型规则（脚本侧 `Is-CommonAddCandidate` 与历史技能一致）：常见源码、前端、Markdown、脚本、工程配置、接口/schema 等文本；二进制与产物目录不进入。

### Step 2) 模型生成提交日志

以 Step 1 的 JSON 确定最终范围：只处理 **`ItemsIncludedDefaultLog`** 中的文件，并尊重 **`ItemsExcluded`**。**不得**再发起聊天内「加入未跟踪 / 排除」流程（已在脚本的限时交互或显式 `-NonInteractive` 中落定）。

生成日志前必须确认有足够信息理解具体变更：

- 优先使用 **`Diffs`** 中的 `unstaged` / `staged` / `patch` 内容归纳行为变化；脚本默认只为最终 `ItemsIncludedDefaultLog` 范围填充 diff。
- 如果 `Diffs` 对相关 Id 不存在、值为空、被截断或仅凭 diff 仍无法判断语义，模型必须补充执行只读检查：可以对 `ItemsIncludedDefaultLog` 的路径运行 `git diff -- <path>`、`git diff --cached -- <path>`、`svn diff -- <path>`，或读取这些路径的当前文件内容。
- 补充检查只能覆盖 `ItemsIncludedDefaultLog` 里的路径；不得重新扫描全仓、不得把未纳入或已排除的文件写进提交日志。
- 只读检查允许使用 `git diff` / `svn diff` / 读取文件；仍禁止模型执行 `git add`、`git commit`、`git push`、`svn add`、`svn commit` 等会改变状态或提交推送的命令。
- 若一个被纳入文件仍无法取得内容或差异，应在内部降低该文件权重；不要编造具体行为。

### 提交日志正文要点

- 只输出一条 `type(scope): 中文摘要`；多项目 scope 用顿号 `、` 连接。
- 正文 2–8 条 `- ` 条目；若 `ItemsExcluded` 非空，附 `本次排除清单`（`#Id Path`）。

### Step 3) 限时确认是否提交并推送

生成最终提交日志后，模型默认调用提交/推送 wrapper。它会打开本机可见 PowerShell，显示提交日志、Git/SVN 文件数量和倒计时：

- 按 `1`：提交并推送（默认，超时自动执行）。
- 按 `2`：暂不提交。
- Git 文件：脚本会按 `ItemsIncludedDefaultLog` 暂存对应 Git 路径，执行 `git commit -F <message>`，然后以非交互方式执行 `git push`；如需凭据则直接失败返回，不阻塞。
- SVN 文件：若 `ItemsIncludedDefaultLog` 中包含 SVN 路径，脚本会在同一个可见窗口执行 `svn commit -F <message>`；SVN 输出/提示显示在窗口中，不会污染 JSON，SVN 没有 push。
- Step 3 的结果 JSON 由 wrapper 指定的结果文件产生；模型不得解析提交窗口输出，也不得临时创建提交 `.ps1` 替代 wrapper。

Step 1 wrapper 会自动把采集 JSON 保存到 `.claude/skills/git_svn_commitlog_generator/.state/last_changes.json`。

模型只负责生成 **Step 2 的最终提交日志文本**。Claude Code Bash 工具无法可靠传递中文参数，也不能用 `python -c` / `node -e` / `powershell -Command` 在 Bash 内处理中文。**因此模型必须在自身推理中直接得到最终提交日志的 UTF-8 Base64 字符串**，Step 3 命令行只传 ASCII Base64 给 `-CommitMessageBase64Utf8`。

`-CommitMessageBase64Utf8` 内容必须只包含最终提交日志标题+正文的 UTF-8 Base64，不得包含核对表、说明文字或占位符。模型不得使用 stdin、pipe、heredoc、重定向来传提交日志，不得为了传日志而单独创建或编辑提交日志/Base64 文件（包括 `commit_msg_b64.txt`、`final_commit_message.txt` 等），不得把提交日志作为 `-CommitMessageText` 长参数塞进 Bash/WSL 命令行，不得在 Bash 中执行 `python -c` / `node -e` / `powershell -Command` 等编码命令来处理中文，不得执行 `git add` / `git commit` / `git push` / `svn add` / `svn commit` 等会改变状态或提交推送的命令，不得创建临时 `.ps1` 提交脚本，也不得直接调用 `run-commit-push-choice.ps1`。

不得因为“无法确信 Base64 与中文原文逐字一致”而跳过 Step 3；Step 3 窗口会显示脚本实际解码后的提交日志和 SHA256，并由用户按 `1` 提交或按 `2` 取消。模型仍不得调用任何 shell 命令来“辅助计算” Base64。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-commit-push-interactive.ps1 -PromptTimeoutSeconds 30 -CommitMessageBase64Utf8 '<模型已在推理中算好的 ASCII Base64>'
```

兼容入口：`-CommitMessageLines`、`-CommitMessageText` 仅供 Windows 原生命令行或自动化脚本使用，不再作为 Claude Code 默认路径。最终提交日志标题和正文都必须使用中文写法，不要照搬英文模板。

Step 3 返回 JSON 中的 `CommitMessage` 与 `CommitMessageSha256` 是脚本实际用于 `git commit -F` / `svn commit -F` 的内容。模型最终回复里的“最终版提交日志（可直接复制）”必须逐字复制 `CommitMessage`，不得再根据记忆或上文重新生成一份；如果发现它和准备展示的日志不同，必须报告 `failed` 并停止。

若执行器是 Bash/WSL，`powershell` 常会因为命令不存在返回 `errorcode 127`；应显式调用 `powershell.exe`。Step 3 在 Bash/WSL 中执行时，命令行只能包含 ASCII Base64，不能包含任何中文提交日志正文或中文编码命令。Step 3 内部若找不到 `git`/`svn`，结果 JSON 会以 `exitCode=127` 返回并写明缺失命令。

若用户明确要求“只生成日志 / 不提交 / 不推送”，模型跳过 Step 3。

## 输出要求（最终给用户的内容）

1. **默认提交日志**：基于 `ItemsIncludedDefaultLog` / `ProjectsDefault` / 相关 `Diffs`，并在 `Diffs` 为空或不足时基于补充只读差异/文件内容。
2. **推荐**在默认日志前附 **`### 待提交文件核对`** Markdown 表（`| # | 状态 | 路径 | 来源 | 项目 |`，行与 `ItemsIncludedDefaultLog` 一一对应；Git `状态` 为 `GitIndexStatus`+`GitWorktreeStatus` 两字符拼接；SVN 为 `SvnItem`）。
3. **提交/推送结果**：若执行 Step 3，简要说明 `completed` / `skipped` / `failed`，失败时列出失败命令摘要。
4. **收尾**：若执行了 Step 3，末尾 **`### 最终版提交日志（可直接复制）`** 必须逐字复制 Step 3 结果 JSON 的 `CommitMessage`；若未执行 Step 3，复制 Step 2 的 `$finalCommitLog`。用 ```text 完整贴一遍标题+正文，须全文便于从底部复制。
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

### 摘要写法

一句话写清楚：**改了什么** + **解决什么问题/为什么要改**。\n
例：\n
- `修复(git_svn_commitlog_generator): 改用 Base64 UTF-8 传递提交日志，消除 Bash eval 中文问题`
- `功能(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化 IPC 共享内存序列化`

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
功能(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化 IPC 共享内存序列化

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
