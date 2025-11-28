放置应用程序图标文件：

1) 将多尺寸 .ico 文件命名为 App.ico，并放到本目录。
   推荐包含 16x16, 24x24, 32x32, 48x48, 64x64, 128x128, 256x256。

2) 当前工程已设置：
   - EXE图标：PackageManager.csproj 的 <ApplicationIcon> 指向 Assets\Icons\App.ico。
   - 任务栏图标：MainWindow.xaml 的 Window.Icon="/Assets/Icons/App.ico"。

2.1) 一键生成 App.ico：
    - 将你的 PNG 图标保存为：Assets\Icons\source.png
    - 运行脚本（PowerShell）：
      powershell -ExecutionPolicy Bypass -File e:\PackageManager\scripts\GenerateIcon.ps1 -InputPng e:\PackageManager\Assets\Icons\source.png -OutputIco e:\PackageManager\Assets\Icons\App.ico
    - 脚本会生成包含 256/128/64/48/32/24/16 多尺寸的 ICO。

3) 制作 .ico 的方法：
   - 使用图标工具（如 IcoMoon、GIMP、RealWorld Icon Editor）。
   - 或在命令行/网站将 PNG 转换为 ICO，确保包含以上多尺寸。

4) 注意：请务必提供 .ico 文件；若缺失，编译不会嵌入图标，运行时也无法显示任务栏图标。

4.1) 若脚本失败：确保系统可加载 System.Drawing，并检查 source.png 路径正确。

5) 如需更换文件名/路径，请同时更新 csproj 的 <ApplicationIcon> 和 MainWindow.xaml 的 Window.Icon。
