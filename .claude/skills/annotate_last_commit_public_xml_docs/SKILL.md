---
name: annotate_last_commit_public_xml_docs
description: 给最近一次“由指定作者提交”的 C# 文件全文生成/校对 XML 注释：Git（按 author 过滤最近一次提交）+ SVN（按用户名过滤每个工作副本最近一次 revision）。SVN 默认仅采纳“提交日期=脚本运行当日（本地日历日）”的提交，更早的整批 `.cs` 写入 `SvnExcluded` 并排除；可用 `-SvnTodayOnly:$false` 关闭。不局限于 diff 中有变动的成员；只处理 public 与 protected 成员；已有注释则校对并补充。默认 Git 作者=AustinYanyh，默认 SVN 用户=yanyunhao。
---

# annotate_last_commit_public_xml_docs - 最近一次提交 public/protected XML 注释生成

目标：用最近一次“由指定作者提交”的变更**仅用来确定要处理哪些 `*.cs` 文件**；对每个入选文件，**通读全文**，为该文件中**所有** public 与 protected 成员生成或校对 C# XML 文档注释（`///`）。**不要**只给本次 diff 里出现变动的方法补注释。  
约束：**只处理 public 与 protected**；其余非 public/protected（private/internal、显式接口实现等）不新增注释；已有注释只做校对/补充，不改语义代码。

## 适用范围

- **Git**：只处理“最近一次由指定 Git 作者（author）提交”中**变更过的** `*.cs` 文件
- **SVN**：对每个嵌套工作副本分别取“最近一次由指定 SVN 用户名提交”的 revision；**默认**仅当该提交的日历日等于**运行脚本当日（本地）**时才纳入；否则该工作副本下本会变更的 `.cs` 全部记入 `SvnExcluded`，**不参与**注释处理范围
- 若最近一次 Git + SVN 都没有 `*.cs` 变更：输出“本次最近提交没有 C# 文件变更，无需生成注释”
- 若有 `*.cs` 变更：对上述**每一个**变更过的 `.cs` 文件做**整文件** public/protected 注释处理（见下节）
- 若当前目录本身不是SVN工作副本，同时检查仓库里是否存在嵌套的SVN工作副本，避免漏掉技能要求里的SVN部分。

## 可选参数（用于锁定“你自己的那批文件”）

- 不提供参数：Git 作者= `AustinYanyh`，SVN 用户名= `yanyunhao`
- 提供 `username=你的名字`：同时覆盖 Git/SVN 的作者匹配字符串
- 提供 `gitAuthor=你的 Git 名`：仅覆盖 Git 作者匹配字符串
- 提供 `svnAuthor=你的 SVN 名`：仅覆盖 SVN 作者匹配字符串
- **SVN 日期过滤（脚本参数）**：
  - **默认**：`-SvnTodayOnly` 为 `true`（脚本参数写为布尔默认 `true`）：只采纳提交日期为**本地当日**的作者提交
  - **关闭当日限制**（仍会取各工作副本下该作者的最近一次提交，可能很早）：`get-last-change-files.ps1` 调用时加 `-SvnTodayOnly:$false`，或等价开关 `-SvnAllDates`


## 执行流程

### 1) 获取最近一次变更的文件列表（只要 C#）

#### 必须先用脚本获取文件列表（单一数据源）

为避免模型自行去 `git/svn` 再次计算处理范围，**本技能用于确定“要处理哪些文件”的唯一来源**是下面脚本的 JSON 输出。

在仓库根目录执行：

```powershell
# 不提供参数则使用脚本内置默认：GitAuthor=AustinYanyh, SvnAuthor=yanyunhao
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_last_commit_public_xml_docs/scripts/get-last-change-files.ps1

# 如需同时覆盖：Git/SVN 作者匹配
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_last_commit_public_xml_docs/scripts/get-last-change-files.ps1 -Username "{username}"

# 如需分别覆盖：Git 作者 & SVN 用户名
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_last_commit_public_xml_docs/scripts/get-last-change-files.ps1 -GitAuthor "{gitAuthor}" -SvnAuthor "{svnAuthor}"

# 关闭 SVN「仅当日」过滤（各工作副本仍会取该作者的最近一次提交，可能是很早的 revision）
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_last_commit_public_xml_docs/scripts/get-last-change-files.ps1 -SvnTodayOnly:$false

# 同上（开关写法）
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_last_commit_public_xml_docs/scripts/get-last-change-files.ps1 -SvnAllDates
```

脚本输出的 JSON 结构：

- `Git`: `string[]`（Git 侧命中的 `.cs` 文件绝对路径）
- `Svn`: `string[]`（SVN 侧命中的 `.cs` 文件绝对路径，**已应用当日过滤后**）
- `SvnMeta`: 每个纳入的 SVN 工作副本一条（`wcRoot`、`rev`、`csChangedCount`、`commitDay`、`svnTodayOnly`）
- `SvnExcluded`: **被当日过滤排除**的条目列表；每条含 `reason`、`wcRoot`、`rev`、`commitDay`、`scriptToday`、`excludedCsFiles`（本会进入 `Svn` 但被排除的 `.cs` 绝对路径）
- `SvnTodayOnly`: `true` / `false`（与本次调用一致）

本技能后续步骤中：
- **处理范围 = `Git` 与 `Svn` 的并集（去重）**（**不得**把 `SvnExcluded.excludedCsFiles` 算进处理范围）
- **禁止**仅凭 `git show` / `svn diff` 自行决定“处理哪些文件”（可以把 diff 当背景理解，但不得改变处理范围）。

为便于你核对“是否按多个目录取到多个 revision”，技能执行时请把脚本返回的 `SvnMeta`、`SvnExcluded`、`SvnTodayOnly` **原样贴出**。

补充：若 `svn log` 行无法解析出 `yyyy-MM-dd` 日期，脚本**不会**按日期排除该条（避免因日志格式差异误杀全部 SVN 结果）。

#### Git：按 author 取“最近一次由指定作者提交”（由脚本完成）

```bash
git log -1 --author="{GitAuthor}" --pretty=format:%H
git show {sha} --name-only --diff-filter=ACMRT
```

筛选出以 `.cs` 结尾的文件（可排除常见生成文件）：

- 建议跳过：`*.g.cs`、`*.generated.cs`、`*.designer.cs`、`AssemblyInfo.cs`

#### SVN：按用户名取“最近一次由指定用户提交”（由脚本完成）

扫描当前目录下的所有嵌套 `svn` 工作副本（即每个包含 `.svn` 的目录），对**每个工作副本根目录**分别：
1. 取“最近一次由指定用户提交”的 revision（取最近到最旧，找到第一个 author 匹配的 revision）
2. **（默认）当日过滤**：若该 revision 的提交日期（从 `svn log` 解析出的 `yyyy-MM-dd`）**不等于**运行脚本当日的本地日历日，则将该工作副本本会得到的 `.cs` 全部写入 `SvnExcluded`，**跳过**该工作副本
3. 对通过过滤的 revision 执行 `svn diff -c {rev} --summarize`
4. 汇总所有工作副本得到的 `.cs` 变更文件（最后去重）

补充：脚本在 `svn log` 时会显式从 `HEAD` 开始（`-r HEAD:0`），避免你本地工作副本 revision 落后导致漏掉更晚的提交。

```bash
svn log -l 50
```

拿到匹配到作者的 revision（记为 `{rev}`）后，获取本次 revision 变更文件列表：

```bash
svn diff -c {rev} --summarize
```

同样筛选出以 `.cs` 结尾的文件，并跳过常见生成文件（同上）。

### 2) 对入选的每个 `*.cs` 文件：全文处理 public/protected，而非“只处理有变动的成员”

对每个在最近提交中出现的 `*.cs` 文件：

1. **打开并通读该文件的完整内容**（不要仅根据 diff 范围工作）。
2. 在**整份源码**中枚举需要文档化的 `public`/`protected` 成员（成员所在类型的访问修饰符不限制；即使外层类型是 `internal`，也要为其中的 `public`/`protected` 成员生成或校对 XML 注释）。

（可选）若外层类型本身也是 `public`/`protected`，则也为该类型声明补充 XML 注释。

3. 对上述**每一个** `public`/`protected` 成员：若无 `///` 则补充；若已有则校对并补全 `summary` / `param` / `returns` / `typeparam` / `exception` 等与签名一致。  
4. **非 public/protected** 成员：不新增注释（与 diff 是否改动无关）。  
5. （可选）若需理解“本次提交改了什么”，可查看 diff 作为背景，但**注释补全的范围仍是整文件 public/protected API（按成员访问修饰符）**，不以 diff 为边界。

```bash
# 可选：仅作上下文，不作为处理边界
git show -1 -U0 -- "{文件路径}"
# 或
svn diff -c {rev} -- "{文件路径}"
```

### 3) 生成/校对 XML 注释的规则（必须遵守）

#### A. 注释语言与风格

- 默认使用**中文简体**，与仓库既有风格一致
- 只写高信息密度：一句话说明用途/副作用/约束；避免“显而易见”的叙述性废话

#### B. 必备标签

- **类型/成员**：至少提供 `/// <summary>...</summary>`
- **方法**：
  - 有参数：补齐 `/// <param name="x">...</param>`（参数名必须与签名一致）
  - 有返回值且非 `void`：补齐 `/// <returns>...</returns>`
  - 可能抛异常：补齐必要的 `/// <exception cref="...">...</exception>`
- **泛型**：有 `T` 等类型参数时补齐 `/// <typeparam name="T">...</typeparam>`

#### C. 已有注释则“补充校对”

当成员已存在 XML 注释时：

- 校对错别字、标点、措辞
- **校对一致性**：`param/typeparam/returns/exception` 与当前签名一致
- 删除或改写与当前行为明显矛盾的描述（但不改代码语义）

#### D. 绝不处理的情况

- 非 public/protected 成员：不新增注释（已有注释可保持不动，除非它属于 public/protected 成员的一部分并明显不一致）
- 自动生成/第三方代码：不改
- 仅 `.sln/.csproj/.config` 等非 C# 源码：不做注释生成

### 4) 输出要求（对话/PR 描述/提交说明用）

当处理完成后，输出一个简短清单：

- Git 变更文件：`{file1, file2, ...}`（若无则写“无”）
- SVN 变更文件：`{file1, file2, ...}`（若无则写“无”）
- SVN 选择的 revision 明细：`SvnMeta`（每个 `wcRoot` -> `rev` -> `csChangedCount` -> `commitDay`）
- SVN 被排除清单：`SvnExcluded`（含 `reason` 与 `excludedCsFiles`）；并贴出 `SvnTodayOnly` 当前值
- 按文件列出：本文件内处理的 public/protected 成员数量（类型补注仅在类型本身为 public/protected 时计入；新增注释/校对注释分别多少；以**整文件**为准，非仅 diff）
- 若两边都无 C# 变更：明确说明“无需处理”

## 常见陷阱

- `param` 名称不匹配（重命名参数后最常见）
- `returns` 在 `void` 方法中错误出现
- 属性/字段注释写成“方法返回值”语气
- 泛型类型参数缺少 `typeparam`
