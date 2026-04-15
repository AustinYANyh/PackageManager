using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MftScanner
{
    /// <summary>
    /// 通过 NTFS MFT（FSCTL_ENUM_USN_DATA）枚举卷上所有文件和目录的原始记录。
    /// P/Invoke 声明与 MftScanService 保持一致，作为独立的数据来源层。
    /// </summary>
    public sealed class MftEnumerator
    {
        // ── Win32 常量 ──────────────────────────────────────────────────────────
        private const uint GENERIC_READ          = 0x80000000;
        private const uint FILE_SHARE_READ       = 1;
        private const uint FILE_SHARE_WRITE      = 2;
        private const uint FILE_SHARE_DELETE     = 4;
        private const uint OPEN_EXISTING         = 3;
        private const uint FSCTL_ENUM_USN_DATA   = 0x000900B3;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ── P/Invoke 声明 ───────────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref MftEnumDataV0 lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ── 内部结构 ────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MftEnumDataV0
        {
            public ulong StartFileReferenceNumber;
            public long  LowUsn;
            public long  HighUsn;
        }

        // ── 公开 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// 枚举指定卷上的所有 MFT 记录，以 <see cref="RawMftEntry"/> 形式逐条 yield。
        /// 若无法打开卷（权限不足等），抛出 <see cref="InvalidOperationException"/>。
        /// </summary>
        /// <param name="driveLetter">盘符，如 'C'。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>原始 MFT 条目序列。</returns>
        public IEnumerable<RawMftEntry> EnumerateVolume(char driveLetter, CancellationToken ct)
        {
            var volumePath = @"\\.\" + driveLetter + ":";
            var handle = CreateFile(
                volumePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"无法打开卷 {volumePath}，错误码={err}");
            }

            const int bufferSize = 128 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var enumData = new MftEnumDataV0
                {
                    StartFileReferenceNumber = 0,
                    LowUsn  = 0,
                    HighUsn = long.MaxValue
                };

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var ok = DeviceIoControl(
                        handle,
                        FSCTL_ENUM_USN_DATA,
                        ref enumData,
                        Marshal.SizeOf(typeof(MftEnumDataV0)),
                        buffer,
                        bufferSize,
                        out var bytesReturned,
                        IntPtr.Zero);

                    if (!ok) break;
                    if (bytesReturned <= 8) break;

                    // 前 8 字节是下一次枚举的起始 FRN
                    enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer, 0);

                    var offset = 8;
                    while (offset + 60 < bytesReturned)
                    {
                        var recordLength = Marshal.ReadInt32(buffer, offset);
                        if (recordLength <= 0) break;

                        var frn       = (ulong)Marshal.ReadInt64(buffer, offset + 8)  & 0x0000FFFFFFFFFFFF;
                        var parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16) & 0x0000FFFFFFFFFFFF;
                        var fileAttributes  = (uint)Marshal.ReadInt32(buffer, offset + 52);
                        var fileNameLength  = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                        var fileNameOffset  = (ushort)Marshal.ReadInt16(buffer, offset + 58);

                        if (fileNameLength > 0 &&
                            offset + fileNameOffset + fileNameLength <= bytesReturned)
                        {
                            var fileName = Marshal.PtrToStringUni(
                                IntPtr.Add(buffer, offset + fileNameOffset),
                                fileNameLength / 2);

                            var isDirectory = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                            yield return new RawMftEntry
                            {
                                Frn          = frn,
                                ParentFrn    = parentFrn,
                                FileName     = fileName,
                                FileAttributes = fileAttributes,
                                IsDirectory  = isDirectory
                            };
                        }

                        offset += recordLength;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// MFT 枚举返回的原始条目，包含文件引用号、父目录引用号、文件名和属性。
    /// </summary>
    public struct RawMftEntry
    {
        /// <summary>文件引用号（File Reference Number）。</summary>
        public ulong Frn;

        /// <summary>父目录的文件引用号。</summary>
        public ulong ParentFrn;

        /// <summary>文件或目录名称（原始大小写）。</summary>
        public string FileName;

        /// <summary>Win32 文件属性标志（FILE_ATTRIBUTE_*）。</summary>
        public uint FileAttributes;

        /// <summary>是否为目录。</summary>
        public bool IsDirectory;
    }
}
