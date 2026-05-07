using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using PackageManager.Models;
using System.Collections.ObjectModel;
using System.Linq;
using CustomControlLibrary.CustomControl.Controls.DataGrid.Filter;

namespace PackageManager.Services
{
    /// <summary>
    /// 包状态数据模型，用于序列化保存
    /// </summary>
    public class PackageStateData
    {
        /// <summary>
        /// 产品名称
        /// </summary>
        public string ProductName { get; set; }

        /// <summary>
        /// 本地安装路径
        /// </summary>
        public string LocalPath { get; set; }

        /// <summary>
        /// 上传包名称
        /// </summary>
        public string UploadPackageName { get; set; }

        /// <summary>
        /// 选中的可执行文件版本
        /// </summary>
        public string SelectedExecutableVersion { get; set; }

        /// <summary>
        /// 可执行文件路径
        /// </summary>
        public string ExecutablePath { get; set; }

        /// <summary>
        /// 是否启用调试模式
        /// </summary>
        public bool IsDebugMode { get; set; }

        /// <summary>
        /// 可用的可执行文件版本列表
        /// </summary>
        public List<ApplicationVersion> AvailableExecutableVersions { get; set; } = new List<ApplicationVersion>();

        /// <summary>
        /// 版本与本地路径的映射字典
        /// </summary>
        public Dictionary<string, string> VersionLocalPaths { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 主界面状态数据模型
    /// </summary>
    public class MainWindowStateData
    {
        /// <summary>
        /// 包状态数据列表
        /// </summary>
        public List<PackageStateData> Packages { get; set; } = new List<PackageStateData>();

        /// <summary>
        /// 最后保存时间
        /// </summary>
        public DateTime LastSaved { get; set; } = DateTime.Now;

        /// <summary>
        /// 主界面筛选条件集合，用于精确还原各列筛选
        /// </summary>
        public List<FilterCondition> PackageGridFilterConditions { get; set; } = new List<FilterCondition>();

        /// <summary>
        /// 筛选是否使用"或"逻辑
        /// </summary>
        public bool UseOrLogic { get; set; }

        /// <summary>
        /// 产品可见性配置字典
        /// </summary>
        public Dictionary<string, bool> ProductVisibility { get; set; } = new Dictionary<string, bool>();
    }

    /// <summary>
    /// 数据持久化服务，用于保存和加载应用程序查询结果和主界面状态
    /// </summary>
    public class DataPersistenceService
    {
        private readonly string _dataFilePath;

        private readonly string _mainWindowStateFilePath;

        private readonly string _settingsFilePath;
        private readonly string _commonStartupSettingsFilePath;

        private readonly string _packagesFilePath;
        private readonly string _finalizePackagesFilePath;

        private readonly string _appFolder;
        private readonly string _settingsBackupFolderPath;
        private readonly string _commonStartupSettingsBackupFolderPath;

        private readonly JsonSerializerSettings _jsonSettings;

        private List<FilterCondition> _lastGridFilterConditions;

        private bool useOrLogic;

        private Dictionary<string, bool> _productVisibility = new Dictionary<string, bool>();

        /// <summary>
        /// 初始化数据持久化服务，创建数据文件目录并设置文件路径
        /// </summary>
        public DataPersistenceService()
        {
            // 数据文件保存在应用程序数据目录
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _appFolder = Path.Combine(appDataPath, "PackageManager");

            if (!Directory.Exists(_appFolder))
            {
                Directory.CreateDirectory(_appFolder);
            }

            _dataFilePath = Path.Combine(_appFolder, "application_cache.json");
            _mainWindowStateFilePath = Path.Combine(_appFolder, "main_window_state.json");
            _settingsFilePath = Path.Combine(_appFolder, "settings.json");
            _commonStartupSettingsFilePath = Path.Combine(_appFolder, "common_startup_settings.json");
            _settingsBackupFolderPath = Path.Combine(_appFolder, "settings_history");
            _commonStartupSettingsBackupFolderPath = Path.Combine(_appFolder, "common_startup_settings_history");
            _packagesFilePath = Path.Combine(_appFolder, "packages.json");
            _finalizePackagesFilePath = Path.Combine(_appFolder, "finalize_packages.json");

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        /// <summary>
        /// 包配置项数据模型
        /// </summary>
        public class PackageConfigItem
        {
            /// <summary>
            /// 产品名称
            /// </summary>
            public string ProductName { get; set; }

            /// <summary>
            /// FTP 服务器路径
            /// </summary>
            public string FtpServerPath { get; set; }

            /// <summary>
            /// 定版包 FTP 服务器路径
            /// </summary>
            public string FinalizeFtpServerPath { get; set; }

            /// <summary>
            /// 本地安装路径
            /// </summary>
            public string LocalPath { get; set; }

            /// <summary>
            /// 是否支持配置操作
            /// </summary>
            public bool SupportsConfigOps { get; set; } = true;
        }

        /// <summary>
        /// 从本地文件加载包配置列表
        /// </summary>
        /// <returns>包配置列表；如果文件不存在或加载失败则返回空列表</returns>
        public List<PackageConfigItem> LoadPackageConfigs()
        {
            try
            {
                if (!File.Exists(_packagesFilePath))
                {
                    return new List<PackageConfigItem>();
                }

                var json = File.ReadAllText(_packagesFilePath);
                var list = JsonConvert.DeserializeObject<List<PackageConfigItem>>(json, _jsonSettings);
                return list ?? new List<PackageConfigItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载包配置失败: {ex.Message}");
                return new List<PackageConfigItem>();
            }
        }

        /// <summary>
        /// 将包配置列表保存到本地文件
        /// </summary>
        /// <param name="items">要保存的包配置集合</param>
        /// <returns>如果保存成功则返回 <c>true</c>，否则返回 <c>false</c></returns>
        public bool SavePackageConfigs(IEnumerable<PackageConfigItem> items)
        {
            try
            {
                var list = items?.ToList() ?? new List<PackageConfigItem>();
                var json = JsonConvert.SerializeObject(list, _jsonSettings);
                File.WriteAllText(_packagesFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存包配置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取包配置文件的路径
        /// </summary>
        /// <returns>包配置文件的完整路径</returns>
        public string GetPackagesConfigPath() => _packagesFilePath;

        /// <summary>
        /// 从本地文件加载定版包配置列表
        /// </summary>
        /// <returns>定版包配置列表；如果文件不存在或加载失败则返回空列表</returns>
        public List<PackageConfigItem> LoadFinalizePackageConfigs()
        {
            try
            {
                if (!File.Exists(_finalizePackagesFilePath))
                {
                    return new List<PackageConfigItem>();
                }

                var json = File.ReadAllText(_finalizePackagesFilePath);
                var list = JsonConvert.DeserializeObject<List<PackageConfigItem>>(json, _jsonSettings);
                return list ?? new List<PackageConfigItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载定版包配置失败: {ex.Message}");
                return new List<PackageConfigItem>();
            }
        }

        /// <summary>
        /// 将定版包配置列表保存到本地文件
        /// </summary>
        /// <param name="items">要保存的定版包配置集合</param>
        /// <returns>如果保存成功则返回 <c>true</c>，否则返回 <c>false</c></returns>
        public bool SaveFinalizePackageConfigs(IEnumerable<PackageConfigItem> items)
        {
            try
            {
                var list = items?.ToList() ?? new List<PackageConfigItem>();
                var json = JsonConvert.SerializeObject(list, _jsonSettings);
                File.WriteAllText(_finalizePackagesFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存定版包配置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取定版包配置文件的路径。
        /// </summary>
        /// <returns>定版包配置文件的完整路径。</returns>
        public string GetFinalizePackagesConfigPath() => _finalizePackagesFilePath;

        /// <summary>
        /// 获取内置的开发包配置列表。
        /// </summary>
        /// <returns>内置开发包配置列表。</returns>
        public List<PackageConfigItem> GetBuiltInPackageConfigs()
        {
            return new List<PackageConfigItem>
            {
                new()
                {
                    ProductName = "MaxiBIM（CAB）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/HWMaxiBIMCAB/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBIM(CAB)",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（CAB）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "MaxiBIM（MEP）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(MEP)/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/建模大师（机电）",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（MEP）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "MaxiBIM（PMEP）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/MaxiBIM(PMEP)/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBim（管道）", 
                    LocalPath = @"C:\红瓦科技\MaxiBIM（PMEP）Develop",
                    SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "MaxiBIM（SH）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/HWMaxiBIMSH/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBIM(SH)", 
                    LocalPath = @"C:\红瓦科技\MaxiBIM（SH）Develop",
                    SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "MaxiBIM（Duct）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/HWMaxiBIMDUCT/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBim（风管）",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（Duct）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "建模大师（CABE）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(CABE)/",
                    LocalPath = @"C:\红瓦科技\建模大师（CABE）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "建模大师（钢构）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(ST)/",
                    LocalPath = @"C:\红瓦科技\建模大师（钢构）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "建模大师（施工）Develop", FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(CST)/",
                    FinalizeFtpServerPath = "http://192.168.0.215:8001/Publish/建模大师（施工）",
                    LocalPath = @"C:\红瓦科技\建模大师（施工）Develop", SupportsConfigOps = true,
                },
                new()
                {
                    ProductName = "BuildMaster(Dazzle)", FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(Dazzle)/Dazzle.RevitApp/",
                    LocalPath = @"C:\红瓦科技\BuildMaster(Dazzle)Develop", SupportsConfigOps = false,
                },
                new()
                {
                    ProductName = "TeamworkMaster(Develop)", FtpServerPath = "http://doc-dev.hongwa.cc:8001/TeamworkMaster/",
                    LocalPath = @"C:\红瓦科技\TeamworkMaster(Develop)", SupportsConfigOps = true,
                },
            };
        }

        /// <summary>
        /// 获取内置的定版包配置列表。
        /// </summary>
        /// <returns>内置定版包配置列表。</returns>
        public List<PackageConfigItem> GetBuiltInFinalizePackageConfigs()
        {
            return new List<PackageConfigItem>
            {
                new PackageConfigItem { ProductName = "MaxiBim（管道）", FtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBim（管道）/", LocalPath = @"C:\红瓦科技\MaxiBim（管道）", SupportsConfigOps = false },
                new PackageConfigItem { ProductName = "MaxiBim（风管）", FtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBim（风管）/", LocalPath = @"C:\红瓦科技\MaxiBim（风管）", SupportsConfigOps = false },
                // 建模大师系列
                new PackageConfigItem { ProductName = "建模大师（机电）", FtpServerPath = "http://192.168.0.215:8001/Publish/建模大师（机电）/", LocalPath = @"C:\红瓦科技\建模大师（CABE）", SupportsConfigOps = false },
                new PackageConfigItem { ProductName = "建模大师（钢构）", FtpServerPath = "http://192.168.0.215:8001/Publish/建模大师（钢构）/", LocalPath = @"C:\红瓦科技\建模大师（钢构）", SupportsConfigOps = false },
                new PackageConfigItem { ProductName = "建模大师（施工）", FtpServerPath = "http://192.168.0.215:8001/Publish/建模大师（施工）/", LocalPath = @"C:\红瓦科技\建模大师（施工）", SupportsConfigOps = false },
                // MaxiBIM 常见目录
                new PackageConfigItem { ProductName = "MaxiBIM（CAB）", FtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBIM(CAB)/", LocalPath = @"C:\红瓦科技\MaxiBIM（CAB）", SupportsConfigOps = false },
                new PackageConfigItem { ProductName = "MaxiBIM（SH）", FtpServerPath = "http://192.168.0.215:8001/Publish/MaxiBIM(SH)/", LocalPath = @"C:\红瓦科技\MaxiBIM（SH）", SupportsConfigOps = false },
            };
        }

        /// <summary>
        /// 保存应用程序查询结果到文件
        /// </summary>
        /// <param name="applicationData">应用程序数据字典，键为程序名称，值为版本列表</param>
        /// <returns>是否保存成功</returns>
        public bool SaveApplicationData(Dictionary<string, List<ApplicationVersion>> applicationData)
        {
            try
            {
                var json = JsonConvert.SerializeObject(applicationData, _jsonSettings);
                File.WriteAllText(_dataFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                // 记录错误（这里简单忽略，实际应用中应该记录日志）
                System.Diagnostics.Debug.WriteLine($"保存数据失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从文件加载应用程序查询结果
        /// </summary>
        /// <returns>应用程序数据字典，如果加载失败则返回空字典</returns>
        public Dictionary<string, List<ApplicationVersion>> LoadApplicationData()
        {
            try
            {
                if (!File.Exists(_dataFilePath))
                {
                    return new Dictionary<string, List<ApplicationVersion>>();
                }

                var json = File.ReadAllText(_dataFilePath);
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<ApplicationVersion>>>(json, _jsonSettings);
                return data ?? new Dictionary<string, List<ApplicationVersion>>();
            }
            catch (Exception ex)
            {
                // 记录错误（这里简单忽略，实际应用中应该记录日志）
                System.Diagnostics.Debug.WriteLine($"加载数据失败: {ex.Message}");
                return new Dictionary<string, List<ApplicationVersion>>();
            }
        }

        /// <summary>
        /// 检查指定程序是否已有缓存数据
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>是否存在缓存数据</returns>
        public bool HasCachedData(string programName)
        {
            var data = LoadApplicationData();
            return data.ContainsKey(programName) && data[programName].Count > 0;
        }

        /// <summary>
        /// 获取指定程序的缓存数据
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <returns>缓存的版本列表，如果不存在则返回空列表</returns>
        public List<ApplicationVersion> GetCachedData(string programName)
        {
            var data = LoadApplicationData();
            return data.ContainsKey(programName) ? data[programName] : new List<ApplicationVersion>();
        }

        /// <summary>
        /// 更新指定程序的缓存数据
        /// </summary>
        /// <param name="programName">程序名称</param>
        /// <param name="versions">版本列表</param>
        /// <returns>是否更新成功</returns>
        public bool UpdateCachedData(string programName, List<ApplicationVersion> versions)
        {
            var data = LoadApplicationData();
            data[programName] = versions;
            return SaveApplicationData(data);
        }

        /// <summary>
        /// 清除所有缓存数据
        /// </summary>
        /// <returns>是否清除成功</returns>
        public bool ClearAllData()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    File.Delete(_dataFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清除数据失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取缓存文件的最后修改时间
        /// </summary>
        /// <returns>最后修改时间，如果文件不存在则返回null</returns>
        public DateTime? GetCacheLastModified()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    return File.GetLastWriteTime(_dataFilePath);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存主界面状态数据
        /// </summary>
        /// <param name="packages">包信息集合</param>
        /// <returns>是否保存成功</returns>
        public bool SaveMainWindowState(ObservableCollection<PackageInfo> packages)
        {
            try
            {
                var stateData = new MainWindowStateData();

                foreach (var package in packages)
                {
                    var packageState = new PackageStateData
                    {
                        ProductName = package.ProductName,
                        LocalPath = package.LocalPath,
                        UploadPackageName = package.UploadPackageName,
                        SelectedExecutableVersion = package.SelectedExecutableVersion,
                        ExecutablePath = package.ExecutablePath,
                        IsDebugMode = package.IsDebugMode,
                        AvailableExecutableVersions = package.AvailableExecutableVersions?.ToList() ?? new List<ApplicationVersion>(),
                        VersionLocalPaths = package.VersionLocalPaths?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>()
                    };
                    stateData.Packages.Add(packageState);
                }

                // 包含最近一次提取的筛选条件集合
                stateData.PackageGridFilterConditions = _lastGridFilterConditions ?? new List<FilterCondition>();
                stateData.UseOrLogic = useOrLogic;
                stateData.ProductVisibility = _productVisibility ?? new Dictionary<string, bool>();

                var json = JsonConvert.SerializeObject(stateData, _jsonSettings);
                File.WriteAllText(_mainWindowStateFilePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存主界面状态失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 保存主界面筛选条件集合（仅更新缓存，持久化由 SaveMainWindowState 统一执行）
        /// </summary>
        /// <param name="filterConditions">筛选条件集合</param>
        /// <param name="filterManagerUseOrLogic"></param>
        /// <returns>是否保存成功（更新缓存成功）</returns>
        public bool SaveMainWindowFilterCondition(ObservableCollection<FilterCondition> filterConditions, bool filterManagerUseOrLogic)
        {
            try
            {
                _lastGridFilterConditions = filterConditions?.ToList() ?? new List<FilterCondition>();
                useOrLogic = filterManagerUseOrLogic;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存筛选条件集合失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 加载主界面状态数据
        /// </summary>
        /// <returns>主界面状态数据，如果加载失败则返回null</returns>
        public MainWindowStateData LoadMainWindowState()
        {
            try
            {
                if (!File.Exists(_mainWindowStateFilePath))
                {
                    return null;
                }

                var json = File.ReadAllText(_mainWindowStateFilePath);
                var stateData = JsonConvert.DeserializeObject<MainWindowStateData>(json, _jsonSettings);

                // 读取筛选条件集合
                _lastGridFilterConditions = stateData?.PackageGridFilterConditions ?? new List<FilterCondition>();
                useOrLogic = stateData?.UseOrLogic ?? false;
                _productVisibility = stateData?.ProductVisibility ?? new Dictionary<string, bool>();
                return stateData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载主界面状态失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 检查是否存在主界面状态数据
        /// </summary>
        /// <returns>是否存在状态数据</returns>
        public bool HasMainWindowState()
        {
            return File.Exists(_mainWindowStateFilePath);
        }

        /// <summary>
        /// 将状态数据应用到PackageInfo对象
        /// </summary>
        /// <param name="package">目标PackageInfo对象</param>
        /// <param name="stateData">状态数据</param>
        public void ApplyStateToPackage(PackageInfo package, PackageStateData stateData)
        {
            if (package == null || stateData == null) return;

            // 应用基本属性
            // 先应用调试模式，再设置LocalPath以便配置文件优先生效
            package.IsDebugMode = stateData.IsDebugMode;
            package.LocalPath = stateData.LocalPath;
            try
            {
                package.VersionLocalPaths = stateData.VersionLocalPaths ?? new Dictionary<string, string>();
            }
            catch { }
            if (stateData.AvailableExecutableVersions?.Count > 0)
            {
                package.AvailableExecutableVersions.Clear();
                foreach (var execVersion in stateData.AvailableExecutableVersions)
                {
                    package.AvailableExecutableVersions.Add(execVersion);
                }
            }
            
            package.SelectedExecutableVersion = stateData.SelectedExecutableVersion;
            package.ExecutablePath = stateData.ExecutablePath;
        }

        /// <summary>
        /// 清除主界面状态数据
        /// </summary>
        /// <returns>是否清除成功</returns>
        public bool ClearMainWindowState()
        {
            try
            {
                if (File.Exists(_mainWindowStateFilePath))
                {
                    File.Delete(_mainWindowStateFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清除主界面状态失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 清除所有缓存数据
        /// </summary>
        /// <returns>是否清除成功</returns>
        public bool ClearAllCachedData()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    File.Delete(_dataFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清除缓存数据失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 保存应用程序设置
        /// </summary>
        /// <param name="settings">设置对象</param>
        /// <returns>是否保存成功</returns>
        public bool SaveSettings(AppSettings settings, bool preserveExistingCommonStartupData = true)
        {
            try
            {
                var effectiveSettings = settings ?? new AppSettings();

                if (preserveExistingCommonStartupData)
                {
                    var currentSettings = LoadSettings();
                    var hasIncomingStartupItems = effectiveSettings.CommonStartupItems != null && effectiveSettings.CommonStartupItems.Count > 0;
                    var hasIncomingStartupGroups = effectiveSettings.CommonStartupGroups != null && effectiveSettings.CommonStartupGroups.Count > 0;
                    var hasExistingStartupItems = currentSettings?.CommonStartupItems != null && currentSettings.CommonStartupItems.Count > 0;
                    var hasExistingStartupGroups = currentSettings?.CommonStartupGroups != null && currentSettings.CommonStartupGroups.Count > 0;

                    if (!hasIncomingStartupItems && hasExistingStartupItems)
                    {
                        effectiveSettings.CommonStartupItems = currentSettings.CommonStartupItems
                            .Select(CloneCommonStartupItem)
                            .ToList();
                    }

                    if (!hasIncomingStartupGroups && hasExistingStartupGroups)
                    {
                        effectiveSettings.CommonStartupGroups = currentSettings.CommonStartupGroups
                            .Select(CloneCommonStartupGroup)
                            .ToList();
                    }
                }

                SaveCommonStartupSettingsInternal(new CommonStartupSettings
                {
                    CommonStartupItems = effectiveSettings.CommonStartupItems ?? new List<CommonStartupItem>(),
                    CommonStartupGroups = effectiveSettings.CommonStartupGroups ?? new List<CommonStartupGroup>()
                });

                if (File.Exists(_settingsFilePath))
                {
                    File.Copy(_settingsFilePath, _settingsFilePath + ".bak", true);
                    Directory.CreateDirectory(_settingsBackupFolderPath);
                    var stampedBackupPath = Path.Combine(
                        _settingsBackupFolderPath,
                        $"settings_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                    File.Copy(_settingsFilePath, stampedBackupPath, true);

                    foreach (var staleBackup in new DirectoryInfo(_settingsBackupFolderPath)
                                 .GetFiles("settings_*.json")
                                 .OrderByDescending(file => file.LastWriteTimeUtc)
                                 .Skip(20))
                    {
                        staleBackup.Delete();
                    }
                }

                var json = JsonConvert.SerializeObject(effectiveSettings, _jsonSettings);
                var document = Newtonsoft.Json.Linq.JObject.Parse(json);
                document.Remove(nameof(AppSettings.CommonStartupItems));
                document.Remove(nameof(AppSettings.CommonStartupGroups));
                File.WriteAllText(_settingsFilePath, document.ToString());
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 加载应用程序设置
        /// </summary>
        /// <returns>设置对象，如果加载失败则返回默认设置</returns>
        public AppSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new AppSettings();
                }

                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json, _jsonSettings);
                settings ??= new AppSettings();

                var startupSettings = LoadCommonStartupSettingsInternal();
                settings.CommonStartupItems = startupSettings.CommonStartupItems ?? new List<CommonStartupItem>();
                settings.CommonStartupGroups = startupSettings.CommonStartupGroups ?? new List<CommonStartupGroup>();
                return settings;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
                var settings = new AppSettings();
                var startupSettings = LoadCommonStartupSettingsInternal();
                settings.CommonStartupItems = startupSettings.CommonStartupItems ?? new List<CommonStartupItem>();
                settings.CommonStartupGroups = startupSettings.CommonStartupGroups ?? new List<CommonStartupGroup>();
                return settings;
            }
        }

        /// <summary>
        /// 检查是否存在设置数据
        /// </summary>
        /// <returns>是否存在设置数据</returns>
        public bool HasSettings()
        {
            return File.Exists(_settingsFilePath);
        }

        /// <summary>
        /// 清除设置数据
        /// </summary>
        /// <returns>是否清除成功</returns>
        public bool ClearSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清除设置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取数据文件夹路径
        /// </summary>
        /// <returns>数据文件夹路径</returns>
        public string GetDataFolderPath()
        {
            return _appFolder;
        }

        /// <summary>
        /// 保存产品可见性配置到内存缓存。
        /// </summary>
        /// <param name="visibility">产品名称与可见性的映射字典。</param>
        /// <returns>是否保存成功。</returns>
        public bool SaveProductVisibility(Dictionary<string, bool> visibility)
        {
            try
            {
                _productVisibility = visibility?.ToDictionary(kv => kv.Key ?? string.Empty, kv => kv.Value)
                                   ?? new Dictionary<string, bool>();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存产品可见性失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取产品可见性配置。
        /// </summary>
        /// <returns>产品名称与可见性的映射字典。</returns>
        public Dictionary<string, bool> GetProductVisibility()
        {
            return _productVisibility ?? new Dictionary<string, bool>();
        }

        private static CommonStartupItem CloneCommonStartupItem(CommonStartupItem item)
        {
            if (item == null)
            {
                return null;
            }

            return new CommonStartupItem
            {
                Name = item.Name,
                FullPath = item.FullPath,
                Arguments = item.Arguments,
                Note = item.Note,
                GroupName = item.GroupName,
                IsFavorite = item.IsFavorite,
                Order = item.Order,
                LastLaunchedAt = item.LastLaunchedAt,
                LaunchCount = item.LaunchCount
            };
        }

        private static CommonStartupGroup CloneCommonStartupGroup(CommonStartupGroup group)
        {
            if (group == null)
            {
                return null;
            }

            return new CommonStartupGroup
            {
                Name = group.Name,
                Order = group.Order
            };
        }

        private CommonStartupSettings LoadCommonStartupSettingsInternal()
        {
            try
            {
                if (File.Exists(_commonStartupSettingsFilePath))
                {
                    var json = File.ReadAllText(_commonStartupSettingsFilePath);
                    var settings = JsonConvert.DeserializeObject<CommonStartupSettings>(json, _jsonSettings) ?? new CommonStartupSettings();
                    settings.CommonStartupItems ??= new List<CommonStartupItem>();
                    settings.CommonStartupGroups ??= new List<CommonStartupGroup>();
                    return settings;
                }

                if (!File.Exists(_settingsFilePath))
                {
                    return new CommonStartupSettings();
                }

                var legacyJson = File.ReadAllText(_settingsFilePath);
                var legacySettings = JsonConvert.DeserializeObject<AppSettings>(legacyJson, _jsonSettings) ?? new AppSettings();
                return new CommonStartupSettings
                {
                    CommonStartupItems = legacySettings.CommonStartupItems ?? new List<CommonStartupItem>(),
                    CommonStartupGroups = legacySettings.CommonStartupGroups ?? new List<CommonStartupGroup>()
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载启动项设置失败: {ex.Message}");
                return new CommonStartupSettings();
            }
        }

        private void SaveCommonStartupSettingsInternal(CommonStartupSettings settings)
        {
            var effectiveSettings = settings ?? new CommonStartupSettings();
            effectiveSettings.CommonStartupItems ??= new List<CommonStartupItem>();
            effectiveSettings.CommonStartupGroups ??= new List<CommonStartupGroup>();

            if (File.Exists(_commonStartupSettingsFilePath))
            {
                File.Copy(_commonStartupSettingsFilePath, _commonStartupSettingsFilePath + ".bak", true);
                Directory.CreateDirectory(_commonStartupSettingsBackupFolderPath);
                var stampedBackupPath = Path.Combine(
                    _commonStartupSettingsBackupFolderPath,
                    $"common_startup_settings_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                File.Copy(_commonStartupSettingsFilePath, stampedBackupPath, true);

                foreach (var staleBackup in new DirectoryInfo(_commonStartupSettingsBackupFolderPath)
                             .GetFiles("common_startup_settings_*.json")
                             .OrderByDescending(file => file.LastWriteTimeUtc)
                             .Skip(20))
                {
                    staleBackup.Delete();
                }
            }

            var json = JsonConvert.SerializeObject(effectiveSettings, _jsonSettings);
            File.WriteAllText(_commonStartupSettingsFilePath, json);
        }
    }

    /// <summary>
    /// 应用程序设置数据模型
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// 获取或设置是否使用 G 键快捷入口。
        /// </summary>
        public bool ProgramEntryWithG { get; set; } = true;

        /// <summary>
        /// 获取或设置 Revit 插件目录路径。
        /// </summary>
        public string AddinPath { get; set; } = @"C:\\ProgramData\\Autodesk\\Revit\\Addins";

        /// <summary>
        /// 获取或设置应用自动更新服务器地址；若设置了该值，则覆盖 .config 与环境变量。
        /// </summary>
        public string UpdateServerUrl { get; set; } = null;

        /// <summary>
        /// 获取或设置拉取包目录时是否过滤包含 'log' 的目录。
        /// </summary>
        public bool FilterLogDirectories { get; set; } = true;

        /// <summary>
        /// 获取或设置 VS Code 外部工具的路径缓存。
        /// </summary>
        public string VsCodePath { get; set; } = null;

        /// <summary>
        /// 获取或设置 PingCode 应用的 Client ID。
        /// </summary>
        public string PingCodeClientId { get; set; } = "wgRVSMOfjwqp";

        /// <summary>
        /// 获取或设置 PingCode 应用的 Client Secret。
        /// </summary>
        public string PingCodeClientSecret { get; set; } = "UMxFmEenlPmQJuDKhguTlwJE";
        /// <summary>
        /// 日志文本查看器
        /// </summary>
        public string LogTxtReader { get; set; } = "LogViewPro";
        
        /// <summary>
        /// 产品日志页的日志等级选择
        /// </summary>
        public string ProductLogLevel { get; set; } = "ERROR";

        /// <summary>
        /// 获取或设置 Jenkins 服务器的基地址。
        /// </summary>
        public string JenkinsBaseUrl { get; set; } = "http://192.168.0.245:8080";

        /// <summary>
        /// 获取或设置 Jenkins 视图名称。
        /// </summary>
        public string JenkinsViewName { get; set; } = "机电项目组";

        /// <summary>
        /// 获取或设置 Jenkins 登录用户名。
        /// </summary>
        public string JenkinsUsername { get; set; } = null;

        /// <summary>
        /// 获取或设置经过 DPAPI 加密保护的 Jenkins 密码。
        /// </summary>
        public string JenkinsPasswordProtected { get; set; } = null;

        /// <summary>
        /// 获取或设置 Git HTTP 代理地址。
        /// </summary>
        public string GitProxyHttpUrl { get; set; } = "http://127.0.0.1:7897";

        /// <summary>
        /// 获取或设置 Git HTTPS 代理地址。
        /// </summary>
        public string GitProxyHttpsUrl { get; set; } = "http://127.0.0.1:7897";

        /// <summary>
        /// 获取或设置 Revit 文件清理工具的自定义扫描目录列表。
        /// </summary>
        public List<string> RevitCleanupCustomDirectories { get; set; } = new List<string>();

        /// <summary>
        /// 获取或设置常用启动项列表。
        /// </summary>
        public List<CommonStartupItem> CommonStartupItems { get; set; } = new List<CommonStartupItem>();

        /// <summary>
        /// 获取或设置常用启动项分组定义。
        /// </summary>
        public List<CommonStartupGroup> CommonStartupGroups { get; set; } = new List<CommonStartupGroup>();

        /// <summary>
        /// 获取或设置常用启动项全局热键显示文本。
        /// </summary>
        public string CommonStartupHotkey { get; set; } = "Ctrl+Q";

        /// <summary>
        /// 获取或设置是否启用索引服务性能分析日志。
        /// </summary>
        public bool EnableIndexServicePerformanceAnalysis { get; set; } = false;

        /// <summary>
        /// 获取或设置是否启用文件传输功能。
        /// </summary>
        public bool EnableLanTransfer { get; set; } = true;

        /// <summary>
        /// 获取或设置文件传输显示名称。
        /// </summary>
        public string LanTransferDisplayName { get; set; } = null;

        /// <summary>
        /// 获取或设置文件传输设备标识。
        /// </summary>
        public string LanTransferDeviceId { get; set; } = null;

        /// <summary>
        /// 获取或设置文件传输收件箱目录。
        /// </summary>
        public string LanTransferInboxPath { get; set; } = null;
    }

    /// <summary>
    /// 表示一个常用启动项。
    /// </summary>
    public class CommonStartupItem
    {
        /// <summary>显示名称（用户可自定义）。</summary>
        public string Name { get; set; }

        /// <summary>可执行文件或脚本的完整路径。</summary>
        public string FullPath { get; set; }

        /// <summary>启动参数（可选）。</summary>
        public string Arguments { get; set; } = "";

        /// <summary>备注说明（可选）。</summary>
        public string Note { get; set; } = "";

        /// <summary>所属分组名称。</summary>
        public string GroupName { get; set; } = "";

        /// <summary>是否收藏。</summary>
        public bool IsFavorite { get; set; }

        /// <summary>排序值。</summary>
        public int Order { get; set; }

        /// <summary>最近启动时间。</summary>
        public DateTime? LastLaunchedAt { get; set; }

        /// <summary>累计启动次数。</summary>
        public int LaunchCount { get; set; }
    }

    /// <summary>
    /// 常用启动项分组定义。
    /// </summary>
    public class CommonStartupGroup
    {
        /// <summary>分组名称。</summary>
        public string Name { get; set; } = "";

        /// <summary>排序值。</summary>
        public int Order { get; set; }
    }

    internal sealed class CommonStartupSettings
    {
        /// <summary>
        /// 获取或设置常用启动项列表。
        /// </summary>
        public List<CommonStartupItem> CommonStartupItems { get; set; } = new List<CommonStartupItem>();

        /// <summary>
        /// 获取或设置常用启动项分组定义列表。
        /// </summary>
        public List<CommonStartupGroup> CommonStartupGroups { get; set; } = new List<CommonStartupGroup>();
    }
}

