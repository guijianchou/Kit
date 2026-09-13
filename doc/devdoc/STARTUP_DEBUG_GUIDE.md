# Kit 启动性能诊断指南

## 新版本位置
`C:\Users\Zen\Repos\Codings\Kit\src\runner\x64\Debug\Kit.exe`

## 已添加的性能日志

新版本在启动过程中会记录详细的计时信息到日志文件：
- 位置：`%LOCALAPPDATA%\Microsoft\PowerToys\Kit\Logs\Kit-Runner.log`

每个启动阶段都会记录时间戳：
- DPI Awareness（DPI 感知）
- Trace Provider（跟踪提供程序）
- Load Settings（加载设置）
- Tray Icon（托盘图标）
- Update Worker（更新检查）
- Quick Access Update（快速访问热键）
- Keyboard Hook（键盘钩子）
- Module Loaded: XXX（每个模块加载）
- Module Enable: XXX（每个模块启用）
- Settings Window（设置窗口打开，如果有）

## 如何诊断启动慢的问题

1. **运行新版本 Kit.exe**
   ```
   C:\Users\Zen\Repos\Codings\Kit\src\runner\x64\Debug\Kit.exe
   ```

2. **查看日志文件**
   ```powershell
   notepad "%LOCALAPPDATA%\Microsoft\PowerToys\Kit\Logs\Kit-Runner.log"
   ```

3. **搜索 "STARTUP_TIMING" 找到所有计时点**
   - 查看哪个阶段耗时最长
   - 正常情况下总启动时间应该在 300-500ms 左右

4. **使用诊断脚本**
   ```powershell
   .\DiagnoseTrayIcon.ps1
   ```
   这会显示：
   - Kit 是否正在运行
   - 托盘图标窗口是否存在
   - 最近的启动计时信息

## 托盘图标问题

### 问题：图标不见了

可能的原因：
1. **Windows 任务栏隐藏了图标** - 最常见！
   - 打开：任务栏右键 → 任务栏设置 → "选择哪些图标显示在任务栏上"
   - 或者：点击任务栏右下角的 "^" 箭头，图标可能在溢出区域

2. **设置中关闭了托盘图标**
   - 检查：`%LOCALAPPDATA%\Microsoft\PowerToys\settings.json`
   - 确保：`"show_system_tray_icon": true`

3. **图标创建失败**
   - 查看日志文件中是否有 "Tray Icon" 计时记录
   - 如果没有，说明创建过程出错了

### 验证图标是否真的存在

运行诊断脚本会检查：
- Kit.exe 进程是否运行
- PToyTrayIconWindow 窗口是否存在
- 如果窗口存在但看不到图标，就是 Windows 隐藏了

## 预期性能

应用了优化后的预期启动时间：
- **Quick Access 延迟加载**: 节省 200-400ms（首次 Win+Space 时才启动）
- **GPO 检查跳过**: 节省 10-20ms
- **元数据缓存**: 节省 2-5ms
- **视频会议清理优化**: 节省 10-20ms（仅首次运行，后续跳过）

**总预期**: 从原来的 500-1000ms 降低到 300-500ms

## 下一步

如果启动仍然很慢：
1. 运行 Kit.exe
2. 查看日志文件中的 STARTUP_TIMING 记录
3. 找出哪个阶段耗时异常
4. 报告具体的耗时数据（例如："Tray Icon 耗时 800ms"）
