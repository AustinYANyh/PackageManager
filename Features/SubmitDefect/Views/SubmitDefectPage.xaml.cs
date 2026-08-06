using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PackageManager.Features.SubmitDefect.Models;
using PackageManager.Features.SubmitDefect.Services;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Features.SubmitDefect.Views
{
    /// <summary>
    /// 提交工作项页面：粘贴群聊内容（文字+图片），程序自动提取文字→描述、图片→示意图、首句→标题，
    /// 一键提交到当前项目（建模组）的进行中迭代。
    /// </summary>
    public partial class SubmitDefectPage : Page, INotifyPropertyChanged
    {
        private const string HtmlDataFormat = "HTML Format";

        private readonly PingCodeApiService api = new PingCodeApiService();
        private readonly PingCodeWorkItemCreatorService creator;
        private readonly ObservableCollection<PastedImage> images = new ObservableCollection<PastedImage>();

        private bool loading;
        private bool isSubmitting;
        private Entity selectedProject;
        private Entity selectedIteration;
        private string selectedTypeName;
        private string descriptionText;
        private string titlePreview;
        private string statusText;

        /// <summary>
        /// 初始化 <see cref="SubmitDefectPage"/> 的新实例。
        /// </summary>
        public SubmitDefectPage()
        {
            InitializeComponent();
            creator = new PingCodeWorkItemCreatorService(api);
            Images = images;
            TypeNames = new ObservableCollection<string> { "缺陷", "故事" };
            selectedTypeName = "缺陷";
            DataContext = this;
        }

        /// <summary>属性变更事件。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>示意图图片列表。</summary>
        public ObservableCollection<PastedImage> Images { get; }

        /// <summary>可选迭代列表（进行中优先，其后未完成）。</summary>
        public ObservableCollection<Entity> Iterations { get; } = new ObservableCollection<Entity>();

        /// <summary>工作项类型可选项。</summary>
        public ObservableCollection<string> TypeNames { get; }

        /// <summary>当前选中的工作项类型名称（缺陷/故事）。</summary>
        public string SelectedTypeName
        {
            get => selectedTypeName;
            set
            {
                if (!Equals(selectedTypeName, value))
                {
                    selectedTypeName = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>当前选中的项目。</summary>
        public Entity SelectedProject
        {
            get => selectedProject;
            set
            {
                if (!Equals(selectedProject, value))
                {
                    selectedProject = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedProjectName));
                    OnPropertyChanged(nameof(CanSubmit));
                }
            }
        }

        /// <summary>当前选中的迭代。</summary>
        public Entity SelectedIteration
        {
            get => selectedIteration;
            set
            {
                if (!Equals(selectedIteration, value))
                {
                    selectedIteration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedIterationName));
                    OnPropertyChanged(nameof(CanSubmit));
                }
            }
        }

        /// <summary>当前项目名（只读展示）。</summary>
        public string SelectedProjectName => selectedProject?.Name;

        /// <summary>当前迭代名（只读展示）。</summary>
        public string SelectedIterationName => selectedIteration?.Name;

        /// <summary>是否可提交（项目与迭代均已就绪）。</summary>
        public bool CanSubmit => (selectedProject != null) && (selectedIteration != null) && !isSubmitting;

        /// <summary>描述文本（双向绑定到粘贴区 TextBox）。</summary>
        public string DescriptionText
        {
            get => descriptionText;
            set
            {
                if (!Equals(descriptionText, value))
                {
                    descriptionText = value;
                    OnPropertyChanged();
                    TitlePreview = ExtractTitle(descriptionText);
                }
            }
        }

        /// <summary>标题预览（首句自动提取，只读）。</summary>
        public string TitlePreview
        {
            get => titlePreview;
            private set
            {
                if (!Equals(titlePreview, value))
                {
                    titlePreview = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>状态文本。</summary>
        public string StatusText
        {
            get => statusText;
            set
            {
                if (!Equals(statusText, value))
                {
                    statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            loading = true;
            Overlay.IsBusy = true;
            try
            {
                StatusText = "加载项目与迭代…";
                var projects = await api.GetProjectsAsync();
                var ordered = projects.OrderBy(x => x.Name ?? x.Id).ToList();
                var project = ordered.FirstOrDefault(x => (x.Name ?? "").Contains("建模组")) ?? ordered.FirstOrDefault();
                SelectedProject = project;
                if (project != null)
                {
                    Iterations.Clear();
                    var ongoing = await api.GetOngoingIterationsByProjectAsync(project.Id);
                    var notCompleted = await api.GetNotCompletedIterationsByProjectAsync(project.Id);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 进行中优先、未开始其次；组内按开始时间升序（早的排前，无开始时间排末尾）
                    var ongoingSorted = (ongoing ?? Enumerable.Empty<Entity>()).OrderBy(x => x?.StartAt ?? long.MaxValue);
                    var notCompletedSorted = (notCompleted ?? Enumerable.Empty<Entity>()).OrderBy(x => x?.StartAt ?? long.MaxValue);
                    foreach (var it in ongoingSorted)
                    {
                        if ((it != null) && !string.IsNullOrWhiteSpace(it.Id) && seen.Add(it.Id))
                        {
                            Iterations.Add(it);
                        }
                    }
                    foreach (var it in notCompletedSorted)
                    {
                        if ((it != null) && !string.IsNullOrWhiteSpace(it.Id) && seen.Add(it.Id))
                        {
                            Iterations.Add(it);
                        }
                    }
                    SelectedIteration = Iterations.FirstOrDefault();
                }

                if (selectedProject == null)
                {
                    StatusText = "未取到项目";
                }
                else if (selectedIteration == null)
                {
                    StatusText = "该项目无可用迭代";
                }
                else
                {
                    StatusText = "就绪：粘贴内容后点提交";
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "提交工作项页加载失败");
                StatusText = "加载失败：" + ex.Message;
            }
            finally
            {
                loading = false;
                Overlay.IsBusy = false;
            }
        }

        private void PasteBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                TitlePreview = ExtractTitle(tb.Text);
            }
        }

        private void PasteBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((e.Key == Key.V) && ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
            {
                if (TryHandleClipboard())
                {
                    e.Handled = true;
                }
            }
        }

        private bool TryHandleClipboard()
        {
            var added = false;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var bytes = EncodeBitmapSource(Clipboard.GetImage());
                    if (bytes != null)
                    {
                        AddImage(PastedImage.FromBytes(bytes, $"clipboard_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png", "image/png"));
                        added = true;
                    }
                }
                else if (Clipboard.ContainsData(HtmlDataFormat))
                {
                    added = ExtractImagesAndTextFromHtml((string)Clipboard.GetData(HtmlDataFormat));
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    foreach (var path in Clipboard.GetFileDropList())
                    {
                        if (IsImageExt(Path.GetExtension(path)))
                        {
                            try
                            {
                                AddImage(PastedImage.FromBytes(File.ReadAllBytes(path), Path.GetFileName(path)));
                                added = true;
                            }
                            catch (Exception ex)
                            {
                                StatusText = "读取图片失败：" + ex.Message;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText = "粘贴处理失败：" + ex.Message;
            }

            return added;
        }

        private bool ExtractImagesAndTextFromHtml(string cfHtml)
        {
            if (string.IsNullOrWhiteSpace(cfHtml))
            {
                return false;
            }

            var added = false;
            var matches = Regex.Matches(cfHtml, @"<img[^>]+src=[""'](?<url>data:[^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                if (TryParseDataUrl(m.Groups["url"].Value, out var mime, out var bytes))
                {
                    AddImage(PastedImage.FromBytes(bytes, $"pasted_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{ExtFromMime(mime)}", mime));
                    added = true;
                }
            }

            var plain = HtmlToPlainText(cfHtml);
            if (!string.IsNullOrWhiteSpace(plain))
            {
                AppendToDescription(plain);
                added = true;
            }

            return added;
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasImageDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (!IsImageExt(Path.GetExtension(path)))
                {
                    continue;
                }

                try
                {
                    AddImage(PastedImage.FromBytes(File.ReadAllBytes(path), Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    StatusText = "读取图片失败：" + ex.Message;
                }
            }
        }

        private void PickImagesButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp"
            };
            if (ofd.ShowDialog() != true)
            {
                return;
            }

            foreach (var file in ofd.FileNames)
            {
                try
                {
                    AddImage(PastedImage.FromBytes(File.ReadAllBytes(file), Path.GetFileName(file)));
                }
                catch (Exception ex)
                {
                    StatusText = "读取图片失败：" + ex.Message;
                }
            }
        }

        private void ClearImagesButton_Click(object sender, RoutedEventArgs e)
        {
            Images.Clear();
            StatusText = "已清空图片";
        }

        private void RemoveImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is PastedImage img)
            {
                Images.Remove(img);
            }
        }

        private async void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            if (isSubmitting)
            {
                return;
            }

            if (selectedProject == null)
            {
                MessageBox.Show("未加载到项目，请稍后重试。", "提示");
                return;
            }

            if (selectedIteration == null)
            {
                MessageBox.Show("该项目无可用迭代。", "提示");
                return;
            }

            if (string.IsNullOrWhiteSpace(descriptionText) && (Images.Count == 0))
            {
                MessageBox.Show("内容为空，请先粘贴内容。", "提示");
                return;
            }

            isSubmitting = true;
            OnPropertyChanged(nameof(CanSubmit));
            Overlay.IsBusy = true;
            try
            {
                var typeName = string.IsNullOrWhiteSpace(selectedTypeName) ? "缺陷" : selectedTypeName;
                var options = new SubmitDefectOptions
                {
                    ProjectId = selectedProject.Id,
                    IterationId = selectedIteration.Id,
                    WorkItemType = typeName,
                    Title = ExtractTitle(descriptionText),
                    DescriptionHtml = PlainToHtml(descriptionText),
                    Images = Images.ToList(),
                };

                var result = await creator.CreateAsync(options, new Progress<string>(m => StatusText = m));
                if (result.Success)
                {
                    var msg = "已创建 " + result.Identifier;
                    if ((Images.Count > 0) && !result.ShiyituWritten)
                    {
                        msg += "\n（示意图字段未写入，图片已作为附件关联到工作项）";
                    }

                    var problems = result.Steps.Where(s => s.Contains("失败") || s.Contains("异常") || s.Contains("未取到")).ToList();
                    if (problems.Count > 0)
                    {
                        msg += "\n\n明细：\n" + string.Join("\n", problems);
                    }

                    MessageBox.Show(msg, "提交完成", MessageBoxButton.OK, MessageBoxImage.Information);

                    if (!string.IsNullOrWhiteSpace(result.HtmlUrl))
                    {
                        OpenUrl(result.HtmlUrl);
                    }

                    DescriptionText = "";
                    Images.Clear();
                    StatusText = "已创建 " + result.Identifier + "，可继续提交下一条";
                }
                else
                {
                    MessageBox.Show("提交失败：\n" + string.Join("\n", result.Steps), "提交失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText = "提交失败";
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "提交工作项失败");
                MessageBox.Show("提交异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "提交异常";
            }
            finally
            {
                isSubmitting = false;
                OnPropertyChanged(nameof(CanSubmit));
                Overlay.IsBusy = false;
            }
        }

        private void AddImage(PastedImage img)
        {
            if (img == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(img.Hash))
            {
                foreach (var existing in Images)
                {
                    if (!string.IsNullOrWhiteSpace(existing.Hash) && string.Equals(existing.Hash, img.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = "图片已存在，已跳过：" + img.FileName;
                        return;
                    }
                }
            }

            Images.Add(img);
            StatusText = "已添加 " + Images.Count + " 张图片";
        }

        private void AppendToDescription(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var current = descriptionText ?? string.Empty;
            DescriptionText = string.IsNullOrEmpty(current) ? text : (current + "\n" + text);
        }

        private static string ExtractTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text.Replace("\r", string.Empty).Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length < 8)
                {
                    continue;
                }

                if (IsMetaDataLine(line))
                {
                    continue;
                }

                var sentence = TakeFirstSentence(line).Trim();
                if (sentence.Length < 8)
                {
                    continue;
                }

                return sentence.Length > 25 ? (sentence.Substring(0, 25) + "…") : sentence;
            }

            var flat = Regex.Replace(text.Trim(), @"\s+", " ");
            return flat.Length > 25 ? (flat.Substring(0, 25) + "…") : flat;
        }

        private static bool IsMetaDataLine(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return true;
            }

            if (line.StartsWith("@", StringComparison.Ordinal)
                || line.StartsWith("工作中", StringComparison.Ordinal)
                || line.StartsWith("👍", StringComparison.Ordinal)
                || line.StartsWith("回复", StringComparison.Ordinal))
            {
                return true;
            }

            if (Regex.IsMatch(line, @"^\d{1,2}:\d{2}") || Regex.IsMatch(line, @"^\d{11}$"))
            {
                return true;
            }

            return false;
        }

        private static string TakeFirstSentence(string line)
        {
            var idx = line.IndexOfAny(new[] { '。', '！', '？', '；', '?', '!', ';' });
            var sentence = idx >= 0 ? line.Substring(0, idx) : line;
            if (sentence.Length > 25)
            {
                var comma = sentence.IndexOfAny(new[] { '，', ',' });
                if (comma >= 8 && comma <= 25)
                {
                    sentence = sentence.Substring(0, comma);
                }
            }

            return sentence;
        }

        private static string PlainToHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "<p>-</p>";
            }

            var esc = System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br/>");
            return "<p>" + esc + "</p>";
        }

        private static string HtmlToPlainText(string html)
        {
            try
            {
                var start = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    html = html.Substring(start);
                }

                html = Regex.Replace(html, @"(?is)<script.*?</script>", " ");
                html = Regex.Replace(html, @"(?is)<style.*?</style>", " ");
                html = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");
                html = Regex.Replace(html, @"(?i)</p>", "\n");
                html = Regex.Replace(html, @"(?i)</div>", "\n");
                html = Regex.Replace(html, @"<[^>]+>", " ");
                html = System.Net.WebUtility.HtmlDecode(html).Replace(" ", " ");
                var lines = html.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
                return string.Join("\n", lines);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryParseDataUrl(string dataUrl, out string mime, out byte[] bytes)
        {
            mime = null;
            bytes = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var comma = dataUrl.IndexOf(',');
                if (comma < 0)
                {
                    return false;
                }

                var header = dataUrl.Substring(5, comma - 5);
                var semi = header.IndexOf(';');
                mime = semi >= 0 ? header.Substring(0, semi) : header;
                bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
                return (bytes != null) && (bytes.Length > 0);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] EncodeBitmapSource(BitmapSource source)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                using (var ms = new System.IO.MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(source));
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool HasImageDrop(IDataObject data)
        {
            if ((data == null) || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return false;
            }

            try
            {
                return ((string[])data.GetData(DataFormats.FileDrop)).Any(p => IsImageExt(Path.GetExtension(p)));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsImageExt(string ext)
        {
            ext = (ext ?? string.Empty).ToLowerInvariant();
            return (ext == ".png") || (ext == ".jpg") || (ext == ".jpeg") || (ext == ".gif") || (ext == ".bmp") || (ext == ".webp") || (ext == ".svg");
        }

        private static string ExtFromMime(string mime)
        {
            switch ((mime ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg": return "jpg";
                case "image/gif": return "gif";
                case "image/bmp": return "bmp";
                case "image/webp": return "webp";
                case "image/svg+xml": return "svg";
                default: return "png";
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        /// <summary>触发 <see cref="PropertyChanged"/> 事件。</summary>
        /// <param name="propertyName">属性名，默认为调用方成员名。</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
