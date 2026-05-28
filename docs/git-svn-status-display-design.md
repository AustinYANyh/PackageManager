# Git/SVN 状态实时显示 — 详细设计方案

## 一、背景与需求

### 核心需求
代码工作区当前仅作为"启动器"，无法直观看到仓库的版本控制状态。需要在现有界面中集成 Git/SVN 状态实时显示功能。

### 关键约束
1. **列空间有限**: 当前 DataGrid 操作按钮列已占 880px，加上仓库名(250px)和路径(2.1*)，水平空间紧张
2. **混合仓库场景**: 根目录可能是 Git 仓库，子目录中可能包含多个 SVN 仓库（或反之）
3. **性能要求**: 状态检测不能阻塞 UI，需异步执行
4. **兼容性**: 不能破坏现有 DataGrid 自动模板选择机制（CDataGrid + DataGridColumn 属性）

---

## 二、UI 设计方案

### 推荐方案：紧凑状态列 + 行展开详情

综合考虑空间限制和信息密度，采用**双层信息架构**：
- **第一层（DataGrid 行内）**: 紧凑的状态摘要，一眼可见关键信息
- **第二层（展开面板）**: 点击行后展开详细状态，包括子仓库信息

### 2.1 DataGrid 列重新规划

#### 现有列调整

| 列 | 原宽度 | 新宽度 | 说明 |
|----|--------|--------|------|
| 仓库名 | 250px | 200px | 略缩，仓库名一般不会太长 |
| 状态指示 | 无 | 42px | **新增**，圆形图标 |
| 分支/版本 | 无 | 140px | **新增**，显示当前分支或SVN版本 |
| 变更摘要 | 无 | 90px | **新增**，如 "+3 ~2 -1" 或 "3项变更" |
| 路径 | 2.1* | 1.5* | 适当缩窄 |
| 操作 | 880px | 720px | 缩减按钮间距和宽度 |

#### 操作按钮瘦身方案

当前8个按钮总宽 880px，优化后:

| 按钮 | 原宽度 | 新宽度 | 说明 |
|------|--------|--------|------|
| Claude提交 | 110px | 90px | 缩短 |
| Codex提交 | 100px | 86px | 缩短 |
| VS | 54px | 42px | 缩短 |
| Rider | 60px | 50px | 缩短 |
| Cursor | 64px | 54px | 缩短 |
| Claude | 68px | 58px | 缩短 |
| Codex | 64px | 54px | 缩短 |
| 文件夹 | 68px | 52px | 缩短 |
| **合计** | **880px** | **720px** | **释放 160px** |

加上仓库名释放的 50px，共释放 210px，足够放置新增的三列（42+140+90=272px），路径列改为 1.5* 自动伸缩吸收差异。

### 2.2 状态指示列设计

使用**圆形图标**（直径 16px）表示仓库整体健康状态：

```
● 绿色 (#4CAF50) — 干净，工作区无变更
● 黄色 (#FF9800) — 有未提交的变更
● 红色 (#F44336) — 有冲突或错误
● 灰色 (#9E9E9E) — 未检测到版本控制 / 检测中
```

图标左侧叠加小型 VCS 类型标识：
```
[G●]  — Git 仓库（绿色=干净）
[S●]  — SVN 仓库（黄色=有变更）
[G+S●] — 混合仓库（Git根目录 + SVN子目录）
```

**混合仓库特殊处理**: 当检测到根目录是 Git 且子目录包含 SVN 时，状态图标显示为上下分割的双色圆：
- 上半圆 = Git 状态颜色
- 下半圆 = SVN 子仓库中最严重的状态颜色

Tooltip 悬浮显示完整摘要：
```
Git: main 分支, 3个未提交文件
SVN子仓库 (2个):
  ├ libs/core — r1234, 干净
  └ libs/plugin — r1230, 2个修改
```

### 2.3 分支/版本列设计

显示当前分支名或 SVN 版本号：

```
Git仓库:     main           (分支名)
             feature/login  (分支名, 过长时截断+tooltip)
SVN仓库:     r1234          (版本号)
混合仓库:    main | 2 SVN   (Git分支 + SVN子仓库数)
```

**混合仓库显示规则**:
- 主显示: Git 分支名
- 后缀: `| N SVN`（N为SVN子仓库数量）
- Tooltip: 展开列出每个SVN子仓库的路径和版本号

### 2.4 变更摘要列设计

紧凑格式显示变更统计：

```
Git:    +3 ~2 -1        (3个新增, 2个修改, 1个删除)
        ✓ 干净           (无变更)
SVN:    3项变更           (统计变更文件数)
混合:   G:+3 S:2         (Git有3个新增, SVN子仓库共2个变更)
```

颜色编码：
- 绿色文字: 干净 / 仅新增
- 黄色文字: 有修改
- 红色文字: 有删除或冲突

### 2.5 行展开详情面板

双击仓库行，在行下方展开一个详情面板（高度约 120-180px），显示完整信息：

```
┌─────────────────────────────────────────────────────────────────────┐
│ 📂 MyProject                                                       │
│                                                                     │
│ ┌─ Git (根目录) ─────────────────────────────────────────────────┐  │
│ │ 分支: main                                                     │  │
│ │ 状态: 3个文件变更 (2个已暂存, 1个未暂存)                        │  │
│ │ 远程: ↑2 ↓1 (领先2个提交, 落后1个提交)                         │  │
│ │ 变更文件: src/App.cs(M) src/Config.cs(M) README.md(A)          │  │
│ │ [Pull] [Push] [Stash] [查看详情]                               │  │
│ └────────────────────────────────────────────────────────────────┘  │
│                                                                     │
│ ┌─ SVN 子仓库 (2个) ────────────────────────────────────────────┐  │
│ │ libs/core    r1234  ✓ 干净                                     │  │
│ │ libs/plugin  r1230  2个修改: Plugin.cs(M) Config.xml(M)        │  │
│ │              [Update All] [Commit] [查看详情]                   │  │
│ └────────────────────────────────────────────────────────────────┘  │
│                                                                     │
│ 上次刷新: 10秒前                              [手动刷新]           │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 三、数据模型设计

### 3.1 新增模型类

#### SubRepository.cs — 子仓库信息

```csharp
namespace PackageManager.Features.CodeWorkspace.Models
{
    public class SubRepository : INotifyPropertyChanged
    {
        private string _relativePath;
        private VcsType _vcsType;
        private string _branch;
        private int _revision;
        private VcsStatus _status;
        private int _changedFileCount;
        private string _statusSummary;

        public string RelativePath
        {
            get => _relativePath;
            set => SetProperty(ref _relativePath, value);
        }

        public VcsType VcsType
        {
            get => _vcsType;
            set => SetProperty(ref _vcsType, value);
        }

        public string Branch
        {
            get => _branch;
            set => SetProperty(ref _branch, value);
        }

        public int Revision
        {
            get => _revision;
            set => SetProperty(ref _revision, value);
        }

        public VcsStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public int ChangedFileCount
        {
            get => _changedFileCount;
            set => SetProperty(ref _changedFileCount, value);
        }

        public string StatusSummary
        {
            get => _statusSummary;
            set => SetProperty(ref _statusSummary, value);
        }

        // INotifyPropertyChanged 实现省略...
    }
}
```

#### VcsType 枚举

```csharp
public enum VcsType
{
    None,
    Git,
    Svn,
    Mixed  // Git根目录 + SVN子目录
}
```

#### VcsStatus 枚举

```csharp
public enum VcsStatus
{
    Unknown,     // 未检测 / 检测中
    Clean,       // 干净
    Modified,    // 有未提交变更
    Conflict,    // 有冲突
    Error        // 检测出错
}
```

### 3.2 CodeRepository 模型扩展

在 `CodeRepository.cs` 中新增属性：

```csharp
// === VCS 状态属性 ===

private VcsType _vcsType = VcsType.None;
private VcsStatus _vcsStatus = VcsStatus.Unknown;
private string _gitBranch;
private int _gitAheadCount;
private int _gitBehindCount;
private int _addedCount;
private int _modifiedCount;
private int _deletedCount;
private int _stagedCount;
private int _svnRevision;
private ObservableCollection<SubRepository> _subRepositories = new ObservableCollection<SubRepository>();
private DateTime _lastStatusRefresh;
private bool _isRefreshing;

[DataGridColumn(2, DisplayName = "", Width = "42", IsReadOnly = true)]
public VcsType VcsType
{
    get => _vcsType;
    set => SetProperty(ref _vcsType, value);
}

// 用于 DataGrid 状态列的绑定（通过自定义 CellTemplate）
[DataGridColumn(3, DisplayName = "分支", Width = "140", IsReadOnly = true)]
public string BranchDisplay
{
    get
    {
        switch (VcsType)
        {
            case VcsType.Git:
                return _gitBranch ?? "—";
            case VcsType.Svn:
                return $"r{_svnRevision}";
            case VcsType.Mixed:
                var svnCount = SubRepositories?.Count(s => s.VcsType == VcsType.Svn) ?? 0;
                return $"{_gitBranch ?? "—"} | {svnCount} SVN";
            default:
                return "—";
        }
    }
    set { }  // DataGrid 属性需要 setter
}

[DataGridColumn(4, DisplayName = "变更", Width = "90", IsReadOnly = true)]
public string ChangesSummary
{
    get
    {
        if (VcsStatus == VcsStatus.Unknown) return "检测中...";
        if (VcsStatus == VcsStatus.Error) return "检测失败";
        if (VcsStatus == VcsStatus.Clean && !HasSubRepoChanges) return "✓ 干净";

        if (VcsType == VcsType.Mixed)
        {
            var gitChanges = _addedCount + _modifiedCount + _deletedCount;
            var svnChanges = SubRepositories?.Sum(s => s.ChangedFileCount) ?? 0;
            var parts = new List<string>();
            if (gitChanges > 0) parts.Add($"G:{gitChanges}");
            if (svnChanges > 0) parts.Add($"S:{svnChanges}");
            return string.Join(" ", parts);
        }

        if (_addedCount + _modifiedCount + _deletedCount == 0) return "✓ 干净";

        var result = new List<string>();
        if (_addedCount > 0) result.Add($"+{_addedCount}");
        if (_modifiedCount > 0) result.Add($"~{_modifiedCount}");
        if (_deletedCount > 0) result.Add($"-{_deletedCount}");
        return string.Join(" ", result);
    }
    set { }
}

public VcsStatus VcsStatus
{
    get => _vcsStatus;
    set
    {
        if (SetProperty(ref _vcsStatus, value))
        {
            OnPropertyChanged(nameof(ChangesSummary));
            OnPropertyChanged(nameof(BranchDisplay));
        }
    }
}

public string GitBranch
{
    get => _gitBranch;
    set
    {
        if (SetProperty(ref _gitBranch, value))
            OnPropertyChanged(nameof(BranchDisplay));
    }
}

public int GitAheadCount
{
    get => _gitAheadCount;
    set => SetProperty(ref _gitAheadCount, value);
}

public int GitBehindCount
{
    get => _gitBehindCount;
    set => SetProperty(ref _gitBehindCount, value);
}

public int AddedCount
{
    get => _addedCount;
    set
    {
        if (SetProperty(ref _addedCount, value))
            OnPropertyChanged(nameof(ChangesSummary));
    }
}

public int ModifiedCount
{
    get => _modifiedCount;
    set
    {
        if (SetProperty(ref _modifiedCount, value))
            OnPropertyChanged(nameof(ChangesSummary));
    }
}

public int DeletedCount
{
    get => _deletedCount;
    set
    {
        if (SetProperty(ref _deletedCount, value))
            OnPropertyChanged(nameof(ChangesSummary));
    }
}

public int StagedCount
{
    get => _stagedCount;
    set => SetProperty(ref _stagedCount, value);
}

public int SvnRevision
{
    get => _svnRevision;
    set
    {
        if (SetProperty(ref _svnRevision, value))
            OnPropertyChanged(nameof(BranchDisplay));
    }
}

public ObservableCollection<SubRepository> SubRepositories
{
    get => _subRepositories;
    set => SetProperty(ref _subRepositories, value);
}

public bool IsRefreshing
{
    get => _isRefreshing;
    set => SetProperty(ref _isRefreshing, value);
}

public DateTime LastStatusRefresh
{
    get => _lastStatusRefresh;
    set => SetProperty(ref _lastStatusRefresh, value);
}

private bool HasSubRepoChanges =>
    SubRepositories?.Any(s => s.ChangedFileCount > 0) == true;
```

### 3.3 DataGrid 列序号调整

更新所有 `DataGridColumn` 属性的序号：

| 序号 | 列 | 属性 | 宽度 |
|------|-----|------|------|
| 1 | 仓库 | Name | 200 |
| 2 | 状态 | VcsType（自定义模板）| 42 |
| 3 | 分支 | BranchDisplay | 140 |
| 4 | 变更 | ChangesSummary | 90 |
| 5 | 路径 | Path | 1.5* |
| 6 | 备注 | Note | 180 (隐藏) |
| 7 | 项目文件 | ProjectFileCount | 80 (隐藏) |
| 8 | 最后使用 | LastUsedText | 140 (隐藏) |
| 9 | 操作 | Actions | 720 |

---

## 四、VCS 状态检测服务

### 4.1 新增 VcsStatusService.cs

```
Features\CodeWorkspace\Services\VcsStatusService.cs
```

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class VcsStatusService
    {
        private CancellationTokenSource _refreshCts;
        private readonly ConcurrentDictionary<string, DateTime> _lastRefreshTimes = new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(10);

        /// <summary>
        /// 刷新单个仓库的 VCS 状态。
        /// </summary>
        public async Task RefreshRepositoryStatusAsync(CodeRepository repo, CancellationToken cancellationToken = default)
        {
            if (repo == null || string.IsNullOrEmpty(repo.Path) || !Directory.Exists(repo.Path))
                return;

            // 节流：同一仓库 10 秒内不重复刷新
            if (_lastRefreshTimes.TryGetValue(repo.Path, out var lastTime)
                && DateTime.Now - lastTime < MinRefreshInterval)
                return;

            repo.IsRefreshing = true;

            try
            {
                // 1. 检测根目录 VCS 类型
                var hasGit = Directory.Exists(Path.Combine(repo.Path, ".git"));
                var hasSvn = Directory.Exists(Path.Combine(repo.Path, ".svn"));

                // 2. 扫描子目录中的 SVN 仓库（仅一级子目录）
                var svnSubDirs = await Task.Run(() => FindSvnSubDirectories(repo.Path), cancellationToken);

                // 3. 确定仓库类型
                if (hasGit && svnSubDirs.Count > 0)
                    repo.VcsType = VcsType.Mixed;
                else if (hasGit)
                    repo.VcsType = VcsType.Git;
                else if (hasSvn)
                    repo.VcsType = VcsType.Svn;
                else
                    repo.VcsType = VcsType.None;

                // 4. 获取 Git 状态
                if (hasGit)
                {
                    await RefreshGitStatusAsync(repo, cancellationToken);
                }

                // 5. 获取 SVN 根目录状态（如果根目录本身是SVN）
                if (hasSvn && !hasGit)
                {
                    await RefreshSvnStatusAsync(repo, repo.Path, cancellationToken);
                }

                // 6. 获取 SVN 子仓库状态
                if (svnSubDirs.Count > 0)
                {
                    await RefreshSubRepositoriesAsync(repo, svnSubDirs, cancellationToken);
                }

                // 7. 计算综合状态
                repo.VcsStatus = CalculateOverallStatus(repo);
                repo.LastStatusRefresh = DateTime.Now;
                _lastRefreshTimes[repo.Path] = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                // 取消时不更新状态
            }
            catch (Exception)
            {
                repo.VcsStatus = VcsStatus.Error;
            }
            finally
            {
                repo.IsRefreshing = false;
            }
        }

        /// <summary>
        /// 批量刷新所有仓库状态（并发控制）。
        /// </summary>
        public async Task RefreshAllAsync(IEnumerable<CodeRepository> repositories, CancellationToken cancellationToken = default)
        {
            // 取消上一次的刷新
            _refreshCts?.Cancel();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _refreshCts.Token;

            // 最多 4 个并发
            var semaphore = new SemaphoreSlim(4);
            var tasks = repositories.Select(async repo =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    await RefreshRepositoryStatusAsync(repo, token);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 停止所有正在进行的刷新。
        /// </summary>
        public void CancelRefresh()
        {
            _refreshCts?.Cancel();
        }

        // ========== Git 检测 ==========

        private async Task RefreshGitStatusAsync(CodeRepository repo, CancellationToken ct)
        {
            // 获取当前分支
            var branchResult = await RunCommandAsync("git", "branch --show-current", repo.Path, ct);
            if (branchResult.ExitCode == 0)
            {
                repo.GitBranch = branchResult.Output.Trim();
                if (string.IsNullOrEmpty(repo.GitBranch))
                {
                    // detached HEAD
                    var headResult = await RunCommandAsync("git", "rev-parse --short HEAD", repo.Path, ct);
                    repo.GitBranch = headResult.ExitCode == 0 ? $"({headResult.Output.Trim()})" : "(detached)";
                }
            }

            // 获取 ahead/behind
            var abResult = await RunCommandAsync("git", "rev-list --left-right --count HEAD...@{upstream}", repo.Path, ct);
            if (abResult.ExitCode == 0)
            {
                var parts = abResult.Output.Trim().Split('\t');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out var ahead);
                    int.TryParse(parts[1], out var behind);
                    repo.GitAheadCount = ahead;
                    repo.GitBehindCount = behind;
                }
            }

            // 获取工作区状态
            var statusResult = await RunCommandAsync("git", "status --porcelain", repo.Path, ct);
            if (statusResult.ExitCode == 0)
            {
                var lines = statusResult.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int added = 0, modified = 0, deleted = 0, staged = 0;

                foreach (var line in lines)
                {
                    if (line.Length < 2) continue;
                    var indexStatus = line[0];
                    var workStatus = line[1];

                    // 暂存区状态
                    if (indexStatus != ' ' && indexStatus != '?') staged++;

                    // 工作区状态统计
                    if (indexStatus == '?' || workStatus == '?')
                        added++;
                    else if (indexStatus == 'D' || workStatus == 'D')
                        deleted++;
                    else if (indexStatus == 'A')
                        added++;
                    else
                        modified++;
                }

                repo.AddedCount = added;
                repo.ModifiedCount = modified;
                repo.DeletedCount = deleted;
                repo.StagedCount = staged;
            }
        }

        // ========== SVN 检测 ==========

        private async Task RefreshSvnStatusAsync(CodeRepository repo, string svnPath, CancellationToken ct)
        {
            // 获取版本号
            var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnPath, ct);
            if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
            {
                repo.SvnRevision = rev;
            }

            // 获取变更状态
            var statusResult = await RunCommandAsync("svn", "status", svnPath, ct);
            if (statusResult.ExitCode == 0)
            {
                var lines = statusResult.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int added = 0, modified = 0, deleted = 0;

                foreach (var line in lines)
                {
                    if (line.Length < 1) continue;
                    switch (line[0])
                    {
                        case '?':
                        case 'A': added++; break;
                        case 'M': modified++; break;
                        case 'D': deleted++; break;
                        case 'C': repo.VcsStatus = VcsStatus.Conflict; break;
                    }
                }

                repo.AddedCount = added;
                repo.ModifiedCount = modified;
                repo.DeletedCount = deleted;
            }
        }

        // ========== 子仓库检测 ==========

        private List<string> FindSvnSubDirectories(string rootPath)
        {
            var result = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(rootPath))
                {
                    var dirName = System.IO.Path.GetFileName(dir);
                    // 跳过隐藏目录和常见非仓库目录
                    if (dirName.StartsWith(".") || dirName == "node_modules" ||
                        dirName == "bin" || dirName == "obj" || dirName == "packages")
                        continue;

                    if (Directory.Exists(Path.Combine(dir, ".svn")))
                    {
                        result.Add(dir);
                    }

                    // 递归扫描二级目录（最多两层深度）
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(dir))
                        {
                            var subDirName = System.IO.Path.GetFileName(subDir);
                            if (subDirName.StartsWith(".")) continue;
                            if (Directory.Exists(Path.Combine(subDir, ".svn")))
                            {
                                result.Add(subDir);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (UnauthorizedAccessException) { }

            return result;
        }

        private async Task RefreshSubRepositoriesAsync(CodeRepository repo, List<string> svnDirs, CancellationToken ct)
        {
            repo.SubRepositories.Clear();

            foreach (var svnDir in svnDirs)
            {
                ct.ThrowIfCancellationRequested();

                var subRepo = new SubRepository
                {
                    RelativePath = GetRelativePath(repo.Path, svnDir),
                    VcsType = VcsType.Svn
                };

                // 获取版本号
                var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnDir, ct);
                if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
                {
                    subRepo.Revision = rev;
                }

                // 获取状态
                var statusResult = await RunCommandAsync("svn", "status", svnDir, ct);
                if (statusResult.ExitCode == 0)
                {
                    var lines = statusResult.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    subRepo.ChangedFileCount = lines.Length;
                    subRepo.Status = lines.Length == 0 ? VcsStatus.Clean :
                                     lines.Any(l => l.Length > 0 && l[0] == 'C') ? VcsStatus.Conflict :
                                     VcsStatus.Modified;
                    subRepo.StatusSummary = lines.Length == 0 ? "干净" : $"{lines.Length}项变更";
                }
                else
                {
                    subRepo.Status = VcsStatus.Error;
                    subRepo.StatusSummary = "检测失败";
                }

                repo.SubRepositories.Add(subRepo);
            }
        }

        // ========== 辅助方法 ==========

        private VcsStatus CalculateOverallStatus(CodeRepository repo)
        {
            // 检查冲突
            if (repo.SubRepositories?.Any(s => s.Status == VcsStatus.Conflict) == true)
                return VcsStatus.Conflict;

            // 检查变更
            var hasRootChanges = repo.AddedCount + repo.ModifiedCount + repo.DeletedCount > 0;
            var hasSubChanges = repo.SubRepositories?.Any(s => s.ChangedFileCount > 0) == true;

            if (hasRootChanges || hasSubChanges)
                return VcsStatus.Modified;

            return VcsStatus.Clean;
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (!basePath.EndsWith("\\"))
                basePath += "\\";
            var baseUri = new Uri(basePath);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', '\\'));
        }

        private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = psi })
                using (ct.Register(() => { try { process.Kill(); } catch { } }))
                {
                    process.Start();
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();

                    // 超时 5 秒
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        return new CommandResult { ExitCode = -1, Output = "", Error = "Timeout" };
                    }

                    return new CommandResult
                    {
                        ExitCode = process.ExitCode,
                        Output = output,
                        Error = error
                    };
                }
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, Output = "", Error = ex.Message };
            }
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }
    }
}
```

---

## 五、页面集成

### 5.1 CodeWorkspacePage.xaml 修改

在顶部按钮栏增加"刷新状态"按钮：

```xml
<StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
    <button:CButton Content="刷新状态" Width="80" Height="30" Margin="8,0,0,0" Click="RefreshStatusButton_Click"/>
    <button:CButton Content="管理仓库" Width="86" Height="30" Margin="8,0,0,0" Click="ManageRepositoriesButton_Click"/>
    <button:CButton Content="刷新项目文件" Width="104" Height="30" Margin="8,0,0,0" Click="RefreshProjectFilesButton_Click"/>
    <button:CButton Content="刷新列表" Width="80" Height="30" Margin="8,0,0,0" Click="ReloadButton_Click"/>
    <button:CButton Content="返回" Width="70" Height="30" Margin="8,0,0,0" Click="BackButton_Click"/>
</StackPanel>
```

### 5.2 CodeWorkspacePage.xaml.cs 修改

```csharp
private VcsStatusService _vcsStatusService = new VcsStatusService();
private CancellationTokenSource _autoRefreshCts;

// 页面加载后启动状态刷新
private async void OnPageLoaded()
{
    // ...现有加载逻辑...

    // 首次加载时异步刷新 VCS 状态
    await RefreshAllVcsStatusAsync();

    // 启动自动刷新（每 30 秒）
    StartAutoRefresh();
}

private async Task RefreshAllVcsStatusAsync()
{
    StatusText = "正在刷新仓库状态...";
    try
    {
        await _vcsStatusService.RefreshAllAsync(Repositories);
        StatusText = $"状态刷新完成 — {DateTime.Now:HH:mm:ss}";
    }
    catch (Exception ex)
    {
        StatusText = $"状态刷新失败: {ex.Message}";
    }
}

private void StartAutoRefresh()
{
    _autoRefreshCts?.Cancel();
    _autoRefreshCts = new CancellationTokenSource();
    var token = _autoRefreshCts.Token;

    Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            if (!token.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await _vcsStatusService.RefreshAllAsync(Repositories, token);
                });
            }
        }
    }, token);
}

private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
{
    await RefreshAllVcsStatusAsync();
}

// 页面卸载时停止自动刷新
private void OnPageUnloaded()
{
    _autoRefreshCts?.Cancel();
    _vcsStatusService.CancelRefresh();
}
```

---

## 六、操作按钮更新

### 6.1 ActionButtons 调整

```csharp
public List<ButtonConfig> ActionButtons => new List<ButtonConfig>
{
    new ButtonConfig { Text = "Claude提交", Width = 90, Height = 26, CommandProperty = nameof(ClaudeCommitCommand) },
    new ButtonConfig { Text = "Codex提交", Width = 86, Height = 26, CommandProperty = nameof(CodexCommitCommand) },
    new ButtonConfig { Text = "VS", Width = 42, Height = 26, CommandProperty = nameof(OpenVSCommand) },
    new ButtonConfig { Text = "Rider", Width = 50, Height = 26, CommandProperty = nameof(OpenRiderCommand) },
    new ButtonConfig { Text = "Cursor", Width = 54, Height = 26, CommandProperty = nameof(OpenCursorCommand) },
    new ButtonConfig { Text = "Claude", Width = 58, Height = 26, CommandProperty = nameof(OpenClaudeCommand) },
    new ButtonConfig { Text = "Codex", Width = 54, Height = 26, CommandProperty = nameof(OpenCodexCommand) },
    new ButtonConfig { Text = "文件夹", Width = 52, Height = 26, CommandProperty = nameof(OpenFolderCommand) },
};
```

`DataGridMultiButton` 的 Width 改为 `"720"`。

---

## 七、自动刷新策略

### 7.1 刷新时机

| 触发 | 行为 |
|------|------|
| 页面加载 | 异步刷新全部仓库状态 |
| 手动点击"刷新状态" | 异步刷新全部仓库状态 |
| 定时器（30秒） | 后台静默刷新全部仓库状态 |
| 执行 Git/SVN 操作后 | 立即刷新相关仓库状态 |
| 从 IDE 返回焦点 | 刷新全部（可选，通过 Window.Activated 事件） |

### 7.2 性能保障

- **并发限制**: 最多 4 个仓库同时检测，避免 CPU 占满
- **命令超时**: 单个 git/svn 命令最多 5 秒，超时即放弃
- **节流控制**: 同一仓库 10 秒内不重复刷新
- **取消机制**: 页面卸载或新一轮刷新自动取消上一轮
- **优先刷新**: 可见行的仓库优先刷新（后续优化）

### 7.3 缓存策略

- 状态数据仅保存在内存中，不持久化（每次启动重新检测）
- `_lastRefreshTimes` 字典记录每个仓库的上次刷新时间
- 自动刷新时跳过最近 10 秒内已刷新的仓库

---

## 八、实施步骤

### Phase 1: 数据模型（1-2天）
1. 创建 `VcsType.cs`、`VcsStatus.cs` 枚举
2. 创建 `SubRepository.cs` 模型
3. 扩展 `CodeRepository.cs`，增加 VCS 状态属性
4. 调整现有 DataGridColumn 属性序号和宽度

### Phase 2: 状态检测服务（2-3天）
1. 创建 `VcsStatusService.cs`
2. 实现 Git 状态检测（branch, status, ahead/behind）
3. 实现 SVN 状态检测（revision, status）
4. 实现子仓库扫描逻辑
5. 实现并发控制和取消机制

### Phase 3: UI 集成（1-2天）
1. 调整 DataGrid 列宽和按钮宽度
2. 新增状态列、分支列、变更列
3. 集成 VcsStatusService 到 CodeWorkspacePage
4. 增加"刷新状态"按钮
5. 实现自动刷新定时器

### Phase 4: 行展开详情（2-3天）
1. 实现 DataGrid 行展开面板模板
2. 显示 Git 详细状态（文件列表、ahead/behind）
3. 显示 SVN 子仓库列表和状态
4. 增加展开面板内的快速操作按钮（Pull/Push/Stash）

### Phase 5: 测试与优化（1-2天）
1. 多种仓库类型测试（纯Git/纯SVN/混合）
2. 大仓库性能测试
3. 网络异常/命令不存在等异常场景测试
4. UI 响应性验证

**总预估工期**: 7-12天

---

## 九、涉及文件清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Features\CodeWorkspace\Models\VcsType.cs` | 新增 | VCS 类型枚举 |
| `Features\CodeWorkspace\Models\VcsStatus.cs` | 新增 | VCS 状态枚举 |
| `Features\CodeWorkspace\Models\SubRepository.cs` | 新增 | 子仓库模型 |
| `Features\CodeWorkspace\Models\CodeRepository.cs` | 修改 | 增加 VCS 状态属性，调整列序号和宽度 |
| `Features\CodeWorkspace\Services\VcsStatusService.cs` | 新增 | VCS 状态检测服务 |
| `Features\CodeWorkspace\Views\CodeWorkspacePage.xaml` | 修改 | 增加刷新状态按钮 |
| `Features\CodeWorkspace\Views\CodeWorkspacePage.xaml.cs` | 修改 | 集成 VcsStatusService，自动刷新 |

---

## 十、风险评估

| 风险 | 影响 | 缓解措施 |
|------|------|---------|
| Git/SVN 未安装 | 命令执行失败 | 启动时检测可用性，未安装时显示灰色状态 |
| 大仓库 `git status` 慢 | UI 卡顿 | 异步执行 + 5秒超时 |
| SVN 子仓库层级很深 | 扫描耗时 | 限制扫描深度为2层 |
| 混合仓库状态矛盾 | 用户困惑 | Tooltip 明确标注每个 VCS 的独立状态 |
| CDataGrid 不支持自定义模板列 | 无法显示彩色圆点 | 改用文字标识 [G]/[S] + 颜色文字 |
