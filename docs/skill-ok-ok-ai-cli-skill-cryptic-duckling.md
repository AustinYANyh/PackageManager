# 仪表盘代码工作区功能设计方案

## Context

用户创建了 `git_svn_commitlog_generator` skill，用于自动采集 Git/SVN 改动、生成提交日志并推送。目前需要手动进入 Claude Code CLI 调用，用户觉得繁琐。

需要在 PackageManager 仪表盘中实现"代码工作区"功能：
1. 代码仓库管理（设置代码根目录）
2. 每个仓库一行，每行多个操作按钮（列布局）
3. 支持 IDE 快捷打开（VS/Rider/Cursor）、AI 工具启动（Claude Code/Codex）、代码提交
4. 自动处理 Claude Code 的 accept 交互和 Codex 的权限设置

## 技术背景

- **技术栈**：WPF + MVVM
- **导航**：NavigationService + ToolRegistry
- **配置**：DataPersistenceService，JSON 格式，位于 `%AppData%\PackageManager\`
- **进程调用**：Process.Start + 异步执行（参考 EmbeddedToolRunnerService）
- **常用启动项**：CommonStartupItem（Name, FullPath, Arguments, GroupName），保存在 `common_startup_settings.json`
- **终端启动**：已有 `CreatePowerShellTerminalStartInfo` 实现，支持 Windows Terminal + pwsh.exe + Base64 编码命令

## 实现计划

### 1. 数据模型

**新文件**：`Features\CodeWorkspace\Models\CodeRepository.cs`

```csharp
public class CodeRepository
{
    public string Name { get; set; }
    public string Path { get; set; }
    public DateTime LastUsed { get; set; }
    public int UsageCount { get; set; }
    public string Note { get; set; } = "";
    public List<string> ProjectFiles { get; set; } = new List<string>();
}
```

**修改**：`Models\AppSettings.cs` 添加字段：
```csharp
public List<CodeRepository> CodeRepositories { get; set; } = new List<CodeRepository>();
public string LastUsedRepositoryPath { get; set; }
```

### 2. 代码仓库管理页面

**新文件**：`Features\CodeWorkspace\Views\CodeRepositoryManagementPage.xaml` + `.xaml.cs`

UI 布局（参考 PackageConfigPage）：
- 顶部工具栏："新增仓库"、"刷新项目文件"、"保存"、"返回"
- 中部：CDataGrid 显示仓库列表（名称、路径、最后使用时间、使用次数、操作按钮）
- 支持拖放文件夹添加仓库

注册到导航系统（`Shell\ToolRegistration.cs`）：
```csharp
registry.Register(new ToolPageDescriptor
{
    Key = "code-repository-management",
    DisplayName = "代码仓库管理",
    Group = "开发工具",
    Factory = () => new CodeRepositoryManagementPage()
});
```

### 3. 代码工作区窗口（核心）

**新文件**：`Features\CodeWorkspace\Views\CodeWorkspaceWindow.xaml` + `.xaml.cs`

#### 3.1 UI 布局：列式表格，每个仓库一行

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│ [管理仓库]  [刷新项目文件]                                                        │
├──────────┬──────┬──────┬──────┬──────┬──────┬──────┬──────┐                     │
│ 仓库     │ 提交 │  VS  │Rider │Cursor│Claude│Codex │文件夹│                     │
├──────────┼──────┼──────┼──────┼──────┼──────┼──────┼──────┤                     │
│ PkgMgr   │  ●   │  ●   │  ●   │  ●   │  ●   │  ●   │  ●  │                     │
│ E:\Pkg.. │      │      │      │      │      │      │      │                     │
├──────────┼──────┼──────┼──────┼──────┼──────┼──────┼──────┤                     │
│ WebApp   │  ●   │  ●   │  ●   │  ●   │  ●   │  ●   │  ●  │                     │
│ D:\Web.. │      │      │      │      │      │      │      │                     │
└──────────┴──────┴──────┴──────┴──────┴──────┴──────┴──────┘                     │
│ 状态栏：10:30:15 - 已在 Visual Studio 中打开 PackageManager.csproj               │
└─────────────────────────────────────────────────────────────────────────────────┘
```

XAML 关键结构：
```xml
<DataGrid ItemsSource="{Binding Repositories}" AutoGenerateColumns="False"
          CanUserAddRows="False" IsReadOnly="True" SelectionMode="Single">
    <DataGrid.Columns>
        <!-- 仓库名称列 -->
        <DataGridTemplateColumn Header="仓库" Width="200">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <StackPanel Margin="4">
                        <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                        <TextBlock Text="{Binding Path}" FontSize="11" Foreground="#888"
                                   TextTrimming="CharacterEllipsis"/>
                    </StackPanel>
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
        
        <!-- 操作按钮列（每列一个按钮） -->
        <DataGridTemplateColumn Header="提交" Width="70">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <Button Content="提交" Tag="commit" Click="ActionButton_Click"
                            Style="{StaticResource GreenActionButton}"/>
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
        
        <DataGridTemplateColumn Header="VS" Width="70">
            <DataGridTemplateColumn.CellTemplate>
                <DataTemplate>
                    <Button Content="VS" Tag="vs" Click="ActionButton_Click"/>
                </DataTemplate>
            </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
        
        <!-- Rider / Cursor / Claude / Codex / 文件夹 列同理 -->
    </DataGrid.Columns>
</DataGrid>
```

#### 3.2 统一事件处理

```csharp
private void ActionButton_Click(object sender, RoutedEventArgs e)
{
    var button = sender as Button;
    var repo = button?.DataContext as CodeRepository;
    if (repo == null) return;
    
    var action = button.Tag as string;
    switch (action)
    {
        case "commit":  DoCodeCommit(repo); break;
        case "vs":      DoOpenIDE(repo, "Visual Studio", "VS"); break;
        case "rider":   DoOpenIDE(repo, "Rider", "JetBrains Rider"); break;
        case "cursor":  DoOpenCursor(repo); break;
        case "claude":  DoOpenClaudeCode(repo); break;
        case "codex":   DoOpenCodex(repo); break;
        case "folder":  DoOpenFolder(repo); break;
    }
    
    repo.LastUsed = DateTime.Now;
    repo.UsageCount++;
    SaveSettings();
}
```

### 4. Claude Code 自动化处理

#### 问题
`claude --dangerously-skip-permissions` 首次运行时需要交互式输入 `accept` 接受条款。

#### 解决方案（三层递进）

```csharp
private void DoOpenClaudeCode(CodeRepository repo)
{
    // 方案1：检查是否已接受条款（读取 ~/.claude/.accepted-terms）
    var termsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", ".accepted-terms");
    
    bool alreadyAccepted = File.Exists(termsFile);
    
    if (alreadyAccepted)
    {
        // 已接受，直接启动
        var command = $"cd '{repo.Path}'; claude --dangerously-skip-permissions";
        LaunchTerminalWithCommand(command, $"Claude Code - {repo.Name}");
        UpdateStatus($"已启动 Claude Code：{repo.Name}");
    }
    else
    {
        // 方案2：使用 echo 管道自动输入 accept
        var command = $@"
cd '{repo.Path}'
$env:CLAUDE_ACCEPT_TERMS = 'yes'
echo 'accept' | claude --dangerously-skip-permissions
if ($LASTEXITCODE -ne 0) {{
    Write-Host '自动接受失败，请手动输入 accept 后回车' -ForegroundColor Yellow
    claude --dangerously-skip-permissions
}}
";
        LaunchTerminalWithCommand(command, $"Claude Code - {repo.Name}");
        
        // 方案3：如果管道方式不支持，提示用户
        UpdateStatus("首次启动 Claude Code，如需手动输入 accept 请在终端中操作");
    }
}
```

#### 关键点
- 检查 `~/.claude/.accepted-terms` 或 `~/.claude/config.json` 判断是否已接受
- 尝试环境变量 `CLAUDE_ACCEPT_TERMS=yes` 跳过交互
- 尝试 `echo 'accept' | claude` 管道输入
- 以上都失败时，回退到普通启动，终端中用户手动输入

### 5. Codex 权限自动化处理

#### 问题
Codex 启动后需要执行 `/permissions` 并选择 full access，这是交互式操作。

#### 解决方案（三层递进）

```csharp
private void DoOpenCodex(CodeRepository repo)
{
    // 方案1（推荐）：通过配置文件预设权限
    bool configSuccess = TryPresetCodexPermissions(repo.Path);
    
    if (configSuccess)
    {
        // 配置文件已设置，直接启动
        var command = $"cd '{repo.Path}'; codex";
        LaunchTerminalWithCommand(command, $"Codex - {repo.Name}");
        UpdateStatus($"已启动 Codex（已预设 full access）：{repo.Name}");
        return;
    }
    
    // 方案2：使用 SendKeys 自动输入（不可靠但可尝试）
    var autoCommand = $@"
cd '{repo.Path}'

# 后台延迟发送按键
$job = Start-Job -ScriptBlock {{
    Start-Sleep -Seconds 3
    Add-Type -AssemblyName System.Windows.Forms
    # 输入 /permissions 回车
    [System.Windows.Forms.SendKeys]::SendWait('/permissions{{ENTER}}')
    Start-Sleep -Seconds 1
    # 选择 full access（通常是选项1）
    [System.Windows.Forms.SendKeys]::SendWait('1{{ENTER}}')
}}

codex
";
    LaunchTerminalWithCommand(autoCommand, $"Codex - {repo.Name}");
    
    // 方案3：提示用户手动操作
    UpdateStatus("已启动 Codex。如自动权限设置失败，请手动执行 /permissions → full access");
}

/// 尝试通过配置文件预设 Codex 权限
private bool TryPresetCodexPermissions(string repoPath)
{
    try
    {
        // Codex 可能读取项目级配置 .codex/config.json 或全局 ~/.codex/config.json
        // 尝试写入项目级配置
        var codexConfigDir = Path.Combine(repoPath, ".codex");
        if (!Directory.Exists(codexConfigDir))
            Directory.CreateDirectory(codexConfigDir);
        
        var configPath = Path.Combine(codexConfigDir, "config.json");
        var config = new
        {
            permissions = "full-access",
            auto_approve = true
        };
        
        File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
        return true;
    }
    catch
    {
        return false;
    }
}
```

#### 方案对比

| 方案 | 可靠性 | 说明 |
|------|--------|------|
| 配置文件预设 | 高（如果 Codex 支持） | 写入 `.codex/config.json`，需要确认 Codex 是否读取此配置 |
| SendKeys 自动输入 | 中 | 依赖窗口焦点和时序，可能因 Codex 启动慢而失败 |
| 命令行参数 | 高（如果支持） | 如 `codex --full-access`，需确认是否有此参数 |
| 用户手动操作 | 100% | 兜底方案，弹出提示告知用户操作步骤 |

#### 实际实现策略
1. 先尝试配置文件方案
2. 失败则尝试 SendKeys
3. 无论如何都在状态栏提示用户检查权限是否已设置成功
4. 后续可根据 Codex 版本更新调整自动化方式

### 6. IDE 打开与项目文件选择

#### 从常用启动项获取 IDE 路径
```csharp
private string GetToolPathFromCommonStartup(params string[] possibleNames)
{
    var settings = ServiceLocator.Resolve<DataPersistenceService>().LoadSettings();
    var items = settings.CommonStartupItems;
    if (items == null) return null;
    
    foreach (var name in possibleNames)
    {
        var item = items.FirstOrDefault(i =>
            i.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (item != null && File.Exists(item.FullPath))
            return item.FullPath;
    }
    return null;
}
```

#### 项目文件选择逻辑
```csharp
private string SelectProjectFile(CodeRepository repo)
{
    if (repo.ProjectFiles == null || repo.ProjectFiles.Count == 0)
    {
        repo.ProjectFiles = Directory.EnumerateFiles(
            repo.Path, "*.csproj", SearchOption.AllDirectories)
            .Take(50).ToList();
    }
    
    if (repo.ProjectFiles.Count == 0)
    {
        MessageBox.Show("未找到 .csproj 项目文件", "提示");
        return null;
    }
    
    if (repo.ProjectFiles.Count == 1)
        return repo.ProjectFiles[0];
    
    // 多个项目文件 → 弹出选择对话框
    var dialog = new ProjectFileSelectionDialog(repo.ProjectFiles, repo.Path);
    return dialog.ShowDialog() == true ? dialog.SelectedProjectFile : null;
}
```

#### 项目文件选择对话框
**新文件**：`Features\CodeWorkspace\Views\ProjectFileSelectionDialog.xaml` + `.xaml.cs`

```xml
<Window Title="选择项目文件" Width="500" Height="400">
    <DockPanel>
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="8">
            <Button Content="确定" Width="80" Click="OK_Click"/>
            <Button Content="取消" Width="80" Click="Cancel_Click" Margin="8,0,0,0"/>
        </StackPanel>
        <ListView x:Name="ProjectFilesListView" MouseDoubleClick="ListView_DoubleClick">
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="项目名称" Width="180"
                                    DisplayMemberBinding="{Binding DisplayName}"/>
                    <GridViewColumn Header="相对路径" Width="280"
                                    DisplayMemberBinding="{Binding RelativePath}"/>
                </GridView>
            </ListView.View>
        </ListView>
    </DockPanel>
</Window>
```

显示相对路径方便用户识别：
```csharp
public ProjectFileSelectionDialog(List<string> projectFiles, string rootPath)
{
    InitializeComponent();
    var items = projectFiles.Select(f => new {
        FullPath = f,
        DisplayName = Path.GetFileNameWithoutExtension(f),
        RelativePath = Path.GetRelativePath(rootPath, f)
    }).ToList();
    ProjectFilesListView.ItemsSource = items;
    if (items.Count > 0) ProjectFilesListView.SelectedIndex = 0;
}
```

### 7. 终端启动辅助

**新文件**：`Features\CodeWorkspace\Services\TerminalHelper.cs`

```csharp
public static class TerminalHelper
{
    public static void LaunchTerminalWithCommand(string command, string title)
    {
        var psPath = ResolvePowerShell7Path();
        var wtPath = ResolveWindowsTerminalPath();
        
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psArgs = $"-NoLogo -NoExit -EncodedCommand {encoded}";
        
        if (!string.IsNullOrWhiteSpace(wtPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = $"new-tab --title \"{title}\" \"{psPath}\" {psArgs}",
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = psArgs,
                UseShellExecute = true
            });
        }
    }
    
    public static string ResolvePowerShell7Path()
    {
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
            Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? "powershell.exe";
    }
    
    public static string ResolveWindowsTerminalPath()
    {
        var wtPath = Environment.ExpandEnvironmentVariables(
            @"%LocalAppData%\Microsoft\WindowsApps\wt.exe");
        return File.Exists(wtPath) ? wtPath : null;
    }
}
```

### 8. 仪表盘集成

**修改**：`Features\Dashboard\Views\DashboardPage.xaml` 添加卡片
**修改**：`Features\Dashboard\Views\DashboardPage.xaml.cs` 添加事件
**修改**：`Features\DevTools\DevToolLauncher.cs` 添加 `OpenCodeWorkspace()`

```csharp
case "tool:code-workspace":
    DevToolLauncher.OpenCodeWorkspace();
    break;
```

## 关键文件清单

### 新增
- `Features\CodeWorkspace\Models\CodeRepository.cs`
- `Features\CodeWorkspace\Views\CodeRepositoryManagementPage.xaml` + `.xaml.cs`
- `Features\CodeWorkspace\Views\CodeWorkspaceWindow.xaml` + `.xaml.cs`
- `Features\CodeWorkspace\Views\ProjectFileSelectionDialog.xaml` + `.xaml.cs`
- `Features\CodeWorkspace\Services\TerminalHelper.cs`

### 修改
- `Models\AppSettings.cs` — 添加 CodeRepositories 字段
- `Features\Dashboard\Views\DashboardPage.xaml` — 添加卡片
- `Features\Dashboard\Views\DashboardPage.xaml.cs` — 添加事件处理
- `Features\DevTools\DevToolLauncher.cs` — 添加 OpenCodeWorkspace
- `Shell\ToolRegistration.cs` — 注册仓库管理页面

## 验证计划

1. **仓库管理**：添加/编辑/删除仓库，保存后重启验证持久化
2. **IDE 打开**：单 csproj 直接打开，多 csproj 弹出选择，无 csproj 提示
3. **工具未配置**：未在常用启动项配置 IDE 时，提示跳转配置
4. **Claude Code**：已接受条款时直接进入，未接受时尝试自动 accept 或提示用户
5. **Codex**：验证配置文件方案是否生效，SendKeys 方案是否正确输入
6. **代码提交**：验证终端启动并进入 Claude Code CLI
7. **使用统计**：验证 LastUsed 和 UsageCount 正确更新，列表按最近使用排序
