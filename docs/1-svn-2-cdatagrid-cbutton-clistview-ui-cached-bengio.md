# 代码工作区改进计划

## 背景

用户在使用代码工作区功能时发现了几个问题：

1. **SVN变更检测不准确**：TortoiseSVN显示"No files were changed"，但DevKit检测到"1项变更"
2. **管理仓库菜单导致数据重新加载**：打开并关闭"管理仓库"对话框后，代码工作区重新加载所有数据，扫描状态被重置
3. **UI控件不统一**：当前混用标准WPF控件和自定义控件，需要统一使用CDataGrid、CButton、CListView
4. **扫描速度慢**：每次进入页面都要重新扫描，没有持久化缓存
5. **缺少拉取功能**：已有提交和推送功能，但缺少拉取代码的操作，需要针对Git+SVN混合仓库做特殊处理

## 问题分析

### 1. SVN变更检测问题

**根本原因**：
- `VcsStatusService.cs:400-407` 中，`svn status` 命令的输出被直接按行数统计
- `SplitLines` 方法（464-468行）只移除空行，但 `svn status` 可能输出非文件变更的信息
- 没有检查每行的状态码，可能将非变更行（如状态信息、警告、外部引用X）计入变更数

**解决思路**：
- 解析 `svn status` 输出时检查每行的状态码
- 只统计真正的文件变更（A、M、D、C、!、~、R等）
- 忽略外部引用（X）、属性变更（如果不需要）、空行、警告信息

### 2. 管理仓库导致重新加载问题

**根本原因**：
- `CodeWorkspacePage.xaml.cs:191-207` 中，`ManageRepositoriesButton_Click` 方法
- 第205行：`LoadRepositories()` 清空并重新加载所有仓库
- 第206行：`RefreshAllVcsStatusAsync(forceRefresh: true)` 强制刷新所有状态
- `LoadRepositories()` 方法（98-117行）会 `Repositories.Clear()`，导致所有扫描状态丢失

**解决思路**：
- 不应该完全清空并重新加载，而应该增量更新
- 保留已有仓库的VCS状态信息
- 只对新增或修改的仓库进行刷新

### 3. UI控件统一问题

**当前状态**：
- `CodeWorkspacePage.xaml` 中混用了：
  - 标准 `Button`（第42-46行）
  - `button:CButton`（第47-50行）
  - 标准 `DataGrid`（第85行）
  - 标准 `ListBox`（第345行）

**目标**：
- 将所有 `Button` 替换为 `button:CButton`
- 将 `DataGrid` 替换为 `CDataGrid`（需要确认CustomControlLibrary中是否存在）
- 将 `ListBox` 替换为 `CListView`（需要确认CustomControlLibrary中是否存在）

**注意**：CustomControlLibrary是外部DLL（`Assets\Tools\CustomControlLibrary.dll`），需要先确认是否包含CDataGrid和CListView控件。

### 4. 自动刷新间隔调整

**当前行为**：
- `Page_Loaded`（119-128行）：首次加载时强制刷新所有仓库
- `StartAutoRefresh`（296-322行）：每30秒自动刷新一次
- `MinRefreshInterval = 10秒`（VcsStatusService.cs:15）：防止频繁刷新

**用户需求**：
- 软件启动后立即开始扫描
- 自动刷新间隔改为1分钟（60秒）
- 不需要持久化缓存，用户可以手动刷新

**解决思路**：
- 将 `StartAutoRefresh` 中的刷新间隔从30秒改为60秒
- 保持现有的手动刷新功能

### 5. 拉取代码功能

**当前功能**：
- Claude提交、Codex提交（371-410行）
- 打开各种IDE和工具

**需要添加**：
- Git pull 功能
- SVN update 功能
- 混合仓库的拉取策略：
  - Git根仓库执行 `git pull`
  - 每个SVN子仓库执行 `svn update`
  - 并行执行以提高速度

**冲突处理策略（简化）**：
- 检测冲突：`git pull` 失败或 `svn update` 返回冲突状态
- 如果有冲突：
  - 显示提示消息："检测到冲突，请在IDE中手动解决"
  - 提供"打开文件夹"按钮，让用户在IDE中处理
  - 不实现自动冲突解决功能
- 如果拉取成功：
  - 显示成功消息
  - 自动刷新VCS状态

## 实施计划

### Phase 1: 修复SVN变更检测

**文件**：`Features/CodeWorkspace/Services/VcsStatusService.cs`

**修改**：
1. 在 `RefreshSubRepositoriesAsync` 方法（378-417行）中，解析 `svn status` 输出时检查状态码
2. 修改第400-407行的逻辑：
   ```csharp
   var lines = SplitLines(statusResult.Output)
       .Where(line => line.Length > 0 && IsValidSvnChangeStatus(line[0]))
       .ToList();
   ```
3. 添加 `IsValidSvnChangeStatus` 方法：
   ```csharp
   private static bool IsValidSvnChangeStatus(char statusCode)
   {
       // 只统计真正的文件变更
       // A=添加, M=修改, D=删除, C=冲突, !=缺失, ~=类型变更, R=替换
       return statusCode == 'A' || statusCode == 'M' || statusCode == 'D' || 
              statusCode == 'C' || statusCode == '!' || statusCode == '~' || 
              statusCode == 'R';
       // 忽略: X=外部引用, ?=未版本控制, I=忽略
   }
   ```

**验证**：
- 对比TortoiseSVN和DevKit的检测结果
- 测试各种SVN状态（干净、有变更、有冲突、有外部引用）

### Phase 2: 优化管理仓库后的重新加载

**文件**：`Features/CodeWorkspace/Views/CodeWorkspacePage.xaml.cs`

**修改**：
1. 修改 `ManageRepositoriesButton_Click` 方法（191-207行）：
   ```csharp
   private void ManageRepositoriesButton_Click(object sender, RoutedEventArgs e)
   {
       var page = new CodeRepositoryManagementPage();
       var window = new Window
       {
           Title = "代码仓库管理",
           Content = page,
           Width = 900,
           Height = 560,
           Owner = Window.GetWindow(this),
           WindowStartupLocation = WindowStartupLocation.CenterOwner,
       };
       page.RequestExit += window.Close;
       window.ShowDialog();
       
       // 增量更新，保留VCS状态
       SyncRepositories();
   }
   ```

2. 添加 `SyncRepositories` 方法：
   ```csharp
   private void SyncRepositories()
   {
       var settings = _dataPersistenceService.LoadSettings();
       var newRepos = settings.CodeRepositories ?? new List<CodeRepository>();
       
       // 保留已有仓库的VCS状态
       var existingRepos = Repositories.ToDictionary(r => NormalizePath(r.Path), StringComparer.OrdinalIgnoreCase);
       
       Repositories.Clear();
       foreach (var repo in newRepos.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
                                     .OrderByDescending(r => r.LastUsed)
                                     .ThenBy(r => r.Name))
       {
           var cloned = repo.Clone();
           var normalizedPath = NormalizePath(cloned.Path);
           
           // 如果仓库已存在，保留VCS状态
           if (existingRepos.TryGetValue(normalizedPath, out var existing))
           {
               cloned.VcsType = existing.VcsType;
               cloned.VcsStatus = existing.VcsStatus;
               cloned.GitBranch = existing.GitBranch;
               cloned.GitAheadCount = existing.GitAheadCount;
               cloned.GitBehindCount = existing.GitBehindCount;
               cloned.AddedCount = existing.AddedCount;
               cloned.ModifiedCount = existing.ModifiedCount;
               cloned.DeletedCount = existing.DeletedCount;
               cloned.StagedCount = existing.StagedCount;
               cloned.SvnRevision = existing.SvnRevision;
               cloned.SubRepositories = existing.SubRepositories;
               cloned.LastStatusRefresh = existing.LastStatusRefresh;
               cloned.HasConflict = existing.HasConflict;
           }
           
           SetupRepositoryCommands(cloned);
           Repositories.Add(cloned);
       }
       
       StatusText = Repositories.Count == 0
           ? "未配置代码仓库，请点击管理仓库添加。"
           : $"已加载 {Repositories.Count} 个仓库。";
       SelectedRepository = Repositories.FirstOrDefault();
       RaisePropertyChanged(nameof(RepositoryCountText));
   }
   ```

**验证**：
- 打开管理仓库对话框，添加/删除/修改仓库
- 关闭对话框后，检查扫描状态是否保留

### Phase 3: 调整自动刷新间隔

**文件**：`Features/CodeWorkspace/Views/CodeWorkspacePage.xaml.cs`

**修改**：
- 修改 `StartAutoRefresh` 方法（296-322行）第305行：
  ```csharp
  await Task.Delay(TimeSpan.FromSeconds(60), token);  // 从30秒改为60秒
  ```

**验证**：
- 启动软件，观察自动刷新间隔是否为1分钟

### Phase 4: 统一UI控件

**文件**：`Features/CodeWorkspace/Views/CodeWorkspacePage.xaml`

**前置条件**：需要先确认CustomControlLibrary中是否包含CDataGrid和CListView

**修改**：
1. 将第42-46行的标准 `Button` 替换为 `button:CButton`：
   ```xml
   <button:CButton Content="{Binding RefreshButtonText}"
                   Width="92"
                   Height="30"
                   Margin="8,0,0,0"
                   IsEnabled="{Binding CanRefreshStatus}"
                   Click="RefreshStatusButton_Click"/>
   ```

2. 如果存在CDataGrid，将第85行的 `DataGrid` 替换为对应的自定义控件
3. 如果存在CListView，将第345行的 `ListBox` 替换为对应的自定义控件

**备选方案**：
- 如果CustomControlLibrary中不存在CDataGrid/CListView：
  - 保持使用标准DataGrid和ListBox
  - 创建统一的样式资源使外观一致

**验证**：
- 检查UI渲染是否正常
- 测试所有交互功能（选择、双击、右键菜单等）

### Phase 5: 添加拉取代码功能

**文件**：
- `Features/CodeWorkspace/Views/CodeWorkspacePage.xaml.cs`
- `Features/CodeWorkspace/Views/CodeWorkspacePage.xaml`

**新增功能**：

1. **UI部分** - 在XAML中添加拉取选项：
   ```xml
   <MenuItem Header="拉取代码" Command="{Binding PullCommand}" Style="{StaticResource ActionMenuItemStyle}"/>
   ```

2. **后端逻辑** - 在CodeWorkspacePage.xaml.cs中添加：
   ```csharp
   private void SetupRepositoryCommands(CodeRepository repo)
   {
       // ... 现有命令 ...
       repo.PullCommand = new RelayCommand(() => RunRepositoryAction(repo, DoPullRepository));
   }
   
   private async void DoPullRepository(CodeRepository repo)
   {
       StatusText = $"正在拉取代码: {repo.Name}";
       try
       {
           repo.IsRefreshing = true;
           var result = await PullRepositoryAsync(repo);
           
           if (result.HasConflicts)
           {
               var conflictMsg = "检测到冲突，请在IDE中手动解决。\n\n";
               if (result.GitConflicts.Count > 0)
               {
                   conflictMsg += $"Git冲突文件: {string.Join(", ", result.GitConflicts)}\n";
               }
               if (result.SvnConflicts.Count > 0)
               {
                   conflictMsg += $"SVN冲突: {string.Join(", ", result.SvnConflicts)}";
               }
               
               var dialogResult = MessageBox.Show(
                   conflictMsg + "\n是否打开文件夹？",
                   "拉取代码 - 冲突",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning);
               
               if (dialogResult == MessageBoxResult.Yes)
               {
                   DoOpenFolder(repo);
               }
               
               StatusText = $"拉取失败（有冲突）: {repo.Name}";
           }
           else if (result.Success)
           {
               StatusText = $"拉取成功: {repo.Name}";
               await _vcsStatusService.RefreshRepositoryStatusAsync(repo, forceRefresh: true);
               RefreshSubRepositoryView();
           }
           else
           {
               StatusText = $"拉取失败: {result.ErrorMessage}";
               MessageBox.Show($"拉取失败: {result.ErrorMessage}", "拉取代码", MessageBoxButton.OK, MessageBoxImage.Error);
           }
       }
       catch (Exception ex)
       {
           LoggingService.LogError(ex, $"拉取代码失败: {repo.Path}");
           StatusText = $"拉取失败: {ex.Message}";
           MessageBox.Show($"拉取失败: {ex.Message}", "拉取代码", MessageBoxButton.OK, MessageBoxImage.Error);
       }
       finally
       {
           repo.IsRefreshing = false;
       }
   }
   
   private async Task<PullResult> PullRepositoryAsync(CodeRepository repo)
   {
       var result = new PullResult { Success = true };
       
       // Git pull
       if (repo.VcsType == VcsType.Git || repo.VcsType == VcsType.Mixed)
       {
           var gitResult = await RunCommandAsync("git", "pull", repo.Path);
           if (gitResult.ExitCode != 0)
           {
               result.Success = false;
               result.ErrorMessage = gitResult.Error;
               
               // 检测Git冲突
               if (gitResult.Output.Contains("CONFLICT") || gitResult.Error.Contains("CONFLICT"))
               {
                   result.HasConflicts = true;
                   result.GitConflicts = ParseGitConflicts(gitResult.Output + gitResult.Error);
               }
           }
       }
       
       // SVN update (并行)
       if (repo.VcsType == VcsType.Svn || 
           (repo.VcsType == VcsType.Mixed && repo.SubRepositories?.Count > 0))
       {
           var svnPaths = repo.VcsType == VcsType.Svn 
               ? new[] { repo.Path }
               : repo.SubRepositories.Select(s => Path.Combine(repo.Path, s.RelativePath)).ToArray();
           
           var svnTasks = svnPaths.Select(async path =>
           {
               var svnResult = await RunCommandAsync("svn", "update", path);
               return new { Path = path, Result = svnResult };
           });
           
           var svnResults = await Task.WhenAll(svnTasks);
           
           foreach (var svnResult in svnResults)
           {
               if (svnResult.Result.ExitCode != 0)
               {
                   result.Success = false;
                   result.ErrorMessage += $"\nSVN更新失败 ({Path.GetFileName(svnResult.Path)}): {svnResult.Result.Error}";
               }
               
               // 检测SVN冲突
               if (svnResult.Result.Output.Contains("conflict") || svnResult.Result.Output.Contains("C "))
               {
                   result.HasConflicts = true;
                   result.SvnConflicts.Add(Path.GetFileName(svnResult.Path));
               }
           }
       }
       
       return result;
   }
   
   private async Task<CommandResult> RunCommandAsync(string fileName, string arguments, string workingDirectory)
   {
       try
       {
           var startInfo = new ProcessStartInfo
           {
               FileName = fileName,
               Arguments = arguments,
               WorkingDirectory = workingDirectory,
               RedirectStandardOutput = true,
               RedirectStandardError = true,
               UseShellExecute = false,
               CreateNoWindow = true,
           };
           
           using (var process = new Process { StartInfo = startInfo })
           {
               process.Start();
               var outputTask = process.StandardOutput.ReadToEndAsync();
               var errorTask = process.StandardError.ReadToEndAsync();
               await Task.Run(() => process.WaitForExit(30000)); // 30秒超时
               
               return new CommandResult
               {
                   ExitCode = process.ExitCode,
                   Output = await outputTask,
                   Error = await errorTask,
               };
           }
       }
       catch (Exception ex)
       {
           return new CommandResult { ExitCode = -1, Output = string.Empty, Error = ex.Message };
       }
   }
   
   private List<string> ParseGitConflicts(string output)
   {
       var conflicts = new List<string>();
       var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
       foreach (var line in lines)
       {
           if (line.Contains("CONFLICT") && line.Contains("in "))
           {
               var parts = line.Split(new[] { " in " }, StringSplitOptions.None);
               if (parts.Length > 1)
               {
                   conflicts.Add(parts[1].Trim());
               }
           }
       }
       return conflicts;
   }
   
   private class PullResult
   {
       public bool Success { get; set; }
       public bool HasConflicts { get; set; }
       public string ErrorMessage { get; set; }
       public List<string> GitConflicts { get; } = new List<string>();
       public List<string> SvnConflicts { get; } = new List<string>();
   }
   
   private class CommandResult
   {
       public int ExitCode { get; set; }
       public string Output { get; set; }
       public string Error { get; set; }
   }
   ```

3. **在CodeRepository模型中添加PullCommand属性**：
   ```csharp
   public ICommand PullCommand { get; set; }
   ```

**验证**：
- 测试Git仓库拉取（无冲突）
- 测试SVN仓库更新（无冲突）
- 测试混合仓库拉取（无冲突）
- 测试冲突场景，确认提示正确显示

## 关键文件

- `Features/CodeWorkspace/Services/VcsStatusService.cs` - VCS状态检测服务
- `Features/CodeWorkspace/Views/CodeWorkspacePage.xaml.cs` - 代码工作区页面逻辑
- `Features/CodeWorkspace/Views/CodeWorkspacePage.xaml` - 代码工作区页面UI
- `Features/CodeWorkspace/Models/CodeRepository.cs` - 仓库模型
- `Features/CodeWorkspace/Models/SubRepository.cs` - 子仓库模型
- `Assets/Tools/CustomControlLibrary.dll` - 自定义控件库（外部DLL）

## 风险和注意事项

1. **CustomControlLibrary依赖**：
   - 需要确认CDataGrid和CListView是否存在
   - 如果不存在，保持使用标准控件并统一样式

2. **SVN状态检测**：
   - 不同SVN版本的输出格式可能不同
   - 需要测试多种场景（外部引用、属性变更等）

3. **拉取操作的安全性**：
   - 拉取可能导致数据丢失（如果有未提交的变更）
   - 建议在拉取前检查工作区状态，如果有未提交变更则提示用户
   - 提供警告和确认对话框

4. **并发问题**：
   - 多个SVN子仓库并行更新时可能出现问题
   - 需要适当的错误处理和超时机制

5. **冲突处理简化**：
   - 不实现自动冲突解决，只提示用户在IDE中处理
   - 降低了复杂度，但需要用户有一定的Git/SVN知识

## 实施顺序

建议按以下顺序实施，每个阶段完成后进行测试：

1. **Phase 1**：修复SVN变更检测（最紧急，影响数据准确性）
2. **Phase 2**：优化管理仓库后的重新加载（改善用户体验）
3. **Phase 3**：调整自动刷新间隔（简单修改）
4. **Phase 5**：添加拉取代码功能（新功能，优先级高）
5. **Phase 4**：统一UI控件（视觉优化，优先级较低）

Phase 4放在最后是因为：
- 需要先确认CustomControlLibrary的能力
- 不影响核心功能
- 可以独立进行，不阻塞其他改进

## 用户需求总结

根据用户反馈，最终需求为：
1. ✅ 修复SVN变更检测不准确的问题
2. ✅ 管理仓库对话框关闭后保留扫描状态
3. ✅ 统一使用CButton、CDataGrid、CListView控件
4. ✅ 自动刷新间隔改为1分钟（60秒）
5. ✅ 添加拉取代码功能（Git pull + SVN update）
6. ✅ 冲突处理：简单提示，引导用户在IDE中手动解决
7. ❌ 不需要持久化VCS状态缓存
