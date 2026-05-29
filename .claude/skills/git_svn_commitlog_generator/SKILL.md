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

- `type`：必须是英文小写 conventional 类型标识，例如 `docs`、`test`、`ci`、`build`、`fix`、`feat`、`refactor`、`perf`、`style`、`chore`。**禁止翻译成中文**，不得写成 `修复`、`功能`、`性能` 等中文词。
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
4. **提交日志按版本库提交组生成**：默认只生成一条提交日志；但当 `ItemsIncludedDefaultLog` 横跨多个独立 Git 仓库或多个真实 SVN 提交组时，必须按 `CommitGroupsDefault[]` 分别生成日志。SVN 提交组按同一次 `svn commit` 可提交的仓库标识合并，优先使用 `SvnRepoUuid`，其次 `SvnRepoRootUrl`，最后才使用 `SvnWcRoot`；同一 SVN 提交组内的多个项目/目录只写一条日志，scope 用顿号合并。每个日志只描述该组自己的文件改动，不得把其他仓库/提交组的改动写进去。同一 Git 仓库内不得按文件夹或模块拆成多条提交。
5. **不要粘贴大段 diff**：提交日志只输出抽象后的变更点，不直接贴 patch 内容。
6. **提交日志默认紧凑，长文本才按语义换行**：标题与正文优先保持单行可读；只有单条内容明显过长、信息密集并影响阅读时，才按后文「长文本换行规则」处理。禁止为了排版整齐把短标题或短 bullet 拆成多行。
7. **最终提交/推送也必须限时交互**：模型生成最终提交日志后，默认必须打开本机可见 PowerShell 询问是否提交并推送；`-PromptTimeoutSeconds` 默认 30 秒，超时自动选择 **第 1 个选项：提交全部提交组**。只有用户明确要求“只生成日志 / 不提交 / 不推送”时才跳过这一步。
8. **提交日志 type 必须保持英文**：无论摘要和正文是否为中文，标题开头 `type(scope):` 中的 `type` 都必须是英文小写标识。Step 3 前必须自检标题；若 `(` 前不是英文类型，必须先改正，禁止提交。

## 执行流程

### Step 1) 获取 Git/SVN 待提交改动（脚本唯一来源）

**模型默认（推荐）**：调用 wrapper 脚本打开本机可见 PowerShell，让脚本完成中文限时交互，并把 JSON 回传给模型；窗口退出后模型读取该 JSON 生成提交日志。Claude Code 当前只有 Bash 工具时，允许用 Bash 调 `powershell.exe` 启动 wrapper，但命令行不得包含中文提交日志正文。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File <skill-root>/scripts/invoke-working-changes-interactive.ps1 -PromptTimeoutSeconds 30
```

由 PackageManager 启动时，`<skill-root>` 是 EXE 内嵌 skill 解压后的绝对目录（通常在 `%LocalAppData%\PackageManager\Skills\git_svn_commitlog_generator`），不得改读仓库 `.claude/skills/...` 下的旧 skill 或旧 `.state`。

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

实现要点：Git 已跟踪用 `git status --porcelain=v1 -z --untracked-files=no`；SVN 已跟踪用 `svn status --xml -q`，不会枚举 `normal` 文件；`NeedsAdd` 默认开启，通过 Git `??` 与 SVN `unversioned` 收集常见源码/配置候选，供步骤 1 勾选加入。极端大库只想看已管理改动时，可用 `-ScanUntrackedForNeedsAdd false`。

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
- **`CommitGroupsDefault[]`**：由上一字段按真实提交单元聚合；Git 按 `GitRepoRoot`，SVN 按同一次 `svn commit` 可提交的仓库标识合并
- **`ProjectsDefault[]`**：由上一字段按项目聚合，只用于辅助推导 scope，不用于决定拆分几条日志
- Git 条目会包含 `GitRepoRoot`、`GitRepoRelRoot`、`GitRepoPath`；嵌套 Git 仓库会按自己的 repository root 采集状态和 diff。
- `ItemsAll[]`、`ItemsIncluded[]`、`ItemsExcluded[]`、`NeedsAdd[]`、`Projects[]`、`Diffs`：含义同脚本输出；`Defaults` 中含 `IncludeDiff`、`MaxDiffBytesPerFile`、`MaxFilesWithDiff`、`NonInteractive`、`PromptTimeoutSeconds`、`ConsoleChoiceUsed`

模型读取优先级：

- PackageManager / wrapper 会同时保存完整采集结果 `.state/last_changes.json` 与轻量模型视图 `.state/last_changes_model.json`。
- **模型生成提交日志时必须优先读取 `last_changes_model.json`**；wrapper 的 stdout 也返回同一份轻量 JSON。它只包含 `Root`、`Defaults`、`Counts`、`ItemsIncludedDefaultLog`、`CommitGroupsDefault`、`ItemsExcluded`、轻量 `ProjectsDefault` 与对应 `Diffs`，避免大仓库下读取完整 JSON 变慢。
- 完整 `last_changes.json` 保留给 Step 3 提交脚本使用；模型只有在轻量视图缺失、字段不完整或需要排查脚本协议问题时才读取完整 JSON。

`NeedsAdd[]` 候选类型规则（脚本侧 `Is-CommonAddCandidate` 与历史技能一致）：常见源码、前端、Markdown、脚本、工程配置、接口/schema 等文本；二进制与产物目录不进入。

### Step 2) 模型生成提交日志

以 Step 1 的 JSON 确定最终范围：只处理 **`ItemsIncludedDefaultLog`** 中的文件，并尊重 **`ItemsExcluded`**。**不得**再发起聊天内「加入未跟踪 / 排除」流程（已在脚本的限时交互或显式 `-NonInteractive` 中落定）。

生成日志前必须确认有足够信息理解具体变更：

- 优先使用 **`Diffs`** 中的 `unstaged` / `staged` / `patch` 内容归纳行为变化；脚本默认只为最终 `ItemsIncludedDefaultLog` 范围填充 diff。
- 如果 `Diffs` 对相关 Id 不存在、值为空、被截断或仅凭 diff 仍无法判断语义，模型必须补充执行只读检查：可以对 `ItemsIncludedDefaultLog` 的路径运行 `git diff -- <path>`、`git diff --cached -- <path>`、`svn diff -- <path>`，或读取这些路径的当前文件内容。
- 补充检查只能覆盖 `ItemsIncludedDefaultLog` 里的路径；不得重新扫描全仓、不得把未纳入或已排除的文件写进提交日志。
- 只读检查允许使用 `git diff` / `svn diff` / 读取文件；仍禁止模型执行 `git add`、`git commit`、`git push`、`svn add`、`svn commit` 等会改变状态或提交推送的命令。
- 若一个被纳入文件仍无法取得内容或差异，应在内部降低该文件权重；不要编造具体行为。
- **日志归纳必须逐文件收敛**：模型必须把 `ItemsIncludedDefaultLog` 视为唯一提交范围，按其中实际文件逐项归纳变更点；你拿到多少个文件，就只允许按这多少个文件归纳日志。
- **禁止混入范围外项目/模块**：提交日志标题里的 `scope` 和正文里显式点名的项目/模块，必须都能在该条日志对应的 `ItemsIncludedDefaultLog` 子集里找到来源；如果某个项目名不在本轮该组文件内，即使它出现在上一轮采集、历史上下文或你的记忆里，也必须删掉并重写日志。
- `CommitGroupsDefault[]` 是判断是否需要多条提交日志的唯一分组依据；`ProjectsDefault[]` 只能作为从 `ItemsIncludedDefaultLog` 派生出的辅助聚合结果，不能反向扩展提交范围，也不能把同一 SVN 提交组拆成多条日志。若 `ProjectsDefault` 与逐文件判断出现任何冲突，以 `ItemsIncludedDefaultLog` 的具体文件列表为准。

### 提交日志正文要点

- 默认只输出一条 `type(scope): 中文摘要`；`type` 必须为英文小写 conventional 类型，`scope` 可含项目/模块名，多项目 scope 用顿号 `、` 连接。
- 如果 `CommitGroupsDefault[]` 中存在多个提交组，必须为每个提交组各生成一条日志，并准备 `CommitMessageGroupsBase64Utf8`。每条日志只基于该提交组自己的 `Items[]`/`Files[]` 子集和对应 `Diffs`；禁止只传单条全局日志让脚本复用到所有组。若只有一个 SVN 提交组，即使包含多个 SVN 项目/目录，也只能生成一条 SVN 日志。
- 正文建议 2–5 条 `- ` 条目，复杂或文件较多时最多 8 条；条目数不得超过该提交组 `Items[]` 文件数。若 `ItemsExcluded` 非空，附 `本次排除清单`（`#Id Path`）。
- 正文必须紧凑输出：每条都要尽量一句话说清楚，能删实现细节变短就删，不要主动插入续行。
- 条目必须按“主题/行为”聚合，不按文件逐条列变更；多个文件服务同一主题时必须合并成一句。允许点名关键文件，但禁止把文件名、函数名、属性名或 JSON 字段名反复作为条目主语展开。
- 多提交组时，**先按 `CommitGroupsDefault[]` 列文件，再写该组日志**；不得按 `ProjectsDefault[]`、目录名或历史上下文机械拆组。
- 如果某个提交组最终只有 16 个文件，就只允许围绕这 16 个文件归纳 `scope`、摘要和条目；不得因为同仓里还有别的改动、上一轮出现过别的项目名，或 `Diffs` 里能联想到别的模块，就把它们写进这条日志。

### 紧凑与长文本换行规则（自检前必须先应用）

提交日志默认采用紧凑版写法；换行是例外，不是常规格式：

- 标题必须保持单行，且保留按改动文件推导出的完整 scope；标题过长时压缩 summary，不得删除 scope，也不得把标题拆成多行。
- 正文建议 2–5 条，复杂或文件较多时最多 8 条，且不得超过该提交组文件数；改动简单时 2 条即可，不得为了显得全面而凑条目。
- 每条 bullet 必须优先写成紧凑的一句话，围绕“关键变更点/行为差异/兼容性/风险点”；禁止“改了 A / 改了 B”“更新规则”“优化流程”“修复问题”这类空泛条目。
- 先按主题聚合，再写条目；同一功能链路跨多个文件时必须合并，不能为了覆盖文件而流水账。
- 实现细节要克制：出现“新增某函数 / 某属性 / 某字段 / 某 JSON 字段 / 某脚本新增某函数”时，必须优先判断是否可删、可并入主题或可改写为用户可理解的行为影响。
- 用户偏好的目标风格：每一条都能一句话说清楚；实现细节说得太多要删；同一主题多个文件合并写。条目数量不是越少越好，但每条都必须有独立主题价值。
- 单条长度要克制：每条 bullet 目标为 40 个中文字符左右或更短；即使没有实际换行，只要视觉上已经接近终端换行边界，也算过长，必须继续压缩。
- 压缩优先级：先删实现细节，再换短词，再合并同义表达；只有删不动且确实包含两个独立主题时才拆成两条。
- 如果一条里出现多个逗号、顿号、`并`、`与`、`以及`、`同时`，优先判断为过长；能改成短动宾结构就不要保留长复合句。
- 优先使用短动宾结构，例如 `改用...`、`收紧...`、`同步...`、`更新...`、`修复...`。文件名只有在能显著帮助定位时才出现；出现后不要再跟过多实现细节。
- 短条目必须保持单行，尤其是测试、文档、配置、构建说明等内容；禁止把一个自然短句拆成“主句 + 很短尾行”的两行。
- 正文不按固定显示列机械换行；只有单条过长、类名/接口名密集、包含多个并列技术对象且读起来拥挤时才换行。
- 不要因为中文逗号、顿号或自然停顿就换行；只有整条 bullet 已经偏长且继续阅读困难时才断行。
- 条目续行必须承接上一行语义，不能留下很短、孤立、没有必要的尾行；续行使用两个空格缩进，不再重复 `- `。
- 优先在中文标点和语义边界处断行：`，`、`；`、`、`、`：`、`与`、`并`、`通过`、`新增`、`支持` 等。
- 不要把同一个长条目塞满多个独立变更点；如果一个条目里出现多个 `；` 或多个“新增/支持/优化/修复”，优先拆成 2 条。
- 代码标识符、方法名、协议名、路径、诊断字段名尽量保持完整；只有单个 token 极长且无法避免时才在 token 内断开。
- 不要在正文列表前额外加两个空格；顶层 bullet 必须从行首 `- ` 开始，只有续行缩进两个空格。

### 输出前自检（必须执行）

模型在进入 Step 3 之前，**必须向用户展示 `### 提交日志自检`**，不得只写“自检通过”。任一项不通过都必须先重写日志，再重新展示自检结果，不得带着未通过日志进入 Step 3。

1. **标题长度**：标题是否保持单行可读？若单行显示明显拥挤，必须压缩 summary（缩短摘要、合并信息），不得删除 scope，不得拆成多行。
2. **标题 type**：`(` 前是否为英文小写 conventional 类型？若不是，必须改正。
3. **条目数量**：是否建议 2–5 条、复杂或文件较多时不超过 8 条，且不超过 `ItemsIncludedDefaultLog` 文件数？能合并则合并，不得凑数。
4. **条目质量**：是否存在“改了 A / 改了 B”、空泛口号、整条复述 diff 内容而非归纳行为？如有，必须重写或删除。
5. **条目长度**：每条是否控制在约 40 个中文字符内，且没有视觉上接近终端换行边界？若只是勉强单行但太满，必须继续压缩。
6. **紧凑性/单行性**：每条是否优先单行？是否能靠删除实现细节变短？是否可改为更短动宾短句？是否有多余续行、孤立短尾行，或在不该换行的地方换了行？如有，必须合并回单行或按语义重写。
7. **scope 归源**：标题和正文里出现的每个项目/模块名，是否都能在 `ItemsIncludedDefaultLog` 里找到来源？
8. **主题聚合**：同一功能链路跨多个文件时是否已合并？是否为了覆盖文件而按文件流水账？若是，必须合并压缩。
9. **细节克制**：函数名、属性名、字段名、JSON 字段名、脚本名是否被过度展开？若实现细节不是理解变更影响所必需，必须删掉或并入主题。

自检展示格式必须逐项列出，每项使用 `通过` / `未通过` / `已重写后通过`，并附一句具体理由。示例：

```text
### 提交日志自检
- 标题单行：通过，标题保留完整 scope 且摘要未换行。
- type：通过，使用英文小写 feat。
- 条目数量：已重写后通过，条目数不超过文件数，并从文件流水账压缩为主题条目。
- 条目质量：已重写后通过，删除了按文件名罗列的低信息量条目。
- 条目长度：已重写后通过，所有条目都未接近终端换行边界。
- 紧凑性：通过，短条目保持单行，并优先使用短动宾结构。
- scope 归源：通过，scope 均来自 ItemsIncludedDefaultLog。
- 主题聚合：通过，同一链路的脚本、C# 与文档变更按能力聚合。
- 细节克制：通过，未展开非必要函数名、属性名和 JSON 字段名。
```

以下情况不得直接判定通过：同一 `run-commit-push-choice.ps1` 相关变更拆成多条重复条目；条目围绕文件名流水账；把“新增函数 / 新增属性 / 新增字段 / 新增 JSON 字段 / 更新文档”拆成低信息量条目；条目虽然单行但已经接近终端换行边界；条目偏长但没有合并主题或压缩表达。条目数本身不是问题，话太多、实现细节过多、按文件流水账才是问题。

短句改写示例：

```text
反例：SKILL.md 细化提交日志正文规则：条目须按主题聚合、克制实现细节，并更新自检项与示例
正例：SKILL.md 收紧主题聚合与自检规则
反例：run-commit-push-choice.ps1 将审查反馈输入从逐键读取循环简化为 Read-Host，移除显式 InputEncoding 设置
正例：run-commit-push-choice.ps1 改用原生输入处理反馈
```

### Step 3) 限时确认是否提交并推送

自检通过后，模型默认调用提交/推送 wrapper。它会打开本机可见 PowerShell，按提交组显示提交日志、文件数量和倒计时：

- 按 `1`：提交全部提交组（默认，超时自动执行）。
- 按 `2`：选择提交组；随后输入组编号，只提交选中的组。
- 按 `3`：暂不提交。
- 按 `4`：提出意见并退回模型重新生成提交日志；按下 `4` 后的意见输入不设超时，输入后直接回车结束。脚本返回 `Status = "regenerate_requested"`、`Choice = 4`、`ReviewFeedbackRaw = "<用户逐字原文>"`、`ReviewFeedback = "<同一份原文>"`，不会执行任何 Git/SVN 提交命令。
- Git 文件：脚本按 `GitRepoRoot` 分组；每个 Git 仓库最多一次 `git add`、一次 `git commit`、一次 `git push`。Git 无法跨多个 `.git` 数据库生成一个 commit。
- Git 提交执行器会对 `git add` / `git commit` / `git push` 中出现的 `.git/index.lock` 失败做一次安全自愈：默认阈值 `-GitIndexLockStaleMinutes 10`，仅当锁文件超过阈值且未发现活动 `git` / `git-remote-*` / `ssh` 进程时，自动删除陈旧锁并重试当前 Git 命令一次；否则失败结果必须包含锁路径、锁龄和相关进程摘要。模型不得绕过 wrapper 手动删除锁或手动执行 Git 提交命令。
- SVN 文件：脚本按同一次 `svn commit` 可提交的仓库标识分组；同一 `SvnRepoUuid`/`SvnRepoRootUrl` 下的多个目录显示为一个 SVN 提交组并只执行一次 `svn commit`。SVN 输出/提示显示在窗口中，不会污染 JSON，SVN 没有 push。
- 任一提交组失败后停止后续组，结果 JSON 的 `Groups[]` 会标记 `completed`、`failed`、`not_started` 或 `skipped`。
- Step 3 的结果 JSON 由 wrapper 指定的结果文件产生；模型不得解析提交窗口输出，也不得临时创建提交 `.ps1` 替代 wrapper。
- 若 Step 3 返回 `regenerate_requested`，模型必须优先读取并逐字展示 `ReviewFeedbackRaw`（兼容读取 `ReviewFeedback`），不得加引号伪装、不得摘要、不得改写成自己的理解；随后回到 Step 2 基于原提交范围和用户意见重写日志，重新展示逐项自检，重新计算并验证 UTF-8 Base64，然后再次调用 Step 3。不得把 `regenerate_requested` 当作 `skipped` 或已完成。

Step 1 wrapper 会自动把完整采集 JSON 保存到 **本次执行的 skill root** 下的 `.state/last_changes.json`，并把模型轻量视图保存到 `.state/last_changes_model.json`。由 PackageManager 启动时，模型生成日志只能优先读取 prompt 中给出的绝对 `last_changes_model.json` 路径；不得读取仓库 `.claude/skills/git_svn_commitlog_generator/.state/last_changes.json`。

模型只负责生成 **Step 2 的最终提交日志文本**。所有模型/自动化调用都必须默认使用 `-CommitMessageBase64Utf8` 传递提交日志，不得因为当前执行器是 PowerShell 就改用兼容入口。Claude Code Bash 工具无法可靠传递中文参数，也不能用 `python -c` / `node -e` / `powershell -Command` 在 Bash 内处理中文。**因此模型必须在自身推理中直接得到最终提交日志的 UTF-8 Base64 字符串**，Step 3 命令行只传 ASCII Base64 给 `-CommitMessageBase64Utf8`。如果存在多个提交组，还必须传 `-CommitMessageGroupsBase64Utf8`，其内容是 UTF-8 JSON 的 Base64。

`-CommitMessageBase64Utf8` 内容必须只包含最终提交日志标题+正文的 UTF-8 Base64，不得包含核对表、说明文字或占位符。模型不得使用 stdin、pipe、heredoc、重定向来传提交日志，不得为了传日志而单独创建或编辑提交日志/Base64 文件（包括 `commit_msg_b64.txt`、`final_commit_message.txt` 等），不得把提交日志作为 `-CommitMessageText` 长参数塞进 Bash/WSL 命令行，不得在 Bash 中执行 `python -c` / `node -e` / `powershell -Command` 等编码命令来处理中文，不得执行 `git add` / `git commit` / `git push` / `svn add` / `svn commit` 等会改变状态或提交推送的命令，不得创建临时 `.ps1` 提交脚本，也不得直接调用 `run-commit-push-choice.ps1`。

不得因为“无法确信 Base64 与中文原文逐字一致”而跳过 Step 3；Step 3 窗口会显示脚本实际解码后的提交日志和 SHA256，并由用户按 `1` 提交或按 `2` 取消。模型应优先在自身推理中直接计算 UTF-8 Base64；若模型无法可靠计算含 CJK 字符的 Base64（常见情况），**允许使用 Python `chr(0xNNNN)` 兜底方案**（见下文），这是唯一许可的 shell 辅助 Base64 计算方式，其他 shell 编码命令仍在禁止之列。

### Bash 非 ASCII 兜底：Python chr() 计算 Base64

Claude Code 的 Bash 工具会将命令行中的非 ASCII 字符（含中文）在 `eval` 阶段破坏，导致命令执行失败（exit code 127）。若模型无法在推理中可靠计算含 CJK 文本的 UTF-8 Base64，可使用本兜底方案。

**原理**：用纯 ASCII 的 Python `chr(0xNNNN)` 构造提交日志字符串，再由 Python 计算 Base64 输出。整个命令行不含任何非 ASCII 字符，绕过 Bash 工具的 eval 编码问题。

**命令模板**：

```bash
python -c “import base64;cp=[0x64,0x6F,...,0x63D0,0x4EA4,...,0x0A];s=''.join(chr(c) for c in cp);print(base64.b64encode(s.encode()).decode())”
```

- `cp=[]` 内是提交日志每个字符的 Unicode 码点（ASCII 字符直接写 `0xHH`，CJK 字符写 `0xNNNN`，换行写 `0x0A`）。
- 逐字符列出码点，不得省略、不得用原始中文替代。
- 命令行必须为纯 ASCII；任何原始中文字符都会导致 `eval` 失败。

**使用流程**：

1. 模型在 Step 2 生成最终中文提交日志文本。
2. 将日志逐字符转为 Unicode 码点数组 `cp=[...]`。
3. 执行上述 Python 命令，获取 Base64 字符串。
4. **必须验证**：用 `python -c “import base64;print(base64.b64decode('<得到的Base64>').decode('utf-8'))”` 回解码，确认与 Step 2 原文逐字一致。
5. 验证通过后，用该 Base64 调用 Step 3 的 `invoke-commit-push-interactive.ps1 -CommitMessageBase64Utf8`。

**多提交组**时，对每组分别执行上述流程，再将各组 Base64 组装为 JSON 并整体编码为 `CommitMessageGroupsBase64Utf8`（同样用本方案计算该 JSON 的 Base64）。

若 `-CommitMessageBase64Utf8` 在 Step 3 返回”不是合法的 UTF-8 Base64 提交日志”、解码后文本与 Step 2 原日志不一致，或窗口回显的 `CommitMessage` 明显缺字、乱码、错行，模型必须把这次 Step 3 视为**传参失败**，而不是日志需要降级：必须回到 Step 2 的最终中文日志逐字核对标题、空行、bullet 与全文内容，重新得到准确的 UTF-8 Base64 后再次发起 Step 3。必要时可以连续重试多次，直到解码结果与原日志逐字一致，或用户明确要求停止；**禁止**为了“先通过解码/先弹出确认窗口”擅自把日志改写成更短、更空泛或信息降级的版本，尤其禁止把原本基于 diff 得出的具体摘要和条目替换成“更新规则”“优化流程”“修复问题”之类无法审阅的空话。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-commit-push-interactive.ps1 -PromptTimeoutSeconds 30 -CommitMessageBase64Utf8 '<模型已在推理中算好的 ASCII Base64>'
```

需要调整 Git 锁陈旧判定阈值时，可额外传入 `-GitIndexLockStaleMinutes <分钟数>`；默认保持 10 分钟，不需要日常显式传参。

多提交组时额外传入：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/invoke-commit-push-interactive.ps1 -PromptTimeoutSeconds 30 -CommitMessageBase64Utf8 '<默认/总览日志 Base64>' -CommitMessageGroupsBase64Utf8 '<提交组日志 JSON 的 Base64>'
```

多提交组场景下，Step 3 会校验每个提交组是否都匹配到 `Groups[].CommitMessage`；若缺少任一组日志，脚本必须在执行提交前返回 `failed`，不得把默认/总览日志套用到该组。

提交组 JSON 形状；SVN 组优先传 `SvnRepoUuid` 或 `SvnRepoRootUrl`，缺失时才传 `SvnWcRoot`：

```json
{
  "Groups": [
    {
      "Source": "git",
      "GitRepoRoot": "E:\\HongWaWorkSpace\\MaxiBIMSH_trunk\\HWRevitToolkit",
      "CommitMessage": "fix(HWRevitToolkit): ..."
    },
    {
      "Source": "svn",
      "SvnRepoUuid": "00000000-0000-0000-0000-000000000000",
      "SvnRepoRootUrl": "https://svn.example.com/repo",
      "CommitMessage": "fix(HWSupportHangerComponent、HWTransMaster4SH): ..."
    }
  ]
}
```

兼容入口：`-CommitMessageLine`、`-CommitMessageLines`、`-CommitMessageText` 仅供人工 Windows 原生命令行或旧自动化脚本使用，不得作为任何模型/自动化调用的默认路径。`-CommitMessageLine` 是数组入口，不能重复写同一个命名参数；人工使用时应传数组或改用单个 `-CommitMessageLines` 分隔字符串。最终提交日志的摘要和正文必须使用中文写法，不要照搬英文模板；但标题开头的 `type(scope):` 中 **`type` 必须保持英文小写**，不得翻译。

Step 3 返回 JSON 中的 `CommitMessage` 与 `CommitMessageSha256` 是默认/总览日志；多提交组时 `Groups[].CommitMessage` 与 `Groups[].CommitMessageSha256` 是各组实际用于 `git commit -F` / `svn commit -F` 的内容。模型最终回复里的“最终版提交日志（可直接复制）”必须逐字复制 Step 3 结果：单组复制 `CommitMessage`，多组按组复制 `Groups[].CommitMessage`，不得再根据记忆或上文重新生成。

若执行器是 Bash/WSL，`powershell` 常会因为命令不存在返回 `errorcode 127`；应显式调用 `powershell.exe`。Step 3 在 Bash/WSL 中执行时，命令行只能包含 ASCII Base64，不能包含任何中文提交日志正文或中文编码命令。Step 3 内部若找不到 `git`/`svn`，结果 JSON 会以 `exitCode=127` 返回并写明缺失命令。

若用户明确要求“只生成日志 / 不提交 / 不推送”，模型跳过 Step 3。

## 输出要求（最终给用户的内容）

1. **默认提交日志**：基于 `ItemsIncludedDefaultLog` / `CommitGroupsDefault` / `ProjectsDefault` / 相关 `Diffs`，并在 `Diffs` 为空或不足时基于补充只读差异/文件内容；`CommitGroupsDefault` 多组时同时列出每个组的日志，单个 SVN 提交组不得按目录拆成多条。
2. **推荐**在默认日志前附 **`### 待提交文件核对`** Markdown 表（`| # | 状态 | 路径 | 来源 | 项目 |`，行与 `ItemsIncludedDefaultLog` 一一对应；Git `状态` 为 `GitIndexStatus`+`GitWorktreeStatus` 两字符拼接；SVN 为 `SvnItem`）。
3. **提交/推送结果**：若执行 Step 3，简要说明 `completed` / `skipped` / `failed` / `regenerate_requested`，失败时列出失败命令摘要；若为 `regenerate_requested`，必须先逐字展示 `ReviewFeedbackRaw`，再按原文重写日志并重新进入 Step 3，不输出最终提交结果。
4. **收尾**：若执行了 Step 3，单组末尾使用 **`### 最终版提交日志（可直接复制）`** 并逐字复制 `CommitMessage`；多组末尾使用 **`### 最终版提交日志（按提交组，可直接复制）`** 并按组逐字复制 `Groups[].CommitMessage`。若未执行 Step 3，复制 Step 2 的日志。每组都用 ```text 完整贴一遍标题+正文，须全文便于从底部复制。
5. 条目换行、scope/type 细则见下文「## type / scope / 条目生成规则」。

## type / scope / 条目生成规则（落地细则）

### type 推断（优先级从高到低）

**硬性格式规则**：

- `type` 必须只使用英文小写字母（必要时可含短横线），放在标题最开头并紧跟 `(`，格式为 `type(scope): 中文摘要`。
- 内置类型只能写：`docs`、`test`、`ci`、`build`、`fix`、`feat`、`refactor`、`perf`、`style`、`chore`。
- 允许扩展类型时也必须是英文小写标识，且必须对团队有稳定含义；不得使用中文扩展类型。
- **禁止示例**：`修复(PackageManager): ...`、`功能(MftScanner): ...`、`性能(PackageManager): ...`。
- **正确示例**：`fix(PackageManager): ...`、`feat(MftScanner): ...`、`perf(PackageManager): ...`。

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

> 允许扩展类型，但务必保证：扩展类型是英文小写标识，对团队有稳定含义，并避免滥用。

### scope 选择

- 默认使用 `Projects[].Scope`（脚本给出的项目名；来自就近 `*.csproj`，否则顶层目录名）。\n
- 若一个项目内改动明显集中在某功能子模块，可输出 `Project-Module` 形式的 scope，但不要过长。
- 若涉及多个项目，将 scope 用中文顿号 `、` 连接，例如 `MftScanner.Core、MftScanner`；scope 只负责说明范围，不表示要拆成多条提交。

### 摘要写法

一句话写清楚：**改了什么** + **解决什么问题/为什么要改**。\n
例：\n
- `fix(git_svn_commitlog_generator): 改用 Base64 UTF-8 传递提交日志，消除 Bash eval 中文问题`
- `feat(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化 IPC 共享内存序列化`

### 条目写法补充

- 使用 `- ` 在内容前，不要使用 `1、2、3` 编号。\n
- 正文 bullet 之间不要插入空行；提交日志默认使用紧凑列表。
- 条目数量、质量和换行门禁以前文「紧凑与长文本换行规则」和「输出前自检」为准。

### 长文本换行示例

只有明显影响阅读时才主动换行，续行使用两个空格缩进，不再重复 `- `，例如：

`- 第一段较长内容，按语义换行`
`  第二段续行内容`

长日志改写示例：

```text
feat(MftScanner.Core、MftScanner、PackageManager): 增加软删除覆盖层与异步搜索调度

- MemoryIndex 新增 _deletedOverlayKeys 软删除覆盖层，MarkDeleted/IsDeleted/HasDeletedOverlay 替代物理数组移除
- Insert 时自动清除覆盖标记，所有搜索函数统一增加 IsSearchVisible 过滤
- IndexService 将路径前置过滤拆分为 postings-first-drive、
  postings-first、drive-filter、path-first、post-filter 五级策略
- EverythingSearchWindow 删除操作先本地移除结果，再异步通知索引
```

示例格式：

```text
feat(MftScanner.Core、MftScanner): 路径前缀前置过滤替换后置过滤，优化 IPC 共享内存序列化

- MftEnumerator 新增 _childDirectoryFrnsByParent，支持将路径前缀定位到目录子树 FRN 集合
- MemoryIndex 新增 ParentSortedArray，通过二分查找高效提取子树候选集
- IndexService 搜索路径前缀时优先使用目录子树前置过滤，解析失败时回退到原有后置过滤
- SharedIndexMemoryProtocol 移除多余 Flush()，用 ThreadStatic 缓冲区优化字符串序列化
- SharedIndexServiceClient 将 WaitForResponseAsync 从阻塞式
  Task.Run+WaitHandle 改为 RegisterWaitForSingleObject+TaskCompletionSource
- MFT 全卷枚举失败时保留现有索引继续服务；Contains 桶预热可配置跳过短查询桶
```
