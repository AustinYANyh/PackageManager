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
        public string LocalPath { get; set; }
        public string UploadPackageName { get; set; }
        public string SelectedExecutableVersion { get; set; }
        public string ExecutablePath { get; set; }
        public bool IsDebugMode { get; set; }
        
        public List<ApplicationVersion> AvailableExecutableVersions { get; set; } = new List<ApplicationVersion>();
        public Dictionary<string, string> VersionLocalPaths { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 主界面状态数据模型
    /// </summary>
    public class MainWindowStateData
    {
        public List<PackageStateData> Packages { get; set; } = new List<PackageStateData>();
        public DateTime LastSaved { get; set; } = DateTime.Now;
        
        // 主界面筛选条件集合用于精确还原各列筛选
        public List<FilterCondition> PackageGridFilterConditions { get; set; } = new List<FilterCondition>();
    }

    /// <summary>
    /// 数据持久化服务，用于保存和加载应用程序查询结果和主界面状态
    /// </summary>
    public class DataPersistenceService
    {
        private readonly string _dataFilePath;

        private readonly string _mainWindowStateFilePath;

        private readonly string _settingsFilePath;

        private readonly string _packagesFilePath;
        private readonly string _finalizePackagesFilePath;

        private readonly string _appFolder;

        private readonly JsonSerializerSettings _jsonSettings;

        private List<FilterCondition> _lastGridFilterConditions;

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
            _packagesFilePath = Path.Combine(_appFolder, "packages.json");
            _finalizePackagesFilePath = Path.Combine(_appFolder, "finalize_packages.json");

            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public class PackageConfigItem
        {
            public string ProductName { get; set; }

            public string FtpServerPath { get; set; }

            public string FinalizeFtpServerPath { get; set; }
            
            public string LocalPath { get; set; }

            public bool SupportsConfigOps { get; set; } = true;
        }

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

        public string GetPackagesConfigPath() => _packagesFilePath;

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

        public string GetFinalizePackagesConfigPath() => _finalizePackagesFilePath;

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
        /// <returns>是否保存成功（更新缓存成功）</returns>
        public bool SaveMainWindowFilterCondition(ObservableCollection<FilterCondition> filterConditions)
        {
            try
            {
                _lastGridFilterConditions = filterConditions?.ToList() ?? new List<FilterCondition>();
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
        public bool SaveSettings(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, _jsonSettings);
                File.WriteAllText(_settingsFilePath, json);
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
                return settings ?? new AppSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
                return new AppSettings();
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
    }

    /// <summary>
    /// 应用程序设置数据模型
    /// </summary>
    public class AppSettings
    {
        public bool ProgramEntryWithG { get; set; } = true;
        
        public string AddinPath { get; set; } = @"C:\\ProgramData\\Autodesk\\Revit\\Addins";

        // 优先用于应用自动更新：若设置了该值，则覆盖 .config 与环境变量
        public string UpdateServerUrl { get; set; } = null;

        // 拉取包目录时是否过滤包含 'log' 的目录
        public bool FilterLogDirectories { get; set; } = true;

        // 外部工具路径缓存
        public string VsCodePath { get; set; } = null;
        
        public string PingCodeClientId { get; set; } = "wgRVSMOfjwqp";

        public string PingCodeClientSecret { get; set; } = "UMxFmEenlPmQJuDKhguTlwJE";
        /// <summary>
        /// 日志文本查看器
        /// </summary>
        public string LogTxtReader { get; set; } = "LogViewPro";
    }
}
