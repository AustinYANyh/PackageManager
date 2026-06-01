# 仪表盘代码提交功能设计方案

## Context

用户创建了一个 `git_svn_commitlog_generator` skill，用于自动采集 Git/SVN 改动、生成提交日志并推送。该 skill 运行良好，但需要手动进入 Claude Code CLI 调用，用户觉得繁琐。

用户希望在 PackageManager 仪表盘中添加"代码提交"按钮，配合代码根目录设置功能，点击后自动执行该 skill，实现更便捷的代码提交流程。

## 技术背景

### 当前 Skill 工作流程
1. **采集改动**：调用 `get-working-changes.ps1` 扫描 Git/SVN 状态，输出 JSON
2. **生成日志**：Claude 读取 JSON 和 diff，生成符合规范的提交日志
3. **提交推送**：调用 `invoke-commit-push-interactive.ps1` 执行 git/svn commit + push

### 项目架构
- **技术栈**：WPF + MVVM
- **导航**：NavigationService + ToolRegistry
- **配置**：DataPersistenceService 持久化
- **进程调用**：成熟的异步执行 + 实时日志监控模式（参考 EmbeddedToolRunnerService）

## 方案对比

### 方案 A：完全自动化（推荐）
**实现方式**：
- 在 C# 中调用 PowerShell 脚本采集改动
- 调用 Claude API 生成提交日志
- 调用 PowerShell 脚本执行提交推送
- 在应用内显示进度和日志

**优点**：
- 用户体验最佳，无需离开应用
- 可以显示实时进度和日志
- 完全自动化，一键完成

**缺点**：
- 需要配置 Claude API key
- 需要实现 API 调用逻辑
- 开发工作量较大

### 方案 B：半自动化
**实现方式**：
- 点击按钮后启动 Claude Code CLI
- 自动发送命令执行 skill
- 在终端窗口中显示过程

**优点**：
- 不需要 API key
- 复用现有 skill 逻辑
- 开发工作量中等

**缺点**：
- 需要打开终端窗口
- Claude Code CLI 的程序化调用可能不稳定
- 用户体验一般

### 方案 C：快捷启动
**实现方式**：
- 点击按钮打开 PowerShell 终端
- 自动 cd 到项目目录
- 用户手动输入 skill 命令

**优点**：
- 实现最简单
- 不需要额外配置
- 开发工作量最小

**缺点**：
- 仍需手动操作
- 用户体验改善有限

## 推荐方案：方案 A（完全自动化）

基于用户"尽量让我容易使用"的需求，推荐方案 A。

## 实现计划

### 1. 配置管理

#### 1.1 扩展 AppSettings
**文件**：`E:\PackageManager\Models\AppSettings.cs`

添加字段：
```csharp
public class AppSettings
{
    // 现有字段...
    
    // 代码提交相关配置
    public List<CodeRepository> CodeRepositories { get; set; } = new List<CodeRepository>();
    public string LastUsedRepositoryPath { get; set; }
    public string ClaudeApiKey { get; set; }
    public string ClaudeApiModel { get; set; } = "claude-sonnet-4-6";
}

public class CodeRepository
{
    public string Name { get; set; }
    public string Path { get; set; }
    public DateTime LastUsed { get; set; }
}
```

#### 1.2 设置页面添加配置项
**文件**：`E:\PackageManager\Features\Settings\Views\SettingsPage.xaml`

添加新的配置区域：
- Claude API Key 输入框（PasswordBox）
- 代码仓库列表管理（添加/删除/编辑）

### 2. 代码提交服务

#### 2.1 创建 GitSvnCommitService
**新文件**：`E:\PackageManager\Features\CodeCommit\Services\GitSvnCommitService.cs`

核心方法：
```csharp
public class GitSvnCommitService
{
    // 1. 采集改动
    public async Task<WorkingChanges> CollectChangesAsync(string repoPath, IProgress<string> progress);
    
    // 2. 生成提交日志（调用 Claude API）
    public async Task<string> GenerateCommitMessageAsync(WorkingChanges changes, IProgress<string> progress);
    
    // 3. 执行提交推送
    public async Task<CommitResult> CommitAndPushAsync(string repoPath, string commitMessage, IProgress<string> progress);
    
    // 4. 完整流程
    public async Task<CommitResult> ExecuteFullWorkflowAsync(string repoPath, IProgress<string> progress);
}
```

实现细节：
- 使用 `Process.Start` 调用 PowerShell 脚本
- 解析 JSON 输出
- 调用 Claude API（使用 Anthropic SDK 或 HttpClient）
- 实时报告进度

#### 2.2 Claude API 集成
**新文件**：`E:\PackageManager\Features\CodeCommit\Services\ClaudeApiClient.cs`

```csharp
public class ClaudeApiClient
{
    public async Task<string> GenerateCommitMessageAsync(
        string changesJson, 
        Dictionary<string, string> diffs,
        CancellationToken cancellationToken);
}
```

使用 Anthropic SDK 或直接 HTTP 调用：
- Endpoint: `https://api.anthropic.com/v1/messages`
- Model: `claude-sonnet-4-6`
- 构造 prompt 包含改动信息和 diff
- 解析返回的提交日志

### 3. UI 实现

#### 3.1 仪表盘添加卡片
**文件**：`E:\PackageManager\Features\Dashboard\Views\DashboardPage.xaml`

添加"代码提交"卡片：
```xml
<Border Style="{StaticResource DashboardCard}" 
        Tag="tool:code-commit" 
        MouseLeftButtonUp="ToolCard_Click">
    <StackPanel>
        <TextBlock Text="&#xE8E5;" FontFamily="Segoe MDL2 Assets" FontSize="22" Foreground="#10B981"/>
        <controls:CTextBlock Text="代码提交" FontSize="14" FontWeight="SemiBold"/>
        <controls:CTextBlock Text="自动生成日志并提交推送" Foreground="#6B6B6B" FontSize="12"/>
    </StackPanel>
</Border>
```

#### 3.2 创建代码提交窗口
**新文件**：`E:\PackageManager\Features\CodeCommit\Views\CodeCommitWindow.xaml`

窗口布局：
- 顶部：项目选择下拉框 + 刷新按钮
- 中部：进度条 + 状态文本 + 日志滚动区域
- 底部：开始提交按钮 + 取消按钮

**新文件**：`E:\PackageManager\Features\CodeCommit\Views\CodeCommitWindow.xaml.cs`

```csharp
public partial class CodeCommitWindow : Window
{
    private readonly GitSvnCommitService commitService;
    private CancellationTokenSource cts;
    
    private async void StartCommitButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedRepo = RepositoryComboBox.SelectedItem as CodeRepository;
        if (selectedRepo == null) return;
        
        cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => AppendLog(msg));
        
        try
        {
            IsEnabled = false;
            ProgressBar.IsIndeterminate = true;
            
            var result = await commitService.ExecuteFullWorkflowAsync(
                selectedRepo.Path, 
                progress);
            
            if (result.Success)
            {
                MessageBox.Show("提交成功！", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                MessageBox.Show($"提交失败：{result.ErrorMessage}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "代码提交失败");
            MessageBox.Show($"发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            ProgressBar.IsIndeterminate = false;
        }
    }
}
```

#### 3.3 启动器集成
**文件**：`E:\PackageManager\Features\DevTools\DevToolLauncher.cs`

添加方法：
```csharp
public static void OpenCodeCommit()
{
    var settings = ServiceLocator.Resolve<DataPersistenceService>().LoadSettings();
    
    // 检查是否配置了 API key
    if (string.IsNullOrWhiteSpace(settings?.ClaudeApiKey))
    {
        var result = MessageBox.Show(
            "需要配置 Claude API Key 才能使用此功能。是否前往设置？",
            "配置缺失",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            var navService = ServiceLocator.Resolve<NavigationService>();
            navService.NavigateTo("settings");
        }
        return;
    }
    
    // 检查是否配置了代码仓库
    if (settings.CodeRepositories == null || settings.CodeRepositories.Count == 0)
    {
        var result = MessageBox.Show(
            "需要先配置代码仓库路径。是否前往设置？",
            "配置缺失",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            var navService = ServiceLocator.Resolve<NavigationService>();
            navService.NavigateTo("settings");
        }
        return;
    }
    
    var window = new CodeCommitWindow();
    window.ShowDialog();
}
```

#### 3.4 DashboardPage 事件处理
**文件**：`E:\PackageManager\Features\Dashboard\Views\DashboardPage.xaml.cs`

在 `ToolCard_Click` 方法中添加：
```csharp
case "tool:code-commit":
    DevToolLauncher.OpenCodeCommit();
    break;
```

### 4. PowerShell 脚本适配

#### 4.1 非交互模式调用
确保脚本支持非交互模式：
- `get-working-changes.ps1 -NonInteractive`
- `invoke-commit-push-interactive.ps1 -WindowStyle Hidden -PromptTimeoutSeconds 0`

#### 4.2 输出格式标准化
确保 JSON 输出格式一致，便于 C# 解析。

### 5. 依赖项

#### 5.1 NuGet 包
- `Anthropic.SDK`（如果使用官方 SDK）
- 或使用 `System.Net.Http` 直接调用 API

#### 5.2 脚本路径
- 脚本位于：`E:\PackageManager\.claude\skills\git_svn_commitlog_generator\scripts\`
- 需要确保应用能访问这些脚本

### 6. 错误处理

- API key 无效：提示用户检查配置
- 网络错误：显示友好错误信息，支持重试
- 脚本执行失败：显示详细错误日志
- Git/SVN 冲突：提示用户手动解决

### 7. 用户体验优化

- 记住上次使用的仓库
- 显示实时日志输出
- 支持取消操作
- 提交成功后显示提交哈希/版本号
- 支持查看生成的提交日志并编辑

## 关键文件清单

### 新增文件
- `E:\PackageManager\Features\CodeCommit\Services\GitSvnCommitService.cs`
- `E:\PackageManager\Features\CodeCommit\Services\ClaudeApiClient.cs`
- `E:\PackageManager\Features\CodeCommit\Views\CodeCommitWindow.xaml`
- `E:\PackageManager\Features\CodeCommit\Views\CodeCommitWindow.xaml.cs`
- `E:\PackageManager\Features\CodeCommit\Models\WorkingChanges.cs`
- `E:\PackageManager\Features\CodeCommit\Models\CommitResult.cs`

### 修改文件
- `E:\PackageManager\Models\AppSettings.cs` - 添加配置字段
- `E:\PackageManager\Features\Settings\Views\SettingsPage.xaml` - 添加配置 UI
- `E:\PackageManager\Features\Settings\Views\SettingsPage.xaml.cs` - 添加配置逻辑
- `E:\PackageManager\Features\Dashboard\Views\DashboardPage.xaml` - 添加卡片
- `E:\PackageManager\Features\Dashboard\Views\DashboardPage.xaml.cs` - 添加事件处理
- `E:\PackageManager\Features\DevTools\DevToolLauncher.cs` - 添加启动方法

## 验证计划

1. **配置验证**：
   - 在设置页面添加 Claude API key
   - 添加至少一个代码仓库路径
   - 保存并重启应用，确认配置持久化

2. **功能验证**：
   - 点击仪表盘"代码提交"卡片
   - 选择代码仓库
   - 点击"开始提交"
   - 观察进度和日志输出
   - 确认提交成功并推送到远程

3. **错误处理验证**：
   - 测试无 API key 场景
   - 测试无代码仓库配置场景
   - 测试网络错误场景
   - 测试 Git/SVN 冲突场景

4. **边界情况**：
   - 测试多个 Git 仓库嵌套场景
   - 测试 SVN 工作副本场景
   - 测试无改动场景
   - 测试大量文件改动场景

## 备选方案

如果完全自动化方案实现复杂度过高，可以降级为**方案 B（半自动化）**：
- 点击按钮后启动 Windows Terminal
- 自动执行 `claude code` 进入 CLI
- 自动发送 skill 调用命令
- 用户在终端中查看和确认

实现更简单，但用户体验稍差。
