---
name: git_svn_commitlog_generator
description: 找出当前目录下所有 Git + SVN 待提交改动，分析改动内容，并按指定格式生成一条提交日志（多项目写入 scope；正文使用短横线条目；支持编号交互排除；未跟踪的代码、Markdown、前端页面/样式与工程配置会先询问是否加入）。
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

1. **脚本 JSON 输出是唯一数据源**：模型不得自行递归扫描目录来决定“改动范围”。\n
2. **交互排除必须走编号**：用户通过选择 `Id`（编号）来排除改动项；排除后应重新运行脚本以得到过滤后的 JSON。优先使用客户端支持的“选项式提问”；若不支持，则按同样结构用文本列出选项。\n
3. **未纳入版本管理的改动必须先确认**：若检测到 Git `??` / SVN `unversioned`，必须询问用户是否需要纳入本次提交；若需要，应调用脚本执行 `git add` / `svn add` 并重新获取 JSON。候选范围应覆盖代码、Markdown、HTML/CSS、模板、脚本、schema、工程配置等与编码相关的文本文件。\n
4. **一次只生成一条提交日志**：即使涉及多个项目，也不要拆成多条提交；多项目只在 `scope` 中写清楚，并在正文条目中说明各项目关键改动。\n
5. **不要粘贴大段 diff**：提交日志只输出抽象后的变更点，不直接贴 patch 内容。
6. **长日志必须主动硬换行**：最终提交日志不能依赖客户端自动折行；标题与正文都要控制物理行长度，避免一条内容无限长导致难读。

## 执行流程

### Step 1) 获取 Git/SVN 待提交改动（脚本唯一来源）

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1
```

常用参数：

```powershell
# 不采集 diff（更快）
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -IncludeDiff false

# 限制 diff 数量与大小
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -MaxFilesWithDiff 60 -MaxDiffBytesPerFile 20480

# 关闭 SVN 扫描
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -Svn false

# 关闭默认噪音排除（默认会排除 IDE/工具目录、.patch、Temp 等）
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -UseDefaultExcludes false
```

脚本输出 JSON，关键字段：

- `ItemsAll[]`：所有改动项（每条含稳定编号 `Id`）
- `ItemsIncluded[]`：应用排除规则后的改动项
- `ItemsExcluded[]`：被排除的改动项
- `NeedsAdd[]`：尚未纳入版本管理、且**值得询问是否加入**的改动项（代码、Markdown、HTML/CSS、模板、脚本、schema、工程配置等与编码相关的文本文件；Git `??` / SVN `unversioned`）
- `Projects[]`：按项目聚合后的结果（用于生成多项目 scope 与正文条目，不用于拆分多条提交）
- `Diffs`：`Id -> diff`（受 `MaxFilesWithDiff/MaxDiffBytesPerFile` 限制）

### Step 2) 交互：编号选择要排除的内容（默认启用）

模型基于 `ItemsAll` 输出“待提交内容摘要”，并列出编号清单（按项目分组更清晰），例如：

- `#12 [git] src/Foo/Bar.cs (M) 项目=Foo`\n
- `#13 [svn] docs/Readme.md (modified) 项目=docs`

你只需要回复要排除的编号：

- 逗号：`12,13,18`
- 区间：`2-6`
- 混合：`1,4-7,20`
- 不排除：`none`

若客户端支持选项式提问，优先提供这种交互，而不是只丢一段纯文本让用户手输：

1. `全部保留`：不排除任何改动（等价 `none`）
2. `选择部分`：显示推荐/常见编号输入示例（如 `12,13,18` 或 `2-6`）
3. `Type something.`：允许用户自由输入编号、区间或路径说明

若只能文本交互，也按上面的 1/2/3 列出，让用户可直接回复编号或 `none`。

### Step 2.A) 交互：是否要把未纳入版本管理的文件加入本次提交

若 JSON 中 `NeedsAdd[]` 非空，模型必须先展示它们的编号清单并询问：

- 是否需要纳入版本管理并加入本次提交？
- 你用编号回答要加入的项（支持 `1,3,8` / `2-6` / `none`）。

`NeedsAdd[]` 默认应包含以下类型，避免遗漏真正需要提交的编码相关文件：

- C# / C++ / Java / Python / Go / Rust / PHP / Ruby / Swift / Kotlin 等常见源码文件（含 `.c`、`.cc`、`.cpp`、`.cxx`、`.h`、`.hh`、`.hpp`、`.hxx` 等 C/C++ 文件）
- 前端与页面样式文件：如 `.html`、`.htm`、`.css`、`.scss`、`.sass`、`.less`、`.vue`、`.svelte`、`.astro`
- Markdown 与开发文档：如 `.md`、`.markdown`、`.mdx`、`.rst`、`.adoc`；其中 `.md` 必须进入询问列表
- 常见脚本文件：如 `.ps1`、`.psm1`、`.bat`、`.cmd`、`.sh`、`.bash`、`.zsh`
- 常见配置/工程文件：如 `.json`、`.jsonc`、`.yml`、`.yaml`、`.xml`、`.toml`、`.config`、`.csproj`、`.vcxproj`、`.sqlproj`、`.sln`、`.props`、`.targets`、`.gradle`、`.cmake`、`.editorconfig`、`.gitattributes`、`.gitignore`
- 与接口/资源/构建相关的文本文件：如 `.proto`、`.graphql`、`.gql`、`.xsd`、`.resx`、`.manifest`、`.tf`、`.bicep`、`Dockerfile`、`Makefile`、`CMakeLists.txt`

像二进制资源、大型产物、依赖目录、临时输出等未跟踪内容，默认仍不要进入 `NeedsAdd[]`。如果无法判断是否与编码相关，应宁可进入询问列表，让用户决定。

若客户端支持选项式提问，优先提供这种交互：

1. `不加入`：不加入任何未跟踪文件（等价 `none`）
2. `全部加入`：将 `NeedsAdd[]` 中所有候选加入版本管理
3. `选择部分`：展示可加入编号示例（如 `1,3,8` 或 `2-6`）
4. `Type something.`：允许用户自由输入编号、区间或补充说明

若只能文本交互，也按上面的 1/2/3/4 列出，让用户可直接回复编号、区间、`all` 或 `none`。

若你选择加入，运行脚本执行 add（会修改工作区状态）。选择全部加入时可直接传 `-AddIds all`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -AddIds 12,13,18

powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -AddIds all
```

执行完应再次运行 Step 1（或直接使用本次输出）获取最新 JSON，再进入排除与提交日志生成。

### Step 2.1) 应用排除并重新获取“过滤后”的 JSON（推荐做法）

将用户选择的编号作为 `-ExcludeIds` 重新运行脚本（确保仍以脚本 JSON 作为唯一数据源）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/git_svn_commitlog_generator/scripts/get-working-changes.ps1 -ExcludeIds 12,13,18
```

> 说明：脚本也支持 `-ExcludePaths`（以 `/` 结尾表示目录前缀），但默认交互应优先使用 `-ExcludeIds`。

### Step 3) 生成提交日志

根据“过滤后”的 JSON（`ItemsIncluded` / `Projects` / `Diffs`）生成提交日志：

- 无论 `Projects` 有几个有效项目，都只输出一条 `type(scope): summary`。\n
- 若涉及多个项目，将项目/模块名合并到同一个 scope 中，例如 `feat(MftScanner.Core、MftScanner): ...`；不要按项目拆成多条提交。\n
- 正文建议输出 2–8 条，以 `- ` 开头，前面不要留有空格，条目详情顶着最前开始输出，不使用 `1、2、3` 编号。\n
- 同时输出 `本次排除清单`（来自 `ItemsExcluded`，仅列 `#Id + Path` 即可）。

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

- 标题行优先压缩 summary，目标不超过 72 个显示列；标题过长时缩短描述，不要把标题拆成多行。
- 正文每个 `- ` 条目的首行目标不超过 88 个显示列；超过时必须硬换行。
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

## 输出要求（最终给用户的内容）

1. 提交日志（始终只输出一条）\n
2. 正文条目使用 `- ` 短横线，不使用编号\n
3. 长条目必须按“长文本换行规则”硬换行，不能输出无限长单行\n
4. `本次排除清单`：列出 `#Id Path`（来自 `ItemsExcluded`）\n
