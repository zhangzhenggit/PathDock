# PathDock

Windows 目录快捷工具。固定常用目录、发现最近目录，并根据当前窗口选择打开、粘贴路径或切换 PowerShell 工作目录。

## 使用

下载并解压发布包，双击 PathDock.exe。默认精简包需要安装 .NET 8 Desktop Runtime。PowerShell 集成需要 PowerShell 7。

- 点击屏幕边缘的胶囊入口展开；3 秒无操作自动收起，点击面板外部经过短暂缓冲后收起。
- Ctrl + Alt + 空格展开或收起；顶部图钉保持面板展开。
- 固定区显示名称，悬停查看完整路径；最近区显示完整路径。
- 直接拖入文件夹。右键可打开、粘贴、收藏、编辑和调整顺序。
- 顶部更多菜单提供点击行为、设置、收起和退出。
- 托盘菜单提供开机自启、设置和退出，双击托盘图标也能展开。

详细操作见 [使用说明](docs/USAGE.md)。

## 文件对话框跟随

在设置中开启“文件对话框跟随资源管理器”。在资源管理器浏览目录后，新打开标准文件对话框或返回原对话框时，自动进入该窗口活动标签页的目录，保留文件名和文件类型筛选。浏览记录在两分钟内有效。

此选项默认关闭。支持同等权限的 Windows 标准打开、保存和文件夹选择框。旧式或自定义对话框、虚拟位置和无法确认的目标会跳过。返回过程中检测到继续输入、焦点变化或请求过期时取消本次跟随。

通过窗口事件触发检查，使用单个后台线程和限长队列。该选项与 PowerShell 集成独立，配置保存在当前账户的用户设置文件中。

## 自动动作与 PowerShell

自动模式会在识别到可编辑文本框或 Codex 输入区时粘贴路径，不发送回车；已有文字保留，在当前光标处插入。非输入区域打开目录，无法确认的终端显示操作菜单。粘贴使用系统剪贴板；管理员窗口可改用复制路径。

如需切换当前 PowerShell 目录，在设置中勾选“在 PowerShell 中切换目录”，然后新开 PowerShell 7 标签。空闲时标签标题会出现短标记 [PD:…]，用于识别会话。集成保留现有提示符、按键绑定及原始 ReadLine 实现，并备份用户 profile。

请求会校验会话、输入代次、时效及前台窗口。状态变化时取消，不长期排队。终端标题被固定或覆盖时，可右键明确选择会话。

## 数据与开机自启

收藏和偏好位于 %LOCALAPPDATA%\PathDock。升级不清除这些数据。

托盘右键菜单可启用“开机自启”，仅影响当前 Windows 账户，默认关闭。未固定面板时，自启只显示边缘按钮。

启用自启或 PowerShell 集成后，请保持程序目录固定；移动前先关闭相关选项，再从新目录启用。程序不修改系统执行策略或 Terminal 配置。

## 开发与构建

需要 .NET 8 SDK、Windows 桌面目标组件及 PowerShell 7。没有第三方 NuGet 依赖。

    .\build.ps1
    .\build.ps1 -Package
    .\build.ps1 -Package -Run

默认生成精简版，需要目标电脑安装 .NET 8 Desktop Runtime（与包架构一致）。如需携带运行环境、无需预装，显式添加 -SelfContained，包体会明显增大。

默认构建输出到 artifacts/publish；加入 -Package 生成 releases 下的 ZIP 和 SHA-256；加入 -Run 备份并更新 dist，然后重启。更新失败会尝试回退到旧文件。ARM64 可指定 -Runtime win-arm64。

Start-PathDock.cmd 优先运行 dist/PathDock.exe，首次开发启动时会调用构建脚本。

## 验证

    pwsh -NoProfile -File .\Tests\Run-Tests.ps1

验证包括配置和收藏、自动识别分类、自启注册、PowerShell 配置安装/还原、真实请求处理、双屏定位、边缘按钮点击命中、收起菜单及动画状态。测试使用隔离目录和注册表测试键，保留真实用户配置。结果写入 artifacts/test-results。

预览和诊断代码放在 Tests 中，只在 PathDockDiagnostics=true 时编译。正式包不包含自测、预览或运动测试入口。测试构建可额外导出界面：

    .\artifacts\diagnostics\PathDock.exe --render-preview .\artifacts\test-results\panel.png
    .\artifacts\diagnostics\PathDock.exe --render-settings .\artifacts\test-results\settings.png

自动插入依赖目标程序的可访问性及界面特征，无法保证覆盖所有 Codex 版本、远程终端或自定义编辑模式。真实 Codex/交互式终端未完成自动化端到端输入验收；不确定时保留手动操作入口。

## 项目结构

- src/：正式应用代码。
- Integration/：PowerShell 适配、历史读取和配置管理。
- Assets/：图标资源及生成脚本。
- Tests/：隔离测试、诊断构建及渲染工具。
- docs/：用户说明和发布检查记录。
- bin/、obj/、artifacts/：编译中间文件、诊断输出和检查记录，可清理后重新生成。
- dist/、releases/：当前运行文件和发布包。
