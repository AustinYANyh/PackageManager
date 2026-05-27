# 代码工作区功能优化方案

## 背景

用户试用代码工作区功能后，提出了7个优化需求：

1. **代码工作区应该全屏显示**：当前是独立小窗口，应该像"管理仓库"那样在程序内全屏显示（高频操作）
2. **使用统一的UI组件**：按钮和表格应该使用CButton和CDataGrid，通过DataGridColumnAttribute控制列
3. **优先查找.sln文件**：仓库扫描应该优先查找.sln文件，不存在时才列出.csproj文件
4. **VS按钮打开的是VSCode**：CommonStartupItems中VS的快捷启动项名称为"VS"，但现在打开的是VSCode
5. **Claude按钮打不开Claude终端**：需要诊断和修复
6. **代码仓库管理应该弹出小窗**：保持弹窗形式，但按钮和表格要使用CButton和CDataGrid
7. **Codex打开的不是PowerShell 7**：打开的是Windows默认PowerShell 5.1，应该是PowerShell 7.5.5

## 关键文件

### 需要修改的文件

1. **Features/CodeWorkspace/Models/CodeRepository.cs** - 添加DataGrid Attributes和Command属性
2. **Features/CodeWorkspace/Views/CodeWorkspaceWindow.xaml** - 改为Page，使用CDataGrid和CButton
3. **Features/CodeWorkspace/Views/CodeWorkspaceWindow.xaml.cs** - 改为Page的代码后台，实现Command逻辑
4. **Features/CodeWorkspace/Views/CodeRepositoryManagementPage.xaml** - 改用CDataGrid和CButton
5. **Features/CodeWorkspace/Services/TerminalHelper.cs** - 改进PowerShell 7启动逻辑
6. **Features/DevTools/DevToolLauncher.cs** - 修改OpenCodeWorkspace方法支持页面导航
7. **Shell/ToolRegistration.cs** - 注册CodeWorkspacePage到导航系统

### 需要重命名的文件

- `CodeWorkspaceWindow.xaml` → `CodeWorkspacePage.xaml`
- `CodeWorkspaceWindow.xaml.cs` → `CodeWorkspacePage.xaml.cs`

## 实现方案

### 1. 代码工作区全屏显示（问题1）

**目标**：将CodeWorkspaceWindow改为CodeWorkspacePage，在主窗口Frame中全屏显示

**步骤**：

1. **重命名文件**：
   - `CodeWorkspaceWindow.xaml` → `CodeWorkspacePage.xaml`
   - `CodeWorkspaceWindow.xaml.cs` → `CodeWorkspacePage.xaml.cs`

2. **修改XAML**：
   - 将根元素从`<Window>`改为`<Page>`
   - 移除Window特有属性：`WindowStartupLocation`, `MinWidth`, `MinHeight`, `Width`, `Height`
   - 保持`Background="#F7F8FA"`

3. **修改代码后台**：
   - 类声明从`public partial class CodeWorkspaceWindow : Window`改为`public partial class CodeWorkspacePage : Page`
   - 移除Owner相关逻辑
   - 移除ActivateOwner方法

4. **注册到导航系统**（Shell/ToolRegistration.cs）：
   ```csharp
   registry.Register(new ToolPageDescriptor
   {
       Key = "code-workspace",
       DisplayName = "代码工作区",
       Factory = () => new CodeWorkspacePage()
   });
   ```

5. **修改启动入口**（Features/DevTools/DevToolLauncher.cs）：
   ```csharp
   public static void OpenCodeWorkspace(Window owner = null)
   {
       try
       {
           var navService = ServiceLocator.Resolve<NavigationService>();
           if (navService != null)
           {
               navService.NavigateTo("code-workspace");
               return;
           }
       }
       catch (Exception ex)
       {
           LoggingService.LogError(ex, "打开代码工作区失败");
       }
       
       MessageBox.Show("无法打开代码工作区，请从主界面导航。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Warning);
   }
   ```

### 2. 使用CButton和CDataGrid（问题2、6）

**目标**：使用统一的UI组件，通过DataGrid Attribute自动生成列

#### 2.1 CodeRepository模型改造

**添加命名空间**：
```csharp
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
```

**添加DataGrid Attributes**：

```csharp
// 基本信息列
[DataGridColumn(1, DisplayName = "仓库", Width = "250", IsReadOnly = true)]
public string Name { get; set; }

[DataGridColumn(2, DisplayName = "路径", Width = "*", IsReadOnly = true)]
public string Path { get; set; }

[DataGridColumn(3, DisplayName = "备注", Width = "180", IsReadOnly = true)]
public string Note { get; set; }

// 计算属性
[DataGridColumn(4, DisplayName = "项目文件", Width = "80", IsReadOnly = true)]
public int ProjectFileCount => ProjectFiles?.Count ?? 0;

[DataGridColumn(5, DisplayName = "最后使用", Width = "140", IsReadOnly = true)]
public string LastUsedText => LastUsed == DateTime.MinValue ? "从未使用" : LastUsed.ToString("yyyy-MM-dd HH:mm");

// 操作按钮列
[DataGridMultiButton(nameof(ActionButtons), 6, DisplayName = "操作", Width = "560", ButtonSpacing = 12)]
public string Actions { get; set; }

// 按钮配置
public List<ButtonConfig> ActionButtons => new List<ButtonConfig>
{
    new ButtonConfig 
    { 
        Text = "提交", 
        Width = 60, 
        Height = 26, 
        CommandProperty = nameof(CommitCommand),
        ToolTip = "使用git-svn-commitlog-generator生成提交日志"
    },
    new ButtonConfig 
    { 
        Text = "VS", 
        Width = 54, 
        Height = 26, 
        CommandProperty = nameof(OpenVSCommand),
        ToolTip = "在Visual Studio中打开"
    },
    new ButtonConfig 
    { 
        Text = "Rider", 
        Width = 60, 
        Height = 26, 
        CommandProperty = nameof(OpenRiderCommand),
        ToolTip = "在Rider中打开"
    },
    new ButtonConfig 
    { 
        Text = "Cursor", 
        Width = 64, 
        Height = 26, 
        CommandProperty = nameof(OpenCursorCommand),
        ToolTip = "在Cursor中打开"
    },
    new ButtonConfig 
    { 
        Text = "Claude", 
        Width = 68, 
        Height = 26, 
        CommandProperty = nameof(OpenClaudeCommand),
        ToolTip = "启动Claude Code"
    },
    new ButtonConfig 
    { 
        Text = "Codex", 
        Width = 64, 
        Height = 26, 
        CommandProperty = nameof(OpenCodexCommand),
        ToolTip = "启动Codex"
    },
    new ButtonConfig 
    { 
        Text = "文件夹", 
        Width = 68, 
        Height = 26, 
        CommandProperty = nameof(OpenFolderCommand),
        ToolTip = "在资源管理器中打开"
    }
};

// Command属性（由Page/ViewModel设置）
public ICommand CommitCommand { get; set; }
public ICommand OpenVSCommand { get; set; }
public ICommand OpenRiderCommand { get; set; }
public ICommand OpenCursorCommand { get; set; }
public ICommand OpenClaudeCommand { get; set; }
public ICommand OpenCodexCommand { get; set; }
public ICommand OpenFolderCommand { get; set; }
```

**添加PropertyChanged通知**：
```csharp
public List<string> ProjectFiles
{
    get => _projectFiles;
    set
    {
        if (SetProperty(ref _projectFiles, value ?? new List<string>()))
        {
            OnPropertyChanged(nameof(ProjectFileCount));
        }
    }
}

public DateTime LastUsed
{
    get => _lastUsed;
    set
    {
        if (SetProperty(ref _lastUsed, value))
        {
            OnPropertyChanged(nameof(LastUsedText));
        }
    }
}
```

#### 2.2 CodeWorkspacePage XAML改造

**添加命名空间**：
```xml
xmlns:C="clr-namespace:CustomControlLibrary.CustomControl.Controls.DataGrid;assembly=CustomControlLibrary"
xmlns:button="clr-namespace:CustomControlLibrary.CustomControl.Controls.Button;assembly=CustomControlLibrary"
```

**替换按钮**：
```xml
<button:CButton Content="管理仓库" Width="86" Height="30" Margin="8,0,0,0" Click="ManageRepositoriesButton_Click"/>
<button:CButton Content="刷新项目文件" Width="104" Height="30" Margin="8,0,0,0" Click="RefreshProjectFilesButton_Click"/>
<button:CButton Content="刷新列表" Width="80" Height="30" Margin="8,0,0,0" Click="ReloadButton_Click"/>
```

**替换DataGrid**：
```xml
<C:CDataGrid Grid.Row="1"
             EnableAutoTemplateSelection="True"
             ItemsSource="{Binding Repositories}"
             SelectedItem="{Binding SelectedRepository, Mode=TwoWay}" />
```

#### 2.3 CodeWorkspacePage代码后台改造

**在LoadRepositories后设置Command**：
```csharp
private void LoadRepositories()
{
    Repositories.Clear();
    var settings = _dataPersistenceService.LoadSettings();
    foreach (var repo in (settings.CodeRepositories ?? new List<CodeRepository>())
                 .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path))
                 .OrderByDescending(repo => repo.LastUsed)
                 .ThenBy(repo => repo.Name))
    {
        var cloned = repo.Clone();
        SetupRepositoryCommands(cloned);
        Repositories.Add(cloned);
    }

    StatusText = Repositories.Count == 0
        ? "未配置代码仓库，请点击管理仓库添加。"
        : $"已加载 {Repositories.Count} 个仓库。";
}

private void SetupRepositoryCommands(CodeRepository repo)
{
    repo.CommitCommand = new RelayCommand(() => DoCodeCommit(repo));
    repo.OpenVSCommand = new RelayCommand(() => DoOpenIde(repo, new[] { "Visual Studio", "devenv", "VS" }, "Visual Studio"));
    repo.OpenRiderCommand = new RelayCommand(() => DoOpenIde(repo, new[] { "Rider", "JetBrains Rider" }, "Rider"));
    repo.OpenCursorCommand = new RelayCommand(() => DoOpenCursor(repo));
    repo.OpenClaudeCommand = new RelayCommand(() => DoOpenClaudeCode(repo));
    repo.OpenCodexCommand = new RelayCommand(() => DoOpenCodex(repo));
    repo.OpenFolderCommand = new RelayCommand(() => DoOpenFolder(repo));
}
```

**移除ActionButton_Click方法**（不再需要）

#### 2.4 CodeRepositoryManagementPage改造

**XAML修改**：
- 添加CDataGrid和CButton命名空间
- 替换所有Button为CButton
- 替换DataGrid为CDataGrid（使用手动列定义，因为需要可编辑）

```xml
<C:CDataGrid Grid.Row="1"
             x:Name="RepositoryGrid"
             ItemsSource="{Binding Repositories}"
             SelectedItem="{Binding SelectedRepository, Mode=TwoWay}"
             AutoGenerateColumns="False"
             CanUserAddRows="False"
             IsReadOnly="False">
    <C:CDataGrid.Columns>
        <DataGridTextColumn Header="名称" Binding="{Binding Name, UpdateSourceTrigger=PropertyChanged}" Width="180"/>
        <DataGridTextColumn Header="路径" Binding="{Binding Path, UpdateSourceTrigger=PropertyChanged}" Width="*"/>
        <DataGridTextColumn Header="备注" Binding="{Binding Note, UpdateSourceTrigger=PropertyChanged}" Width="180"/>
        <DataGridTextColumn Header="项目文件" Binding="{Binding ProjectFileCount}" IsReadOnly="True" Width="80"/>
        <DataGridTextColumn Header="最后使用" Binding="{Binding LastUsedText}" IsReadOnly="True" Width="130"/>
        <DataGridTextColumn Header="次数" Binding="{Binding UsageCount}" IsReadOnly="True" Width="60"/>
    </C:CDataGrid.Columns>
</C:CDataGrid>
```

### 3. 优先查找.sln文件（问题3）

**修改RefreshProjectFiles方法**（在CodeWorkspacePage.xaml.cs和CodeRepositoryManagementPage.xaml.cs中）：

```csharp
private void RefreshProjectFiles(CodeRepository repo)
{
    if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
    {
        return;
    }

    try
    {
        var projectFiles = new List<string>();
        
        // 1. 优先查找.sln文件
        var slnFiles = Directory.EnumerateFiles(repo.Path, "*.sln", SearchOption.AllDirectories)
            .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0)
            .Where(path => path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0)
            .Where(path => path.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) < 0)
            .Take(100)
            .ToList();
        
        if (slnFiles.Count > 0)
        {
            // 找到.sln文件，只使用.sln
            projectFiles = slnFiles;
        }
        else
        {
            // 没有.sln文件，查找.csproj
            projectFiles = Directory.EnumerateFiles(repo.Path, "*.csproj", SearchOption.AllDirectories)
                .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(path => path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Take(100)
                .ToList();
        }
        
        repo.ProjectFiles = projectFiles;
    }
    catch (Exception ex)
    {
        LoggingService.LogError(ex, $"刷新仓库项目文件失败：{repo.Path}");
    }
}
```

### 4. VS vs VSCode区分问题（问题4）

**问题根源**：`GetToolPathFromCommonStartup`使用模糊匹配，"VS"会匹配到"VS Code"

**解决方案**：改进匹配逻辑，避免"VS"匹配到"VS Code"

```csharp
private string GetToolPathFromCommonStartup(params string[] possibleNames)
{
    var settings = _dataPersistenceService.LoadSettings();
    var items = settings.CommonStartupItems ?? new List<CommonStartupItem>();
    
    foreach (var name in possibleNames.Where(n => !string.IsNullOrWhiteSpace(n)))
    {
        var item = items.FirstOrDefault(i =>
            !string.IsNullOrWhiteSpace(i?.FullPath) &&
            File.Exists(i.FullPath) &&
            (MatchesToolName(i.Name, name) || MatchesToolPath(i.FullPath, name)));
            
        if (item != null)
        {
            return item.FullPath;
        }
    }

    return null;
}

private static bool MatchesToolName(string itemName, string searchName)
{
    if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(searchName))
    {
        return false;
    }
    
    // 精确匹配
    if (string.Equals(itemName, searchName, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }
    
    // 特殊处理：避免"VS"匹配到"VS Code"
    if (string.Equals(searchName, "VS", StringComparison.OrdinalIgnoreCase))
    {
        // 只匹配"VS"或包含"Visual Studio"但不包含"Code"的名称
        return string.Equals(itemName, "VS", StringComparison.OrdinalIgnoreCase) ||
               (itemName.IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) >= 0 &&
                itemName.IndexOf("Code", StringComparison.OrdinalIgnoreCase) < 0);
    }
    
    // 包含匹配
    return itemName.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0;
}

private static bool MatchesToolPath(string fullPath, string searchName)
{
    if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(searchName))
    {
        return false;
    }
    
    var fileName = Path.GetFileNameWithoutExtension(fullPath);
    
    // 特殊处理：devenv.exe匹配Visual Studio
    if (string.Equals(searchName, "Visual Studio", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(searchName, "devenv", StringComparison.OrdinalIgnoreCase))
    {
        return string.Equals(fileName, "devenv", StringComparison.OrdinalIgnoreCase);
    }
    
    return string.Equals(fileName, searchName, StringComparison.OrdinalIgnoreCase);
}
```

**配置说明**：
- Visual Studio应命名为："Visual Studio 2022"、"Visual Studio"、"VS"或路径包含"devenv.exe"
- VSCode应命名为："Visual Studio Code"、"VSCode"或路径包含"Code.exe"
- 避免使用"VS Code"作为Visual Studio的名称

### 5. Claude按钮打不开问题（问题5）

**改进DoOpenClaudeCode方法**，添加Claude CLI可用性检查和更好的错误提示：

```csharp
private void DoOpenClaudeCode(CodeRepository repo)
{
    // 检查Claude CLI是否可用
    if (!IsClaudeCliAvailable())
    {
        var result = MessageBox.Show(
            "未检测到Claude CLI。\n\n是否打开安装指南？",
            "Claude Code",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
            
        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://docs.anthropic.com/en/docs/claude-code",
                UseShellExecute = true
            });
        }
        return;
    }
    
    // 改进的启动命令
    var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}

# 检查Claude CLI
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {{
    Write-Host 'Claude CLI未找到，请确保已安装并在PATH中。' -ForegroundColor Red
    Write-Host '安装指南: https://docs.anthropic.com/en/docs/claude-code' -ForegroundColor Yellow
    Read-Host '按回车键关闭'
    exit
}}

# 检查条款接受状态
$termsFile = Join-Path $HOME '.claude\.accepted-terms'
if (Test-Path -LiteralPath $termsFile) {{
    Write-Host '启动Claude Code...' -ForegroundColor Green
    claude --dangerously-skip-permissions
}} else {{
    Write-Host '首次运行，自动接受条款...' -ForegroundColor Yellow
    $env:CLAUDE_ACCEPT_TERMS = 'yes'
    echo 'accept' | claude --dangerously-skip-permissions
    
    if ($LASTEXITCODE -ne 0) {{
        Write-Host '自动接受失败，请手动输入 accept 后回车。' -ForegroundColor Yellow
        claude --dangerously-skip-permissions
    }}
}}
";
    
    try
    {
        TerminalHelper.LaunchTerminalWithCommand(command, $"Claude Code - {repo.Name}");
        StatusText = $"已启动 Claude Code：{repo.Name}";
    }
    catch (Exception ex)
    {
        LoggingService.LogError(ex, "启动Claude Code失败");
        MessageBox.Show(
            $"启动Claude Code失败：{ex.Message}\n\n请确保：\n1. 已安装Claude CLI\n2. claude命令在PATH中\n3. PowerShell执行策略允许运行脚本",
            "Claude Code",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

private bool IsClaudeCliAvailable()
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        using (var process = Process.Start(psi))
        {
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
    }
    catch
    {
        return false;
    }
}
```

### 6. Codex打开的不是PowerShell 7（问题7）

**问题分析**：虽然`ResolvePowerShell7Path()`能找到`C:\Program Files\PowerShell\7\pwsh.exe`，但Windows Terminal可能使用默认配置文件（PowerShell 5.1）

**解决方案**：在Windows Terminal命令中使用`--`分隔符，确保后续参数传递给pwsh.exe

**修改TerminalHelper.LaunchTerminalWithCommand**：

```csharp
public static void LaunchTerminalWithCommand(string command, string title)
{
    var psPath = ResolvePowerShell7Path();
    var wtPath = ResolveWindowsTerminalPath();
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command ?? ""));
    var psArgs = $"-NoLogo -NoExit -EncodedCommand {encoded}";

    if (!string.IsNullOrWhiteSpace(wtPath))
    {
        // 使用 -- 确保后续参数传递给pwsh.exe而不是wt.exe
        Process.Start(new ProcessStartInfo
        {
            FileName = wtPath,
            Arguments = $"new-tab --title \"{EscapeArgument(title)}\" -- \"{psPath}\" {psArgs}",
            UseShellExecute = true,
        });
        return;
    }

    // Fallback：直接启动pwsh.exe
    Process.Start(new ProcessStartInfo
    {
        FileName = psPath,
        Arguments = psArgs,
        UseShellExecute = true,
    });
}
```

**关键改动**：在`new-tab --title "..."`后添加`--`，这告诉Windows Terminal后续的所有参数都传递给指定的shell（pwsh.exe），而不是被wt.exe解析。

## 验证步骤

### 1. 代码工作区全屏显示
- 从Dashboard点击"代码工作区"卡片
- 验证在主窗口Frame中全屏显示，而不是弹出独立窗口
- 验证可以通过返回按钮回到Dashboard

### 2. UI组件统一性
- 验证代码工作区使用CButton和CDataGrid
- 验证表格自动生成7个操作按钮列
- 验证按钮样式与其他页面一致
- 验证代码仓库管理页面使用CButton和CDataGrid

### 3. 项目文件扫描
- 在有.sln文件的仓库中，验证只列出.sln文件
- 在没有.sln文件的仓库中，验证列出.csproj文件
- 验证排除bin、obj、.vs目录

### 4. 工具启动
- 验证VS按钮打开Visual Studio（不是VSCode）
- 验证VSCode按钮（如果有）打开VSCode
- 验证Rider、Cursor按钮正常工作

### 5. Claude终端
- 验证Claude按钮能正常打开Claude Code终端
- 如果Claude CLI未安装，验证显示友好的错误提示

### 6. PowerShell 7
- 验证Codex按钮打开的终端是PowerShell 7.5.5
- 验证终端标题栏显示"PowerShell"
- 验证终端提示符显示自定义格式（pwsh、仓库名、分支等）

### 7. 代码仓库管理
- 点击"管理仓库"按钮，验证弹出小窗
- 验证窗口使用CButton和CDataGrid
- 验证可以编辑仓库信息并保存
