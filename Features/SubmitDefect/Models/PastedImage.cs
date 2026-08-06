using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace PackageManager.Features.SubmitDefect.Models
{
    /// <summary>
    /// 粘贴/拖拽进来的单张图片模型，承载缩略图、原始字节、上传状态与上传后的公开地址。
    /// </summary>
    public class PastedImage : INotifyPropertyChanged
    {
        private UploadStatus uploadStatus = UploadStatus.Pending;
        private string publicUrl;
        private string error;

        /// <summary>
        /// 属性变更事件。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 原始图片字节（用于上传）。
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// 文件名（含扩展名）。
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// MIME 类型，如 image/png。
        /// </summary>
        public string ContentType { get; set; }

        /// <summary>
        /// 缩略图（解码宽度限制为 200，降低内存占用；已 Freeze 可跨线程使用）。
        /// </summary>
        public BitmapImage Thumbnail { get; set; }

        /// <summary>
        /// 内容 SHA256 哈希（十六进制），用于去重。
        /// </summary>
        public string Hash { get; set; }

        /// <summary>
        /// 上传到 PingCode 后的公开图片地址。
        /// </summary>
        public string PublicUrl
        {
            get => publicUrl;
            set
            {
                if (!Equals(publicUrl, value))
                {
                    publicUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 上传状态。
        /// </summary>
        public UploadStatus UploadStatus
        {
            get => uploadStatus;
            set
            {
                if (uploadStatus != value)
                {
                    uploadStatus = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>
        /// 状态文本（用于 UI 缩略图角标展示）。
        /// </summary>
        public string StatusText
        {
            get
            {
                switch (uploadStatus)
                {
                    case UploadStatus.Pending: return "待上传";
                    case UploadStatus.Uploading: return "上传中…";
                    case UploadStatus.Done: return "✓ 已上传";
                    case UploadStatus.Failed: return string.IsNullOrWhiteSpace(error) ? "✕ 失败" : ("✕ " + error);
                    default: return null;
                }
            }
        }

        /// <summary>
        /// 上传失败时的错误描述。
        /// </summary>
        public string Error
        {
            get => error;
            set
            {
                if (!Equals(error, value))
                {
                    error = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>
        /// 从图片字节构造模型，生成缩略图与哈希。
        /// </summary>
        /// <param name="data">图片字节。</param>
        /// <param name="fileName">文件名；为空时按时间戳生成 png 名。</param>
        /// <param name="contentType">MIME 类型；为空时按扩展名推测。</param>
        /// <returns>构造好的实例；字节为空返回 null。</returns>
        public static PastedImage FromBytes(byte[] data, string fileName, string contentType = null)
        {
            if ((data == null) || (data.Length == 0))
            {
                return null;
            }

            var img = new PastedImage
            {
                Data = data,
                FileName = string.IsNullOrWhiteSpace(fileName)
                    ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png"
                    : fileName,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? GuessContentType(fileName) : contentType,
            };

            img.Hash = ComputeHash(data);

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 200;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                img.Thumbnail = bmp;
            }
            catch
            {
                img.Thumbnail = null;
            }

            return img;
        }

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <param name="propertyName">属性名，默认为调用方成员名。</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string ComputeHash(byte[] data)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(data);
                    var sb = new StringBuilder(bytes.Length * 2);
                    foreach (var b in bytes)
                    {
                        sb.Append(b.ToString("x2"));
                    }

                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GuessContentType(string fileName)
        {
            var ext = (Path.GetExtension(fileName ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                default: return "image/png";
            }
        }
    }

    /// <summary>
    /// 图片上传状态。
    /// </summary>
    public enum UploadStatus
    {
        /// <summary>待上传。</summary>
        Pending,

        /// <summary>上传中。</summary>
        Uploading,

        /// <summary>上传成功。</summary>
        Done,

        /// <summary>上传失败。</summary>
        Failed,
    }
}
