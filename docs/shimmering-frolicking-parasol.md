# 文件传输自动接收 + 覆盖健壮性增强方案

## Context

当前局域网文件传输功能，每次接收方都需要在弹窗中点击"接收"才能开始传输。对于信任场景（如团队内部频繁传文件），这很繁琐。需要增加一个"自动接收"勾选项，勾选后所有传入请求自动接受，无需弹窗确认。

同时，当前"静默覆盖"功能存在健壮性缺陷：文件被占用、只读文件、跨卷移动等场景会直接抛异常导致传输失败。需要增强覆盖逻辑的容错能力。

---

## 一、自动接收功能

### 1. AppSettings 增加属性
**文件**: `Services\DataPersistenceService.cs:1109` (在 `LanTransferSilentOverwrite` 之后)

```csharp
public bool LanTransferAutoAccept { get; set; } = false;
```

### 2. SettingsPage 代码后台增加字段和属性
**文件**: `Features\Settings\Views\SettingsPage.xaml.cs`

- 在 `lanTransferSilentOverwrite` 字段后面 (line 46) 增加:
  ```csharp
  private bool lanTransferAutoAccept;
  ```

- 在 `LanTransferSilentOverwrite` 属性后面 (line 191) 增加:
  ```csharp
  public bool LanTransferAutoAccept
  {
      get => lanTransferAutoAccept;
      set => SetProperty(ref lanTransferAutoAccept, value);
  }
  ```

- `LoadSettings()` (line 306 附近) 增加:
  ```csharp
  LanTransferAutoAccept = settings?.LanTransferAutoAccept ?? false;
  ```

- `SaveButton_Click()` (line 480 附近) 增加:
  ```csharp
  settings.LanTransferAutoAccept = LanTransferAutoAccept;
  ```

- `ResetButton_Click()` (line 445 附近) 增加:
  ```csharp
  LanTransferAutoAccept = false;
  ```

### 3. SettingsPage XAML 增加勾选项
**文件**: `Features\Settings\Views\SettingsPage.xaml:134` ("静默覆盖"提示文本之后，`</StackPanel>` 之前)

```xml
<CheckBox Content="自动接收文件（跳过确认）"
          IsChecked="{Binding LanTransferAutoAccept, UpdateSourceTrigger=PropertyChanged}"
          Margin="120,10,0,0"/>
<TextBlock Text="开启后收到的文件传输请求将自动接受，不再弹出确认窗口。建议仅在信任的局域网环境中使用。"
           Foreground="Gray"
           FontSize="11"
           Margin="120,5,0,0"
           TextWrapping="Wrap"/>
```

### 4. LanTransferService 增加属性和逻辑
**文件**: `Features\LanTransfer\Services\LanTransferService.cs`

- 在 `_silentOverwrite` 字段后 (line 46) 增加:
  ```csharp
  private bool _autoAccept;
  ```

- 在 `SilentOverwrite` 属性后增加:
  ```csharp
  public bool AutoAccept
  {
      get => _autoAccept;
      set => SetProperty(ref _autoAccept, value);
  }
  ```

- `ApplySettings()` (line 162) 增加:
  ```csharp
  AutoAccept = settings.LanTransferAutoAccept;
  ```

- `LoadSettingsAndInitialize()` (line 711) 增加:
  ```csharp
  AutoAccept = settings.LanTransferAutoAccept;
  ```

### 5. 修改 ApproveIncomingRequestAsync 跳过确认弹窗
**文件**: `Features\LanTransfer\Services\LanTransferService.cs:854`

在方法开头（`request.SaveDirectory = InboxPath;` 之后）插入自动接收逻辑:

```csharp
if (AutoAccept)
{
    ToastService.ShowToast("自动接收文件", $"{request.SenderLabel} 发送 {request.ItemCount} 项，已自动接收。", "Info");
    request.StatusText = "已自动接收";
    return LanIncomingTransferDecision.Accept(InboxPath, SilentOverwrite);
}
```

保留 Toast 通知让用户知道有文件传入，但不弹窗。`_pendingRequests.Add/Remove` 在自动接收路径中跳过（无需加入待审批列表）。

---

## 二、覆盖健壮性增强

### 当前问题分析

`CommitReceivedItems()` 中的 `DeleteExistingDestination()` → `File.Move()` 链存在以下已知缺陷:

| 问题 | 现象 | 影响 |
|------|------|------|
| 文件被占用 | `File.Delete()` / `Directory.Delete()` 抛 `IOException` | 传输直接失败 |
| 只读文件 | `File.Delete()` 抛 `UnauthorizedAccessException` | 传输直接失败 |
| Delete→Move 非原子 | 删除后移动前另一进程可能创建同名文件 | Move 失败 |
| 跨卷移动 | `Directory.Move()` 不支持跨驱动器 | 传输失败 |
| 部分提交失败 | 循环中某项 Move 失败，已提交项无法回滚 | 部分文件丢失 |

### 改造方案

**文件**: `Features\LanTransfer\Services\LanTransferHostService.cs`

#### 1. 增强 `DeleteExistingDestination` → 重命名为 `TryRemoveExistingDestination`

```csharp
private static bool TryRemoveExistingDestination(string destinationPath, int maxRetries = 3)
{
    for (var attempt = 0; attempt < maxRetries; attempt++)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                var attr = File.GetAttributes(destinationPath);
                if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                    File.SetAttributes(destinationPath, FileAttributes.Normal);
                File.Delete(destinationPath);
            }

            if (Directory.Exists(destinationPath))
            {
                ClearReadOnlyRecursive(destinationPath);
                Directory.Delete(destinationPath, true);
            }

            return true;
        }
        catch (IOException) when (attempt < maxRetries - 1)
        {
            Thread.Sleep(300 * (attempt + 1));
        }
        catch (UnauthorizedAccessException) when (attempt < maxRetries - 1)
        {
            Thread.Sleep(300 * (attempt + 1));
        }
    }

    return false;
}

private static void ClearReadOnlyRecursive(string directoryPath)
{
    foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
    {
        var attr = File.GetAttributes(file);
        if ((attr & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
    }
}
```

#### 2. 增强 `CommitReceivedItems` 整体逻辑

```csharp
private static void CommitReceivedItems(string tempDirectory, string inboxPath, 
    LanTransferRequest request, bool silentOverwrite)
{
    Directory.CreateDirectory(inboxPath);
    var topLevelNames = GetReceiveTopLevelNames(request, tempDirectory);
    var committed = new List<(string source, string destination)>();

    foreach (var topLevelName in topLevelNames)
    {
        var sourcePath = Path.Combine(tempDirectory, topLevelName);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            continue;

        var destinationPath = Path.Combine(inboxPath, topLevelName);
        var isDirectory = Directory.Exists(sourcePath);

        try
        {
            if (silentOverwrite)
            {
                if ((File.Exists(destinationPath) || Directory.Exists(destinationPath))
                    && !TryRemoveExistingDestination(destinationPath))
                {
                    // 删除失败，回退到重命名策略避免丢失新文件
                    destinationPath = GetNonConflictingPath(destinationPath, isDirectory);
                }
            }
            else
            {
                destinationPath = GetNonConflictingPath(destinationPath, isDirectory);
            }

            MoveWithCrossVolumeFallback(sourcePath, destinationPath, isDirectory);
            committed.Add((sourcePath, destinationPath));
        }
        catch (Exception)
        {
            // 单项失败不中断整批；已提交项保留，失败项保留在 temp 目录
            // temp 目录不会被删除，用户可手动取回
            continue;
        }
    }

    // 仅当所有项都已提交时才清理 temp 目录
    if (committed.Count == topLevelNames.Count)
    {
        TryDeleteDirectory(tempDirectory);
    }
    // 否则 temp 目录保留，里面残留未能移动的文件
}
```

#### 3. 新增 `MoveWithCrossVolumeFallback` 处理跨卷场景

```csharp
private static void MoveWithCrossVolumeFallback(string source, string destination, bool isDirectory)
{
    try
    {
        if (isDirectory)
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }
    catch (IOException)
    {
        // File.Move / Directory.Move 跨卷会抛 IOException，回退到 Copy+Delete
        if (isDirectory)
        {
            CopyDirectoryRecursive(source, destination);
            TryDeleteDirectory(source);
        }
        else
        {
            File.Copy(source, destination, true);
            TryDeleteFile(source);
        }
    }
}

private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
{
    Directory.CreateDirectory(destinationDir);

    foreach (var file in Directory.GetFiles(sourceDir))
    {
        var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
        File.Copy(file, destFile, true);
    }

    foreach (var dir in Directory.GetDirectories(sourceDir))
    {
        var destSubDir = Path.Combine(destinationDir, Path.GetFileName(dir));
        CopyDirectoryRecursive(dir, destSubDir);
    }
}

private static void TryDeleteFile(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}
```

---

## 涉及文件清单

| 文件 | 改动 |
|------|------|
| `Services\DataPersistenceService.cs` | 增加 `LanTransferAutoAccept` 属性 |
| `Features\Settings\Views\SettingsPage.xaml` | 增加自动接收 CheckBox + 说明文字 |
| `Features\Settings\Views\SettingsPage.xaml.cs` | 增加字段、属性、Load/Save/Reset 逻辑 |
| `Features\LanTransfer\Services\LanTransferService.cs` | 增加 `AutoAccept` 属性，修改 `ApproveIncomingRequestAsync` |
| `Features\LanTransfer\Services\LanTransferHostService.cs` | 增强 `DeleteExistingDestination`、`CommitReceivedItems`，增加跨卷移动支持 |

## 验证方案

1. **自动接收功能**
   - 在设置中勾选"自动接收文件"，保存设置
   - 从另一台机器发送文件，验证接收方不弹确认窗口，Toast 通知正常显示，文件正确到达收件箱
   - 取消勾选，验证恢复为弹窗确认模式

2. **覆盖健壮性**
   - 发送同名文件到已有同名文件的收件箱，验证覆盖成功
   - 用文本编辑器打开收件箱中的文件（制造文件锁），再发送同名文件，验证重试后覆盖成功或自动降级为重命名
   - 将目标文件设为只读，验证可以被正常覆盖
   - 如果 temp 目录和收件箱在不同驱动器，验证跨卷移动正常工作
   - 发送多个文件，中间一个被锁定，验证其他文件不受影响
