---
name: annotate_specified_files_public_xml_docs
description: 给用户指定的一个或多个 C# 文件/文件夹批量生成/校对 public XML 文档注释（///）。目录展开由脚本 `resolve-cs-targets.ps1` 完成，避免模型自行递归。只处理 public 与 protected 类型/成员；其余非 public/protected 不添加注释；已有注释则校对并补齐。
---

# annotate_specified_files_public_xml_docs - 指定文件批量 XML 注释

## 触发词建议

- 给以下文件添加 XML 注释
- 为这些 `.cs` 文件生成/补全 XML 注释
- 批量校对 public/protected XML 注释

## 输入（文件路径）

### 会话上下文中的「已附加 / 已引用」文件（推荐，少打字；不绑定具体工具）

在 **Cursor、Claude Code、Codex、其他 IDE 插件或 Agent 宿主**中，用户往往可以通过 **@ 文件、拖入附件、勾选上下文文件、/file 引用、工作区多选** 等方式，把文件送进本轮会话上下文。

技能约定：

- **凡本轮会话上下文中已出现、且能解析出路径的 `.cs` 文件**，一律视为用户指定的批处理目标，**纳入文件列表**（无需用户再手抄一遍路径）。
- **目录 / 多文件**：以宿主实际注入上下文为准——若上下文中包含某目录下多个 `.cs` 的路径或片段，则全部纳入；若宿主只注入了部分文件，则只处理**实际出现在上下文中的 `.cs`**，不足时提示用户补充引用或改用下面「手输 / 清单」方式。
- 上述来源可与「手输路径 / 手输文件夹 / 粘贴 JSON」**合并后去重**（同一文件不会重复处理）。

> 说明：不同产品 UI 不同（`@`、`#file`、附件栏等），本技能**只依赖「上下文里是否带有可识别的文件路径与内容」**，不依赖某一种快捷键语法。

### 其他形式

- 用逗号分隔：`A.cs, B.cs`
- 用换行分隔：多行都算一个路径
- JSON 数组：`["A.cs","B.cs"]`

路径可以是绝对路径或相对仓库根路径。

### 文件夹路径（递归展开）

**支持。** 当用户消息（或会话上下文中能解析出）**目录路径**，且该路径在磁盘上存在、且为目录时：

- **默认**：由脚本 `resolve-cs-targets.ps1` 对该目录 **递归** 扫描，收集其下所有符合条件的 `*.cs`，加入批处理列表。
- **「符合条件」**：普通 `.cs` 源文件；**排除** `.g.cs`、`.generated.cs`、`.designer.cs`、`AssemblyInfo.cs`（除非用户明确要求包含）。
- **递归时跳过目录**（避免扫进构建与依赖）：`bin`、`obj`、`packages`、`.git`、`node_modules`、`.vs`、`.idea` 等。

若用户写明 **「只扫当前目录、不递归」**，则脚本以 `-NoRecursive` 模式处理。

> 与会话引用的关系：若宿主在上下文中附带的是 **文件夹** 且能解析出路径，同样按本节展开；若宿主只注入了文件夹内的部分 `.cs` 文件，则仍以**实际上下文里出现的 `.cs`** 为准，可与「手输文件夹」互补使用。

## 执行流程

### 1) 解析文件列表

1. **收集会话上下文中的目标**：从本轮会话已附加、已引用、或显式展示路径的条目里，解析出 **`.cs` 文件路径** 与 **目录路径**（以宿主注入的上下文为准，不限定具体工具）。
2. **合并**用户消息里手写的 **文件路径、目录路径**；若用户粘贴了「路径解析脚本」输出的 JSON，则合并其中的 `Files` 数组（若有）。
3. **目录展开交给脚本**：禁止模型自行递归扫描目录。统一用下面脚本把目录展开成符合条件的 `.cs` 清单。

在仓库根目录执行（示例；`Targets` 请替换为你收集到的文件/目录路径列表）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .claude/skills/annotate_specified_files_public_xml_docs/scripts/resolve-cs-targets.ps1 `
  -Targets @("路径1","路径2","目录A")
```

脚本输出 JSON：

- `Files`: 绝对路径 `.cs` 数组

4. **合并去重**：将脚本输出的 `Files` 与用户显式给出的 `.cs` 文件（如有）合并，去重后作为本次处理范围。
5. 校验规则：
   - 不存在的路径：默认跳过并在结果里提示缺失

### 2) 逐文件通读并处理 public/protected 成员

对每个入选的 `.cs` 文件：

1. **通读全文**，定位所有需要文档化的 `public`/`protected` 成员（成员所在类型的访问修饰符不限制；即使外层类型是 `internal`，也要为其中的 `public`/`protected` 成员生成或校对 XML 注释）。

   （可选）若外层类型本身也是 `public`/`protected`，则也可为该类型声明补充 XML 注释。
2. 生成或校对 XML 文档注释（`///`）：
   - 若该成员已有 XML 注释：校对并补齐缺失标签，确保与签名一致
   - 若该成员没有 XML 注释：插入一组完整 XML 注释
3. 非 public/protected 成员：
   - 不新增注释
   - 不改动其现有注释（避免越界修改）

### 3) 注释内容规范

- 默认中文简体，且与仓库风格一致
- `param` 名称必须与方法签名参数名一致
- `returns` 仅在非 `void` 方法中出现
- 泛型参数：存在 `T`/`TKey` 等时补齐 `typeparam`
- 至少包含：
  - `/// <summary>...</summary>`
- 可能抛异常时（若方法体中明显有 `throw` 或错误分支）：补齐 `/// <exception cref="...">...</exception>`

### 4) 输出结果（必须）

技能结束时输出简短清单：

- 处理文件列表：`{file1, file2, ...}`
- 每个文件的统计（新增注释/校对注释数量）
- 若跳过了不存在的路径：列出缺失路径

## 示例

**通过会话上下文附带文件（在所用工具里把目标 `.cs` 加入上下文后再发）：**

```text
用 annotate_specified_files_public_xml_docs 给当前会话里附带/引用的这些 .cs 文件补全 public/protected XML 注释。
```

**手输路径示例：**

```text
给这些文件添加 XML 注释：
HWTransMaster4PMEP/code/HWPMEP4RevitJob/PipeSplit/Single/Process/FlangePipeSingleSplitProcess.cs
HWTransMaster4PMEP/code/HWPMEP4RevitJob/PipeGroupDrawing/Process/CreateViewProcess.cs
```

**手输文件夹示例（递归处理该目录下所有符合条件的 `.cs`，与会话引用方式可混用）：**

```text
给下面这个文件夹下所有符合条件的 .cs 补全 public XML 注释（与会话里已 @ 的文件合并去重）：
HWTransMaster4PMEP/code/HWPMEP4RevitJob/PipeSplit/Single/Process
```

