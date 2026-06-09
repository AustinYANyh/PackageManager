using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Features.CodeWorkspace.Services;
using PackageManager.Features.PingCode.Models;
using PackageManager.Features.PingCode.Services;
using PackageManager.Services;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

public partial class PingCodeAiExecutionWindow : Window, INotifyPropertyChanged
{
    private readonly PingCodeAiPromptRequest request;
    private readonly WorkItemDetails workItemDetails;
    private readonly DataPersistenceService dataPersistenceService;
    private readonly AiCliLaunchService aiCliLaunchService;
    private readonly PingCodeImageDownloadService imageDownloadService;
    private readonly IntranetPageDownloadService intranetPageDownloadService;
    private readonly string initialPrompt;
    private readonly string accessToken;
    private readonly string tempImageDirectory;
    private readonly string tempIntranetDirectory;
    private CodeRepository selectedRepository;
    private string promptText;
    private string statusText;

    public PingCodeAiExecutionWindow(PingCodeAiPromptRequest request, WorkItemDetails details, string accessToken)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        this.workItemDetails = details;
        this.accessToken = accessToken;
        dataPersistenceService = ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
        aiCliLaunchService = ServiceLocator.Resolve<AiCliLaunchService>() ?? new AiCliLaunchService();
        imageDownloadService = new PingCodeImageDownloadService();
        intranetPageDownloadService = new IntranetPageDownloadService();
        initialPrompt = request.InitialPrompt ?? string.Empty;
        promptText = initialPrompt;
        tempImageDirectory = Path.Combine(Path.GetTempPath(), $"pm-ai-images-{request.WorkItemId ?? "unknown"}");
        tempIntranetDirectory = Path.Combine(Path.GetTempPath(), $"pm-ai-intranet-{request.WorkItemId ?? "unknown"}");
        InitializeComponent();
        DataContext = this;
        LoadRepositories();
        Loaded += async (_, __) => await DownloadAssetsAsync();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<CodeRepository> Repositories { get; } = new ObservableCollection<CodeRepository>();

    public ObservableCollection<PingCodePromptLink> Links { get; } = new ObservableCollection<PingCodePromptLink>();

    public ObservableCollection<DownloadedImage> DownloadedImages { get; } = new ObservableCollection<DownloadedImage>();

    public ObservableCollection<DownloadedIntranetResource> DownloadedIntranetResources { get; } = new ObservableCollection<DownloadedIntranetResource>();

    public string HeaderText => $"PingCode AI {request.ActionKind}: {request.Title}";

    public string SubHeaderText => $"{request.Identifier} · {request.WorkItemType} · 请确认仓库并编辑 Prompt 后再启动 CLI。";

    public CodeRepository SelectedRepository
    {
        get => selectedRepository;
        set => SetProperty(ref selectedRepository, value);
    }

    public string PromptText
    {
        get => promptText;
        set => SetProperty(ref promptText, value);
    }

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    private void LoadRepositories()
    {
        Repositories.Clear();
        Links.Clear();
        foreach (var link in request.Links ?? Enumerable.Empty<PingCodePromptLink>())
        {
            Links.Add(link);
        }

        var settings = dataPersistenceService.LoadSettings();
        var repositories = (settings.CodeRepositories ?? new System.Collections.Generic.List<CodeRepository>())
            .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path) && Directory.Exists(repo.Path))
            .OrderByDescending(repo => repo.LastUsed)
            .ThenByDescending(repo => repo.UsageCount)
            .ThenBy(repo => repo.Name)
            .Select(repo => repo.Clone())
            .ToList();

        foreach (var repository in repositories)
        {
            Repositories.Add(repository);
        }

        SelectedRepository = Repositories.FirstOrDefault();
        StatusText = Repositories.Count == 0
            ? "未配置可用代码仓库，请先在代码工作区添加仓库。"
            : $"已加载 {Repositories.Count} 个代码仓库。";
    }

    private void RestorePrompt_Click(object sender, RoutedEventArgs e)
    {
        PromptText = initialPrompt;
        StatusText = "已恢复初始 Prompt。";
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PromptText ?? string.Empty);
        StatusText = "已复制 Prompt。";
    }

    private async void ClaudeExecute_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(true);
    }

    private async void CodexExecute_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task DownloadAssetsAsync()
    {
        try
        {
            StatusText = "正在提取工作项图片...";
            if (Directory.Exists(tempImageDirectory))
            {
                Directory.Delete(tempImageDirectory, true);
            }

            if (Directory.Exists(tempIntranetDirectory))
            {
                Directory.Delete(tempIntranetDirectory, true);
            }

            var images = await imageDownloadService.DownloadImagesAsync(workItemDetails, accessToken, tempImageDirectory);
            foreach (var img in images)
            {
                DownloadedImages.Add(img);
            }

            StatusText = "正在下载内网页面资源...";
            var intranetResources = await intranetPageDownloadService.DownloadPagesAsync(request.Links, tempIntranetDirectory);
            foreach (var resource in intranetResources)
            {
                DownloadedIntranetResources.Add(resource);
            }

            var imgSuccess = images.Count(x => x.Success);
            var intranetSuccess = intranetResources.Count(x => x.Success);
            StatusText = $"已提取：工作项图片 {imgSuccess}/{images.Count}，内网页面资源 {intranetSuccess}/{intranetResources.Count}。";
        }
        catch (Exception ex)
        {
            StatusText = $"资源提取失败：{ex.Message}";
        }
    }

    private async Task ExecuteAsync(bool useClaude)
    {
        if (SelectedRepository == null)
        {
            MessageBox.Show("请先选择目标代码仓库。", "PingCode AI 执行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptText))
        {
            MessageBox.Show("Prompt 不能为空。", "PingCode AI 执行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            StatusText = useClaude ? "正在启动 Claude..." : "正在启动 Codex...";
            var finalPrompt = PromptText;
            var imageSection = CopyImagesToRepoAndBuildPromptSection(SelectedRepository.Path);
            if (!string.IsNullOrWhiteSpace(imageSection))
            {
                finalPrompt = finalPrompt + "\n\n" + imageSection;
            }

            var intranetSection = CopyIntranetResourcesToRepoAndBuildPromptSection(SelectedRepository.Path);
            if (!string.IsNullOrWhiteSpace(intranetSection))
            {
                finalPrompt = finalPrompt + "\n\n" + intranetSection;
            }

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                finalPrompt = finalPrompt + "\n\n" + BuildPingCodeTokenSection();
            }

            if (request.ActionKind == "拆解" && !string.IsNullOrWhiteSpace(accessToken))
            {
                finalPrompt = finalPrompt + "\n\n" + BuildPingCodeApiAuthSection();
            }

            if (useClaude)
            {
                await aiCliLaunchService.LaunchClaudeAsync(SelectedRepository, finalPrompt, $"Claude PingCode {request.ActionKind} - {SelectedRepository.Name}");
            }
            else
            {
                await aiCliLaunchService.LaunchCodexAsync(SelectedRepository, finalPrompt, $"Codex PingCode {request.ActionKind} - {SelectedRepository.Name}");
            }

            UpdateRepositoryUsage(SelectedRepository);
            StatusText = "已启动 CLI。";
            Close();
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
            MessageBox.Show($"启动失败：{ex.Message}", "PingCode AI 执行", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string CopyImagesToRepoAndBuildPromptSection(string repoPath)
    {
        var successImages = DownloadedImages.Where(x => x.Success).ToList();
        if (successImages.Count == 0)
        {
            return null;
        }

        var targetDir = GetWorkItemImageDirectory(repoPath);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }

        Directory.CreateDirectory(targetDir);
        var sb = new StringBuilder();
        sb.AppendLine("## 工作项图片（已下载到本地）");
        sb.AppendLine("以下图片已保存到仓库本地，请直接读取这些文件来理解截图、示意图等视觉信息。");
        var copiedCount = 0;
        foreach (var img in successImages)
        {
            var destPath = Path.Combine(targetDir, img.FileName);
            try
            {
                File.Copy(img.LocalPath, destPath, true);
                copiedCount++;
                sb.AppendLine($"- {ToRepoRelativePath(repoPath, destPath)}（来源：{img.SourceContext}）");
            }
            catch
            {
            }
        }

        return copiedCount == 0 ? null : sb.ToString();
    }

    private string CopyIntranetResourcesToRepoAndBuildPromptSection(string repoPath)
    {
        var resources = DownloadedIntranetResources.Where(x => x.Success).ToList();
        var failedResources = DownloadedIntranetResources.Where(x => !x.Success).ToList();
        if (resources.Count == 0 && failedResources.Count == 0)
        {
            return null;
        }

        var targetDir = GetIntranetResourceDirectory(repoPath);
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }

        Directory.CreateDirectory(targetDir);
        var sb = new StringBuilder();
        sb.AppendLine("## 内网网页资料（已下载到本地）");
        sb.AppendLine("工作项中的内网 Axure/网页内容已尽量下载到仓库本地。很多 Axure 页面正文不是 DOM 文本，而是一张页面截图图片；多模态模型请直接读取下面列出的图片文件来理解需求。");
        var copiedImages = 0;
        var copiedHtml = 0;
        foreach (var resource in resources)
        {
            var destPath = Path.Combine(targetDir, resource.FileName);
            try
            {
                File.Copy(resource.LocalPath, destPath, true);
                var relativePath = ToRepoRelativePath(repoPath, destPath);
                if (resource.IsImage)
                {
                    copiedImages++;
                    sb.AppendLine($"- [页面图片] {relativePath}（标题：{resource.Title ?? "未命名"}；来源：{resource.SourceContext}；原始地址：{resource.OriginalUrl}）");
                }
                else
                {
                    copiedHtml++;
                    sb.AppendLine($"- [页面HTML] {relativePath}（标题：{resource.Title ?? "未命名"}；来源：{resource.SourceContext}；原始地址：{resource.OriginalUrl}）");
                }
            }
            catch
            {
            }
        }

        if (failedResources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下内网页面资源下载失败，不要把它们误判为没有需求数据：");
            foreach (var resource in failedResources)
            {
                sb.AppendLine($"- {resource.OriginalUrl}（来源：{resource.SourceContext}；错误：{resource.Error}）");
            }
        }

        return copiedImages == 0 && copiedHtml == 0 && failedResources.Count == 0 ? null : sb.ToString();
    }

    private string GetWorkItemImageDirectory(string repoPath)
    {
        return Path.Combine(repoPath, ".pm-ai", "work-items", GetSafeWorkItemKey(), "images");
    }

    private string GetIntranetResourceDirectory(string repoPath)
    {
        return Path.Combine(repoPath, ".pm-ai", "work-items", GetSafeWorkItemKey(), "intranet-pages");
    }

    private string GetSafeWorkItemKey()
    {
        var key = request.Identifier;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = request.WorkItemId;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            key = "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = key.Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string ToRepoRelativePath(string repoPath, string fullPath)
    {
        var relative = fullPath;
        if (!string.IsNullOrWhiteSpace(repoPath) &&
            fullPath.StartsWith(repoPath, StringComparison.OrdinalIgnoreCase))
        {
            relative = fullPath.Substring(repoPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private string BuildPingCodeTokenSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## PingCode 链接访问凭证");
        sb.AppendLine("工作项中的 PingCode 链接（*.pingcode.com）需要认证才能访问。");
        sb.AppendLine("访问时请在 URL 后追加查询参数 access_token，示例：");
        sb.AppendLine($"  原始链接?access_token={accessToken}");
        sb.AppendLine($"  或 原始链接&access_token={accessToken}（如果 URL 已有 ? 参数）");
        sb.AppendLine("此 token 有时效性，请尽快使用。非 PingCode 域名的链接无需添加此参数。");
        return sb.ToString();
    }

    private string BuildPingCodeApiAuthSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## PingCode API 认证凭证");
        sb.AppendLine("调用 PingCode REST API（创建工作项、添加评论等）时，使用以下 Bearer Token：");
        sb.AppendLine($"  Authorization: Bearer {accessToken}");
        sb.AppendLine("在 PowerShell 中使用示例：");
        sb.AppendLine($"  $headers = @{{ 'Content-Type' = 'application/json'; 'Authorization' = 'Bearer {accessToken}' }}");
        sb.AppendLine("此 token 有时效性，请尽快使用。如果 API 返回 401，说明 token 已过期。");
        return sb.ToString();
    }

    private void UpdateRepositoryUsage(CodeRepository selected)
    {
        var settings = dataPersistenceService.LoadSettings();
        var repo = settings.CodeRepositories?.FirstOrDefault(x => string.Equals(x?.Path, selected.Path, StringComparison.OrdinalIgnoreCase));
        if (repo == null)
        {
            return;
        }

        repo.LastUsed = DateTime.Now;
        repo.UsageCount++;
        settings.LastUsedRepositoryPath = repo.Path;
        dataPersistenceService.SaveSettings(settings);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
