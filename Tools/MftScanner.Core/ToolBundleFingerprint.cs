using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MftScanner
{
    public static class ToolBundleFingerprint
    {
        public sealed class Component
        {
            public Component(string name, Stream stream)
            {
                Name = name ?? string.Empty;
                Stream = stream;
            }

            public string Name { get; }
            public Stream Stream { get; }
        }

        public static string ComputeFromFiles(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return string.Empty;
            }

            var components = new List<Component>();
            try
            {
                foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path))
                    {
                        return string.Empty;
                    }

                    components.Add(new Component(Path.GetFileName(path), new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)));
                }

                return ComputeFromStreams(components);
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                foreach (var component in components)
                {
                    try
                    {
                        component.Stream?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static string ComputeFromStreams(IEnumerable<Component> components)
        {
            if (components == null)
            {
                return string.Empty;
            }

            try
            {
                using (var output = new MemoryStream())
                {
                    foreach (var component in components.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (component?.Stream == null)
                        {
                            return string.Empty;
                        }

                        var nameBytes = Encoding.UTF8.GetBytes((component.Name ?? string.Empty).ToLowerInvariant());
                        var nameLength = BitConverter.GetBytes(nameBytes.Length);
                        output.Write(nameLength, 0, nameLength.Length);
                        output.Write(nameBytes, 0, nameBytes.Length);

                        var contentHash = ComputeSha256Bytes(component.Stream);
                        var hashLength = BitConverter.GetBytes(contentHash.Length);
                        output.Write(hashLength, 0, hashLength.Length);
                        output.Write(contentHash, 0, contentHash.Length);
                    }

                    output.Position = 0;
                    using (var sha = SHA256.Create())
                    {
                        return ToHex(sha.ComputeHash(output));
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] ComputeSha256Bytes(Stream stream)
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(stream);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes ?? Array.Empty<byte>()).Replace("-", string.Empty);
        }
    }
}
