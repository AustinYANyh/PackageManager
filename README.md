# PackageManager

一个面向 Windows 桌面的一站式研发效率工作台（WPF / .NET Framework 4.7）。它以“产品包版本管理与更新”为核心，向外聚合了代码工作区（Git/SVN）、PingCode 协作、局域网文件传输、全局快速启动、MFT 文件搜索、命令面板、Revit 工具、开发小工具与工作日报等能力，帮助团队在本地环境下高效完成版本切换、校验、分发与日常研发协作。

所有依赖通过 Costura.Fody 嵌入单一 EXE，双击即可运行，无需安装额外组件。

## 全局热键

| 热键 | 功能 |
|------|------|
| `Ctrl + Q` | 唤起/隐藏「常用启动项」工作台 |
| `Ctrl + E` | 唤起/隐藏「文件搜索」窗口 |
| `Ctrl + Space` | 唤起/隐藏「命令面板」 |

热键在应用启动时全局注册（被其他程序占用时会在日志中告警），可在设置中调整。

## 功能总览

### 包管理（核心）

- **产品分类 / 版本更新**：从 FTP 获取版本目录并排序（兼容带日期前缀的命名，如 `2025.09.30_v1.5.2`），统一的下载、解压、签名/加密校验阶段与进度展示。
- **解锁更新**：基于 Windows Restart Manager 检测文件锁定，自动尝试关闭占用进程并完成替换。
- **路径设置**：为不同产品按版本维度配置本地包路径（`LocalPathSettingsPage`）。
- **产品管理 / 插件管理**：内置与自定义包配置的增删改查；Revit 等插件的集中管理。

### 项目与协作

- **看板统计（PingCode）**：看板/工作项详情、成员统计、AI 评论与 AI 实现/修复入口，支持内网资料（Axure 子页面、图片）下载与 Prompt 提炼。
- **代码工作区**：Git/SVN 代码库管理、VCS 状态与差异查看（自研 Diff 控件，支持行号/字符级高亮）、合并回主干、产品包关联、AI CLI（Claude/Codex）集成与 AI 提交。
- **代码仓库管理**：集中维护源码仓库，扫描 `.sln` / `.csproj` 项目文件。
- **文件传输（LanTransfer）**：局域网设备发现与文件传输，支持加密私聊窗口。
- **MiMo 用量**：MiMo 平台 Token 用量统计、切换账号与用量明细。

### 效率工具（独立唤起窗口）

- **常用启动项（QuickLaunch）**：全局热键唤起的启动器，支持拼音搜索、启动项编辑、打开目录/终端等快捷键操作。
- **文件搜索（FileSearch）**：基于 MftScanner 的毫秒级全盘文件名搜索（详见「MftScanner 子系统」）。
- **命令面板（CommandPalette）**：WebView2 渲染的统一入口，聚合命令、页面导航、产品包操作与文件搜索，支持拼音/简拼模糊匹配与参数收集向导。

### Revit 工具

- Revit 插件管理与文件清理，支持 MFT / Everything / 本地三种索引来源（`RevitFileCleanupWindow`）。

### 开发工具（DevTools）

- DNS 设置与编辑、CSV 加解密（支持拖拽与批量）、SLN 编译顺序更新、Git 代理开关、Jenkins CI 触发、文件解锁、配置预设管理。

### 日志与日报

- **工作日报（DailyLog）**：汇总 Git/SVN 提交与 PingCode 待办，自动生成日报草稿。
- **产品日志 / 软件日志**：按类型/日期/级别筛选、搜索与自动滚动，支持 LogViewPro、VSCode、记事本等多种打开方式。
- **更新日志（Changelog）**：内嵌展示版本变更记录。

### 设置与系统

- **软件设置**：更新服务器地址、热键、调试选项、自动更新等。
- **仪表盘（Dashboard）**：首页概览；**通知中心**：右上角铃铛，保留最多 200 条历史通知。

## 技术架构

### 整体分层

```
App.xaml.cs → MainWindow → 框架初始化（ToolRegistry + NavigationService + ServiceLocator）
```

- **Shell 层**：`ServiceLocator`（静态服务容器，无 DI 框架）、`ToolRegistry`（注册功能页面 `ToolPageDescriptor`）、`NavigationService`（基于 WPF `Frame` 的页面导航）。`ToolRegistration.RegisterAll()` 统一注册所有功能模块。
- **Features 层**：每个功能是自包含子目录（Models / Services / Views），见上方功能总览。

### MftScanner 子系统

高性能 NTFS MFT 文件索引引擎，是文件搜索与 Revit 文件清理的核心依赖：

- `MftScanner.Native`（C++/vcxproj）：Windows API 直接读取 MFT/USN，内存映射文件解析。
- `MftScanner.Core`（C# 类库）：P/Invoke 封装，索引服务与查询接口。
- `MftScanner`（C# 控制台）：可独立运行的搜索引擎，作为 `EmbeddedResource` 嵌入主程序。
- `MftScanner.Service`（C# Windows 服务）：后台索引维护，快照目录在 `ProgramData\PackageManager\MftScannerIndexV2`。

### 嵌入工具模式

子项目构建产物（`.exe` / `.dll`）由 `EnsureEmbeddedToolArtifacts` target 复制到 `Assets\Tools\`，再作为 `EmbeddedResource` 嵌入主 EXE，运行时由 `EmbeddedToolRunnerService` 释放到临时目录执行。

### 数据持久化

所有配置与状态存储在 `%AppData%\PackageManager\` 下的 JSON 文件中（`DataPersistenceService`），保存时自动创建 `.bak` 与带时间戳的历史备份（保留 20 份）。

## 构建

**主项目（Debug / Release）：**

```powershell
msbuild .\PackageManager.csproj /t:Build /p:Configuration=Debug
msbuild .\PackageManager.csproj /t:Build /p:Configuration=Release
```

主项目构建时会通过 `EnsureEmbeddedToolArtifacts` target 自动编译并复制以下子项目产物到 `Assets\Tools\`：`MftScanner.Native`（C++，需 VS 2022 C++ 工具集）、`MftScanner.Core`、`MftScanner`、`MftScanner.Service`、`CommonStartupTool`。

**单独构建 MftScanner Native（Release，性能测试用）：**

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Tools\MftScanner.Native\MftScanner.Native.vcxproj /p:Configuration=Release /p:Platform=x64 /m
```

**发布：** `python scripts\release.py`（通过 FTP/FTPS 上传 EXE 与 `UpdateSummary.txt`）。

> 注意：MftScanner Native 为 C++，需安装 Visual Studio「使用 C++ 的桌面开发」工作负载才能编译。

## 索引性能硬指标

文件搜索索引链路必须满足以下硬性指标：

1. 冷构建必须在 10-15s 内完成。
2. 有索引及快照存在时，恢复索引和各类加速桶必须在 3s 内完成。
3. 任意情况、任意查询的端到端耗时必须小于 50ms。
4. 索引宿主常驻内存必须低于 1GB。
5. 不得破坏 USN 实时更新能力，文件新增、删除、重命名等变更必须实时体现在查询结果中。

性能回归测试必须使用 Release native 与隔离快照目录，不允许修改压测脚本的用例、阈值或 USN 注入语义。推荐命令如下：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe' Tools\MftScanner.Native\MftScanner.Native.vcxproj /p:Configuration=Release /p:Platform=x64 /m

$out='artifacts\mft-sla-<stamp>'
$src='artifacts\mft-sla-20260605-cold-remap-sidecars\snapshot'
$dst=Join-Path $out 'snapshot'
New-Item -ItemType Directory -Force -Path $out | Out-Null
if(Test-Path $dst){ Remove-Item -LiteralPath $dst -Recurse -Force }
Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force

.\scripts\Run-MftScannerBenchmark.ps1 `
  -Configuration Release `
  -Backend SharedHost `
  -SnapshotDirectory ".\$dst" `
  -OutputDirectory ".\$out" `
  -SimulateUsnBacklog `
  -BacklogChangeCounts 200000 `
  -SkipCorrectnessPrecheck
```

`-SkipCorrectnessPrecheck` 仅用于本机缺少固定 fixture（例如 `C:\ makenumberconfig`）时跳过环境预检；脚本内 20W USN backlog 及新增、删除、重命名 synthetic correctness 校验仍必须通过。

涉及 native overlay、快照合并或 USN apply 的改动，必须在同一套 Release 压测定义下复测，不允许通过暂停 live watcher、暂停 snapshot save、改变 backlog 注入语义、缩小查询集合或放宽阈值来过线。压测应使用隔离快照目录；如果要验证持续文件写入场景，只能额外制造外部 USN 噪声，不能让测试绕过正常 watcher/overlay 写入链路。合格标准仍以上述硬指标和脚本内 correctness 结果为准。

## 运行与快速上手

- 直接运行 `PackageManager.exe`（位于 `bin/Release/` 或 `bin/Debug/`），无需额外依赖（集成 Costura.Fody 以嵌入依赖）。
- 首次启动建议：
  - 在「软件设置」中配置更新服务器地址（`UpdateServerUrl`）与相关路径选项。
  - 在「路径设置」中按产品与版本配置本地包路径。
  - 通过全局热键体验效率工具：`Ctrl+Q` 快速启动、`Ctrl+E` 文件搜索、`Ctrl+Space` 命令面板。

## 使用指南

- **包更新**：在「产品分类」主表格中选择目标包与版本，点击更新；可在过程日志中观察阶段与进度。文件被占用时使用「解锁更新」。
- **校验与取消**：触发签名/加密校验后可用「取消」终止外部工具执行，进程会被安全终止，阶段状态与日志会被妥善记录。
- **命令面板**：`Ctrl+Space` 唤起后可直接输入命令名、页面名、产品名或拼音/简拼；以 `/` 开头进入文件搜索；包操作命令（解锁更新、打开目录、定版等）支持缺参时逐级向导选择。
- **筛选与视图**：主界面筛选按钮打开条件编辑器，条件持久化保存并在重启后恢复，支持多列精确/组合筛选。
- **日志与工具**：在「产品日志」「软件日志」中查看并打开日志文件；使用 DNS 设置、CSV 加解密等开发工具完成常用任务。

## 配置与数据

- **设置与状态**：应用设置与主窗口状态通过 `DataPersistenceService` 统一管理（JSON），含更新服务器地址、程序入口是否带 `G`（`ProgramEntryWithG`）等选项。
- **预设与包配置**：配置预设通过 `ConfigPresetStore` 读写（如 `UserPresets.json`），自定义预设以完整 INI 文本存储；当前配置不在预设列表时自动创建「当前配置」卡片并选中。
- **路径与版本**：本地路径按版本维度维护；版本选择逻辑对不同产品有所优化（如 Dazzle 强制选择首个可用版本）。

## 常见问题

- **文件被占用导致更新失败**：使用「解锁更新」；应用会尝试关闭占用进程并对被占用文件进行暂存与后续替换。
- **更新提示但版本未变**：检查 FTP 目录命名是否符合约定；已支持提取 `vX.Y[.Z[.W]]` 并忽略日期前缀。
- **预设匹配不上当前配置**：应用会自动创建「当前配置」卡片并选中；可在预设窗口中编辑或保存为自定义预设。
- **全局热键无响应**：可能被其他程序占用，查看软件日志中的热键注册告警，或在设置中更换热键。

## 发布签名

- 当前仓库已经支持 Authenticode 签名，但必须提供正式的代码签名证书。
- 不要使用自签名或开发证书（例如 `CN=localhost`）；这类证书不能解决 360、SmartScreen 等对未知程序的拦截问题。
- 仓库内可用两种方式签名：
  - `PFX` 文件签名：
    `powershell -ExecutionPolicy Bypass -File .\scripts\SignPackageManager.ps1 -PfxPath "D:\certs\company.pfx" -PfxPassword "******"`
  - 证书仓库签名：
    `powershell -ExecutionPolicy Bypass -File .\scripts\SignPackageManager.ps1 -Thumbprint "‎证书SHA1指纹" -StoreLocation CurrentUser -StoreName My`
- 若要在构建后自动签名，可在编译时传入 MSBuild 属性：
  - `msbuild .\PackageManager.csproj /t:Build /p:Configuration=Release /p:Sign_Enabled=true /p:Sign_CertPath=D:\certs\company.pfx /p:Sign_CertPassword=******`
  - 或：
    `msbuild .\PackageManager.csproj /t:Build /p:Configuration=Release /p:Sign_Enabled=true /p:Sign_Thumbprint=证书SHA1指纹 /p:Sign_StoreLocation=CurrentUser /p:Sign_StoreName=My`
- 脚本会自动查找 Windows SDK 中的 `signtool.exe`，不再要求手动配置系统 `PATH`。

## 自签名 Release 流程

- 这是单独的本地测试流程，不会覆盖正式证书签名入口。
- 现在直接编译 `PackageManager.csproj` 的 `Release` 配置时，也会默认自动执行自签名。
- 如果你只想出未自签名的 `Release`，可在编译时显式关闭：
  - `msbuild .\PackageManager.csproj /t:Build /p:Configuration=Release /p:SelfSign_AutoForRelease=false`
- 构建命令：
  - `powershell -ExecutionPolicy Bypass -File .\scripts\BuildReleaseSelfSigned.ps1`
- 该流程会在 `Release` 构建完成后自动执行：
  - 若未指定证书指纹，则创建或复用 `CN=PackageManager Self-Signed` 的自签名代码签名证书。
  - 默认把证书公钥导入当前用户的 `Root` 和 `TrustedPublisher`，便于本机识别该签名。
  - 然后对 `bin\Release\PackageManager.exe` 执行签名。
- 如需固定使用某个自签名证书，可在 `scripts\selfsign.config.json` 中填写 `Thumbprint`。
- 如需关闭构建脚本中的自动信任，可把 `scripts\selfsign.config.json` 里的 `TrustCurrentUser` 改为 `false`。
- 也可以直接通过 MSBuild 触发自签名构建：
  - `msbuild .\PackageManager.csproj /t:Build /p:Configuration=Release /p:SelfSign_Enabled=true`
- 注意：
  - 自签名只能解决你自己机器或已导入该证书机器上的「未知发布者」提示。
  - 对 360、SmartScreen、下载信誉这类问题帮助非常有限，不能替代正式代码签名证书。

## 版本管理

- 当前版本：`4.2.0.0`（见 `Properties/AssemblyInfo.cs`）。
- 详细版本更新记录请参阅仓库根目录的 `CHANGELOG.md`。
- 变更依据来自 `gitlog.md` 提交历史，按版本号进行归档与提炼。

## 贡献与反馈

- 欢迎通过 Issue 或提交 PR 的方式反馈问题与建议。
- 如需新增内置功能或优化流程，可附上使用场景与期望的交互方式以便评估。

## 许可

- 许可协议未在仓库中声明时，默认仅供内部团队使用；如需开源发布请先补充 LICENSE。
