using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.Win32;

namespace PackageManager.Services
{
    /// <summary>
    /// 应用程序版本信息
    /// </summary>
    public class ApplicationVersion
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string ExecutablePath { get; set; }
        public string InstallPath { get; set; }

        public string DisPlayName => ToString();

        
        
        public override string ToString()
        {
            return string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
        }
    }

    /// <summary>
    /// 应用程序查找服务
    /// </summary>
    public class ApplicationFinderService
    {
        /// <summary>
        /// 查找指定程序的所有版本
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的所有版本列表</returns>
        public List<ApplicationVersion> FindAllApplicationVersions(string programName)
        {
            if (string.IsNullOrWhiteSpace(programName))
                return new List<ApplicationVersion>();

            var allVersions = new List<ApplicationVersion>();

            if (string.Equals(programName, "Revit", StringComparison.OrdinalIgnoreCase))
            {
                var revitByGuid = FindAllRevitByGuid();
                if (revitByGuid.Any())
                {
                    allVersions.AddRange(revitByGuid);
                    return allVersions;
                }
            }
            // 在PATH环境变量中查找
            var pathVersions = FindAllInPath(programName);
            allVersions.AddRange(pathVersions);

            // 在注册表中查找
            var registryVersions = FindAllInRegistry(programName);
            allVersions.AddRange(registryVersions);

            // 在常见安装目录中查找
            var commonDirVersions = FindAllInCommonDirectories(programName);
            allVersions.AddRange(commonDirVersions);

            // 去重并排序
            var uniqueVersions = allVersions
                .GroupBy(v => v.ExecutablePath)
                .Select(g => g.First())
                .OrderBy(v => v.Name)
                .ThenBy(v => v.Version)
                .ToList();

            return uniqueVersions;
        }

        /// <summary>
        /// 根据程序名称查找可执行文件的路径（保持向后兼容）
        /// </summary>
        /// <param name="programName">程序名称（不包含扩展名）</param>
        /// <returns>可执行文件的完整路径，如果未找到则返回null</returns>
        public string FindApplicationPath(string programName)
        {
            if (string.IsNullOrWhiteSpace(programName))
                return null;

            if (string.Equals(programName, "Revit", StringComparison.OrdinalIgnoreCase))
            {
                var byGuid = FindRevitByGuid();
                if (!string.IsNullOrEmpty(byGuid))
                    return byGuid;
            }
            // 1. 首先在PATH环境变量中查找
            var pathResult = FindInPath(programName);
            if (!string.IsNullOrEmpty(pathResult))
                return pathResult;

            // 2. 在注册表中查找已安装的程序
            var registryResult = FindInRegistry(programName);
            if (!string.IsNullOrEmpty(registryResult))
                return registryResult;

            // 3. 在常见的程序安装目录中查找
            var commonDirsResult = FindInCommonDirectories(programName);
            if (!string.IsNullOrEmpty(commonDirsResult))
                return commonDirsResult;

            return null;
        }

        private string FindRevitByGuid()
        {
            try
            {
                var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
                var roots = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                var regex = new Regex(@"^7346B4A0-\d{4}-0510-0000-705C0D862004$", RegexOptions.IgnoreCase);
                foreach (var view in views)
                {
                    foreach (var root in roots)
                    {
                        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(root))
                        {
                            if (key == null) continue;
                            foreach (var name in key.GetSubKeyNames())
                            {
                                if (!regex.IsMatch(name)) continue;
                                using (var sub = key.OpenSubKey(name))
                                {
                                    if (sub == null) continue;
                                    var installLocation = sub.GetValue("InstallLocation")?.ToString();
                                    if (string.IsNullOrWhiteSpace(installLocation)) continue;
                                    var exe = Path.Combine(installLocation, "Revit.exe");
                                    if (File.Exists(exe)) return exe;
                                    var alt = FindExecutableInDirectory(installLocation, "Revit");
                                    if (!string.IsNullOrEmpty(alt)) return alt;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private List<ApplicationVersion> FindAllRevitByGuid()
        {
            var results = new List<ApplicationVersion>();
            try
            {
                var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
                var roots = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };
                var regex = new Regex(@"^{7346B4A0-\d{4}-0510-0000-705C0D862004}$", RegexOptions.IgnoreCase);
                foreach (var view in views)
                {
                    foreach (var root in roots)
                    {
                        using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(root))
                        {
                            if (key == null) continue;
                            foreach (var name in key.GetSubKeyNames())
                            {
                                if (!regex.IsMatch(name)) continue;
                                using (var sub = key.OpenSubKey(name))
                                {
                                    if (sub == null) continue;
                                    var installLocation = sub.GetValue("InstallLocation")?.ToString();
                                    var version = sub.GetValue("DisplayName")?.ToString();
                                    if (string.IsNullOrWhiteSpace(installLocation)) continue;
                                    var exe = Path.Combine(installLocation, "Revit.exe");
                                    var execPath = File.Exists(exe) ? exe : FindExecutableInDirectory(installLocation, "Revit");
                                    if (string.IsNullOrEmpty(execPath)) continue;
                                    if (version != null)
                                    {
                                        results.Add(new ApplicationVersion
                                        {
                                            Name = "Revit",
                                            Version = version.Replace("Revit", string.Empty).Trim(),
                                            ExecutablePath = execPath,
                                            InstallPath = installLocation
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignored
            }

            return results;
        }
        /// <summary>
        /// 在PATH环境变量中查找所有版本的程序
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序版本列表</returns>
        private List<ApplicationVersion> FindAllInPath(string programName)
        {
            var results = new List<ApplicationVersion>();
            
            try
            {
                var pathVariable = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathVariable))
                    return results;

                var paths = pathVariable.Split(';');
                var executableExtensions = new[] { ".exe", ".bat", ".cmd", ".com" };

                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        continue;

                    foreach (var extension in executableExtensions)
                    {
                        var exactMatch = Path.Combine(path, programName + extension);
                        if (File.Exists(exactMatch))
                        {
                            var version = ExtractVersionFromPath(exactMatch);
                            results.Add(new ApplicationVersion
                            {
                                Name = programName,
                                Version = version,
                                ExecutablePath = exactMatch,
                                InstallPath = path
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常，继续其他查找方式
            }

            return results;
        }

        /// <summary>
        /// 在PATH环境变量中查找程序（保持向后兼容）
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序路径</returns>
        private string FindInPath(string programName)
        {
            try
            {
                var pathVariable = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathVariable))
                    return null;

                var paths = pathVariable.Split(';');
                var executableExtensions = new[] { ".exe", ".bat", ".cmd", ".com" };

                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                        continue;

                    foreach (var extension in executableExtensions)
                    {
                        var fullPath = Path.Combine(path, programName + extension);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常，继续其他查找方式
            }

            return null;
        }

        /// <summary>
        /// 从路径中提取版本信息
        /// </summary>
        /// <param name="path">文件或目录路径</param>
        /// <returns>提取的版本信息</returns>
        private string ExtractVersionFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            // 匹配常见的版本模式：2020, 2021, v2.0, 1.0.0 等
            var versionPatterns = new[]
            {
                @"(\d{4})",                    // 年份版本：2020, 2021
                @"[vV]?(\d+\.\d+(?:\.\d+)?)",  // 语义版本：v1.0, 2.1.0
                @"(\d+\.\d+)",                 // 简单版本：1.0, 2.5
                @"[rR](\d+)",                  // R版本：R14, R15
            };

            foreach (var pattern in versionPatterns)
            {
                var match = Regex.Match(path, pattern);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 在注册表中查找所有版本的已安装程序
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序版本列表</returns>
        private List<ApplicationVersion> FindAllInRegistry(string programName)
        {
            var results = new List<ApplicationVersion>();
            
            try
            {
                var registryKeys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var keyPath in registryKeys)
                {
                    using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(keyPath))
                    {
                        if (key == null) continue;

                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using (var subKey = key.OpenSubKey(subKeyName))
                            {
                                if (subKey == null) continue;

                                var displayName = subKey.GetValue("DisplayName")?.ToString();
                                var installLocation = subKey.GetValue("InstallLocation")?.ToString();
                                var displayVersion = subKey.GetValue("DisplayVersion")?.ToString();

                                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(installLocation))
                                    continue;

                                // 精确匹配程序名称（不区分大小写）
                                if (displayName.IndexOf(programName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var executablePaths = FindExecutablesInDirectory(installLocation, programName);
                                    foreach (var execPath in executablePaths)
                                    {
                                        var version = displayVersion ?? ExtractVersionFromPath(displayName) ?? ExtractVersionFromPath(execPath);
                                        results.Add(new ApplicationVersion
                                        {
                                            Name = programName,
                                            Version = version,
                                            ExecutablePath = execPath,
                                            InstallPath = installLocation
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常，继续其他查找方式
            }

            return results;
        }

        /// <summary>
        /// 在注册表中查找已安装的程序（保持向后兼容）
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序路径</returns>
        private string FindInRegistry(string programName)
        {
            try
            {
                var registryKeys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var keyPath in registryKeys)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(keyPath))
                    {
                        if (key == null) continue;

                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using (var subKey = key.OpenSubKey(subKeyName))
                            {
                                if (subKey == null) continue;

                                var displayName = subKey.GetValue("DisplayName")?.ToString();
                                var installLocation = subKey.GetValue("InstallLocation")?.ToString();

                                if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(installLocation))
                                    continue;

                                // 检查程序名称是否匹配
                                if (displayName.IndexOf(programName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var executablePath = FindExecutableInDirectory(installLocation, programName);
                                    if (!string.IsNullOrEmpty(executablePath))
                                        return executablePath;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常，继续其他查找方式
            }

            return null;
        }

        /// <summary>
        /// 在指定目录中查找可执行文件
        /// </summary>
        /// <param name="directory">目录路径</param>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的可执行文件路径列表</returns>
        private List<string> FindExecutablesInDirectory(string directory, string programName)
        {
            var results = new List<string>();
            
            if (!Directory.Exists(directory))
                return results;

            try
            {
                // 查找直接匹配的可执行文件
                var executableExtensions = new[] { ".exe", ".bat", ".cmd" };
                
                foreach (var ext in executableExtensions)
                {
                    var exactPath = Path.Combine(directory, programName + ext);
                    if (File.Exists(exactPath))
                    {
                        results.Add(exactPath);
                    }
                }

                // 递归查找子目录中的可执行文件（限制深度为3层）
                SearchDirectoryRecursive(directory, programName, results, 0, 3);
            }
            catch (Exception)
            {
                // 忽略异常
            }

            return results;
        }

        /// <summary>
        /// 递归搜索目录中的可执行文件
        /// </summary>
        /// <param name="directory">目录路径</param>
        /// <param name="programName">程序名称</param>
        /// <param name="results">结果列表</param>
        /// <param name="currentDepth">当前深度</param>
        /// <param name="maxDepth">最大深度</param>
        private void SearchDirectoryRecursive(string directory, string programName, List<string> results, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth || !Directory.Exists(directory))
                return;

            try
            {
                // 查找当前目录中的可执行文件
                var files = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.Equals(programName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }

                // 递归搜索子目录
                var subdirectories = Directory.GetDirectories(directory);
                foreach (var subdir in subdirectories)
                {
                    SearchDirectoryRecursive(subdir, programName, results, currentDepth + 1, maxDepth);
                }
            }
            catch (Exception)
            {
                // 忽略异常
            }
        }

        /// <summary>
        /// 在常见安装目录中查找所有版本的程序
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序版本列表</returns>
        private List<ApplicationVersion> FindAllInCommonDirectories(string programName)
        {
            var results = new List<ApplicationVersion>();
            
            var commonDirectories = new[]
            {
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\ProgramData",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };

            foreach (var baseDir in commonDirectories)
            {
                if (!Directory.Exists(baseDir))
                    continue;

                try
                {
                    var executablePaths = FindExecutablesInDirectory(baseDir, programName);
                    foreach (var execPath in executablePaths)
                    {
                        var version = ExtractVersionFromPath(execPath) ?? ExtractVersionFromPath(execPath);
                        results.Add(new ApplicationVersion
                        {
                            Name = programName,
                            Version = version,
                            ExecutablePath = execPath,
                            InstallPath = baseDir
                        });
                    }
                }
                catch (Exception)
                {
                    // 忽略异常，继续下一个目录
                }
            }

            return results;
        }

        /// <summary>
        /// 在常见安装目录中查找程序（保持向后兼容）
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序路径</returns>
        private string FindInCommonDirectories(string programName)
        {
            try
            {
                var commonDirectories = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"C:\Program Files",
                    @"C:\Program Files (x86)",
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local"),
                    @"C:\Windows\System32",
                    @"C:\Windows"
                };

                foreach (var directory in commonDirectories)
                {
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                        continue;

                    var result = SearchDirectoryRecursively(directory, programName, 3); // 限制递归深度为3
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }
            catch (Exception)
            {
                // 忽略异常
            }

            return null;
        }

        /// <summary>
        /// 在指定目录中递归查找可执行文件
        /// </summary>
        /// <param name="directory">目录路径</param>
        /// <param name="programName">程序名称</param>
        /// <param name="maxDepth">最大递归深度</param>
        /// <returns>找到的程序路径</returns>
        private string SearchDirectoryRecursively(string directory, string programName, int maxDepth)
        {
            if (maxDepth <= 0 || !Directory.Exists(directory))
                return null;

            try
            {
                // 在当前目录中查找
                var result = FindExecutableInDirectory(directory, programName);
                if (!string.IsNullOrEmpty(result))
                    return result;

                // 递归查找子目录
                var subdirectories = Directory.GetDirectories(directory);
                foreach (var subdirectory in subdirectories)
                {
                    result = SearchDirectoryRecursively(subdirectory, programName, maxDepth - 1);
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }
            catch (Exception)
            {
                // 忽略访问权限等异常
            }

            return null;
        }

        /// <summary>
        /// 在指定目录中查找可执行文件
        /// </summary>
        /// <param name="directory">目录路径</param>
        /// <param name="programName">程序名称</param>
        /// <returns>找到的程序路径</returns>
        private string FindExecutableInDirectory(string directory, string programName)
        {
            if (!Directory.Exists(directory))
                return null;

            try
            {
                var executableExtensions = new[] { ".exe", ".bat", ".cmd", ".com" };

                foreach (var extension in executableExtensions)
                {
                    var exactMatch = Path.Combine(directory, programName + extension);
                    if (File.Exists(exactMatch))
                        return exactMatch;
                }

                // 如果精确匹配失败，尝试模糊匹配
                var files = Directory.GetFiles(directory, "*.exe");
                foreach (var file in files)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.IndexOf(programName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return file;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略异常
            }

            return null;
        }

        /// <summary>
        /// 获取所有可能的程序路径（用于调试或提供选择）
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>所有找到的程序路径列表</returns>
        public List<string> FindAllApplicationPaths(string programName)
        {
            var results = new List<string>();

            if (string.IsNullOrWhiteSpace(programName))
                return results;

            // 在PATH中查找
            var pathResult = FindInPath(programName);
            if (!string.IsNullOrEmpty(pathResult))
                results.Add(pathResult);

            // 在注册表中查找
            var registryResult = FindInRegistry(programName);
            if (!string.IsNullOrEmpty(registryResult) && !results.Contains(registryResult))
                results.Add(registryResult);

            // 在常见目录中查找
            var commonDirsResult = FindInCommonDirectories(programName);
            if (!string.IsNullOrEmpty(commonDirsResult) && !results.Contains(commonDirsResult))
                results.Add(commonDirsResult);

            return results;
        }
    }
}
