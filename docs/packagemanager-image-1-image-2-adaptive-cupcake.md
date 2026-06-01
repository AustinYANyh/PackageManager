# DevKit 新交互方案：仪表盘首页 + 分组导航 + Bug 修复

## Context

DevKit（原 PackageManager）经过领域重构后，已从单一的拉包工具发展为拥有 10+ 功能的开发者工作台。但目前存在两个问题：
1. **Bug**：路径设置页打不开，报错"值不能为 null。参数名: source"
2. **UX**：启动后仍直接进入拉包界面（产品分类），不符合 DevKit 多功能定位

本方案：修复 Bug、新增仪表盘首页、侧栏分组导航、通用工具提升到仪表盘。

---

## 一、修复路径设置 Bug

**根因**：`Shell/ToolRegistration.cs:53` 向 `LocalPathSettingsPage` 传了 `null`：
```csharp
Factory = () => new LocalPathSettingsPage(persistence, null)  // ← null!
```
`LocalPathSettingsPage` 构造函数（line 45）对 `packages.OrderBy(...)` 调用抛出 `ArgumentNullException`。

**修复方案**：
1. `MainWindow.xaml.cs` 构造函数中注册自身到 ServiceLocator（在 `ToolRegistration.RegisterAll` 之前）
2. `ToolRegistration.cs` 的工厂 lambda 中通过 `ServiceLocator.Resolve<MainWindow>().Packages` 延迟获取

**改动文件**：
- `MainWindow.xaml.cs:~65` — 添加 `ServiceLocator.Register(this);`
- `Shell/ToolRegistration.cs:53` — `null` → `ServiceLocator.Resolve<MainWindow>().Packages`

---

## 二、新建 DashboardPage 仪表盘首页

**位置**：`Features/Dashboard/Views/DashboardPage.xaml` + `.xaml.cs`

### 布局设计

```
┌─────────────────────────────────────────────────────┐
│  欢迎使用 DevKit                                      │
│  集成式开发者工具箱                                     │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ── 包管理 ──────────────────────────────────────     │
│  [产品分类] [路径设置] [产品管理] [更新日志]              │
│                                                     │
│  ── 项目与协作 ──────────────────────────────────     │
│  [看板统计] [文件传输]                                  │
│                                                     │
│  ── 开发工具 ──────────────────────────────────      │
│  [插件管理] [CSV加解密] [清理rvt] [解除占用]            │
│  [编译顺序] [Git代理]  [VCS映射]                       │
│                                                     │
│  ── 日志 ──────────────────────────────────────      │
│  [产品日志] [软件日志]                                  │
│                                                     │
│  ── 设置 ──────────────────────────────────────      │
│  [软件设置]                                           │
└─────────────────────────────────────────────────────┘
```

每个卡片为 Border（与现有 PackagesHomePage 卡片样式一致：`Background="#06000000"`, `CornerRadius="8"`, `BorderBrush="#12000000"`），内含 Glyph 图标 + 标题 + 简短描述。点击导航到对应页面。

**导航目标页的卡片**（点击调用 `NavigationService.NavigateTo(key)`）：
- 产品分类 → `packages-home`（新注册的工具页 key）
- 路径设置 → `local-path-settings`
- 产品管理 → `package-config`
- 更新日志 → `changelog`
- 看板统计 → `kanban`
- 文件传输 → `lan-transfer`
- 插件管理 → `plugin-management`
- 产品日志 → `product-logs`
- 软件日志 → `log-viewer`
- 软件设置 → `settings`

**弹窗工具卡片**（点击直接打开窗口，不走导航）：
- CSV加解密 → 打开 `CsvCryptoWindow`
- 清理rvt → 启动 `MftScanner.exe --window cleanup`
- 解除占用 → 打开 `UnlockFilesWindow`
- 编译顺序 → 打开 `SlnUpdateWindow`
- Git代理 → 调用 `GitProxyService.ToggleAsync()`
- VCS映射 → 调用 VCS 映射逻辑

### DashboardPage.xaml.cs

代码后置包含：
- 卡片点击处理：NavigationService 导航 或 直接打开工具窗口
- 通用工具的打开逻辑复用自 PackagesHomePage 中的现有实现

为避免重复代码，抽取 `DevToolLauncher` 静态类集中管理通用工具启动逻辑。

**新建文件**：
- `Features/Dashboard/Views/DashboardPage.xaml`
- `Features/Dashboard/Views/DashboardPage.xaml.cs`
- `Features/DevTools/DevToolLauncher.cs`

---

## 三、侧栏分组导航

### NavigationActionItem 扩展

在 `Views/NavigationPanel.xaml.cs` 的 `NavigationActionItem` 类添加：
```csharp
public bool IsGroupHeader { get; set; }
```

### NavigationPanel.xaml 模板改造

在现有 `DataTemplate` 中使用 `DataTrigger`：
- `IsGroupHeader=False`（默认）→ 显示正常的图标+文字行
- `IsGroupHeader=True` → 显示为灰色小标题文字，无图标，不可选中

在 `ListBoxItem` 的 `ControlTemplate` 中添加 DataTrigger，使分组头 `IsEnabled=False`、无选中高亮、无悬停效果。

### ToolPageDescriptor 扩展

`Shell/ToolPageDescriptor.cs` 添加：
```csharp
public string Group { get; set; }
```

### 分组侧栏结构

| 分组 | 导航项 |
|------|--------|
| — | 仪表盘 (Home) |
| **包管理** | 产品分类、路径设置、产品管理、更新日志 |
| **项目与协作** | 看板统计、文件传输 |
| **开发工具** | 插件管理 |
| **日志** | 产品日志、软件日志 |
| — | 软件设置 (底部独立) |

`NavigationPanel_Loaded` 重写逻辑：
1. "仪表盘" 作为首项，调用 `NavigateHome()`
2. 按 `ToolPageDescriptor.Group` 分组插入组头 + 各工具导航项
3. 默认选中"仪表盘"
4. `SelectionChanged` 中跳过 `IsGroupHeader=true` 项

### ToolRegistration.cs 更新

注册所有工具时赋值 `Group`：
```
Group = "包管理":     packages-home, local-path-settings, package-config, changelog
Group = "项目与协作": kanban, lan-transfer
Group = "开发工具":   plugin-management
Group = "日志":       product-logs, log-viewer
Group = "设置":       settings
```

**重要**：新增 `packages-home` 描述符（Key: `"packages-home"`, DisplayName: `"产品分类"`），其 Factory 返回 `MainWindow.GetOrCreateHomePage()`（缓存单例）。

**改动文件**：
- `Shell/ToolPageDescriptor.cs` — 添加 `Group` 属性
- `Shell/ToolRegistration.cs` — 全部描述符赋 Group；新增 `packages-home`
- `Views/NavigationPanel.xaml` — DataTemplate 添加 DataTrigger；ListBoxItem 样式添加分组头触发器
- `Views/NavigationPanel.xaml.cs` — `NavigationActionItem` 添加 `IsGroupHeader`；重写 `NavigationPanel_Loaded` 构建分组列表

---

## 四、NavigationService + MainWindow 适配

### NavigationService.cs

- `NavigateHome()` 的 `Navigated?.Invoke("产品分类")` 改为 `Navigated?.Invoke("仪表盘")`
- Home 页工厂产出 `DashboardPage`（不再是 `PackagesHomePage`）

### MainWindow.xaml.cs

构造函数改动（按顺序）：
```
1. ServiceLocator.Register(this);                          // NEW
2. var registry = new ToolRegistry();
3. ToolRegistration.RegisterAll(registry);                 // packages-home 已注册
4. var navService = new NavigationService(CentralFrame, registry);
5. navService.SetHomePageFactory(() => new DashboardPage()); // 改为 DashboardPage
6. ServiceLocator.Register(navService);
7. DataContext = this;
8. navService.NavigateHome();                               // 启动显示仪表盘
```

新增方法：
```csharp
internal PackagesHomePage GetOrCreateHomePage()
{
    if (_homePage == null)
        _homePage = new PackagesHomePage { DataContext = this };
    return _homePage;
}
```

需要修改的现有调用：
- `NavigateHome()` 私有方法（line 371-373）：保持不变，NavigateHome() 现在回到仪表盘
- `ApplyCategorySelection()` (line 1062)：从 `NavigateHome()` 改为 `ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home")`
- `ReloadPackagesFromConfig()` (line 856)：同上
- `GetPackageDataGrid()` (line 385-387)：保持不变，仍通过 `_homePage?.PackageGrid` 访问

---

## 五、DevToolLauncher 抽取

**位置**：`Features/DevTools/DevToolLauncher.cs`

将 `PackagesHomePage.xaml.cs` 中以下 6 个通用工具的启动逻辑抽取为静态方法：

```csharp
public static class DevToolLauncher
{
    public static void OpenCsvCrypto(Window owner);
    public static void OpenRevitFileCleanup();
    public static void OpenUnlockFiles(Window owner);
    public static void OpenSlnUpdate(Window owner);
    public static async Task ToggleGitProxy(Action<string> statusCallback);
    public static void OpenVcsMapping();
}
```

- `PackagesHomePage.xaml.cs` 中对应的 Click 处理方法改为调用 `DevToolLauncher` 
- `DashboardPage.xaml.cs` 中的卡片点击同样调用 `DevToolLauncher`

---

## 六、完整文件清单

### 新建文件（3 个）
| 文件 | 说明 |
|------|------|
| `Features/Dashboard/Views/DashboardPage.xaml` | 仪表盘首页 XAML |
| `Features/Dashboard/Views/DashboardPage.xaml.cs` | 仪表盘代码后置 |
| `Features/DevTools/DevToolLauncher.cs` | 通用工具启动器 |

### 修改文件（7 个）
| 文件 | 改动要点 |
|------|----------|
| `Shell/ToolPageDescriptor.cs` | 添加 `Group` 属性 |
| `Shell/ToolRegistration.cs` | 所有描述符赋 Group；新增 `packages-home`；修复 null bug |
| `Shell/NavigationService.cs` | `NavigateHome()` 事件名改为 "仪表盘" |
| `MainWindow.xaml.cs` | 注册 this 到 ServiceLocator；SetHomePageFactory → DashboardPage；添加 GetOrCreateHomePage；ApplyCategorySelection/ReloadPackagesFromConfig 导航到 packages-home |
| `Views/NavigationPanel.xaml` | DataTemplate 添加分组头 DataTrigger；ListBoxItem 样式添加分组头不可选触发器 |
| `Views/NavigationPanel.xaml.cs` | NavigationActionItem 添加 IsGroupHeader；NavigationPanel_Loaded 重写为分组构建 |
| `Features/PackageManagement/Views/PackagesHomePage.xaml.cs` | 通用工具 Click 方法改为委托 DevToolLauncher |

---

## 七、验证方案

1. **Bug 修复**：启动应用 → 点击侧栏"路径设置" → 应正常打开页面，不再报 null 错误
2. **仪表盘首页**：启动应用 → 默认显示仪表盘而非产品列表 → 卡片图标/文字/分组正确显示
3. **卡片导航**：点击仪表盘各卡片 → 正确导航到对应页面或弹出工具窗口
4. **分组侧栏**：左侧导航按分组显示，分组头不可点击/选中，功能项正常点击导航
5. **产品分类兼容**：从仪表盘点"产品分类"卡片或侧栏"产品分类" → 进入包管理页 → 操作正常（选版本、下载、定版）
6. **分类切换**：左侧曾有的分类树切换功能 → 仍自动回到产品分类页面（非仪表盘）
7. **回退到仪表盘**：在任意工具页面时，点侧栏"仪表盘" → 回到仪表盘首页
8. **通用工具**：仪表盘上的 CSV加解密、清理rvt 等卡片 → 点击后弹出对应工具窗口，且 PackagesHomePage 上同名按钮仍可用
