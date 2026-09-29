# 系统集成检查（Windows 0.6.0）

本次检查覆盖 Windows 接收端、Core、Linux 采集端、安装打包和 GitHub Actions。判断依据是公开的系统 API、协议和工具契约，不将自定义业务规则或兼容旧配置本身视为“非正式方案”。

| 集成 | 检查结果及处理 | 公开依据 |
| --- | --- | --- |
| Windows 通知 | 将仅发送托盘气泡的 ShowBalloonTip 替换为 Windows App SDK AppNotificationManager；注册 COM 激活，保留通知历史，卸载时注销。气泡 API 本身是正式 WinForms API，但不能满足本项目对通知中心留存的要求。 | [微软 .NET 应用通知](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet) |
| Windows 主题 | 移除对 AppsUseLightTheme 注册表值的依赖，改用 UISettings.GetColorValue 和 ColorValuesChanged；保留 WPF 配色和公开 DwmSetWindowAttribute。 | [微软 Win32 深浅主题](https://learn.microsoft.com/windows/apps/desktop/modernize/ui/apply-windows-themes) |
| Windows 托盘、窗口、退出 | 保留 NotifyIcon、WPF SystemCommands、公开窗口消息和 Restart Manager 会话关闭消息；托盘图标与消息通知各司其职。 | [NotifyIcon](https://learn.microsoft.com/dotnet/api/system.windows.forms.notifyicon)、[WM_ENDSESSION](https://learn.microsoft.com/windows/win32/shutdown/wm-endsession) |
| 登录启动 | 保留当前用户 Run 键。这是 Win32 正式支持的登录启动机制，不是未公开注册表技巧。 | [Run and RunOnce Registry Keys](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys) |
| SSH 与主机确认 | 保留 Windows OpenSSH、StrictHostKeyChecking、known_hosts 和 SSH_ASKPASS；不跳过主机校验，不模拟输入。命名管道只传递用户确认。 | [OpenSSH ssh](https://man.openbsd.org/ssh)、[ssh_config](https://man.openbsd.org/ssh_config) |
| Linux 托盘采集 | 保留 StatusNotifierItem/Watcher 与 D-Bus Properties 接口；gdbus 和 dbus-monitor 是公开工具，GVariant 和 profile 输出均有文档。保留输出限制、超时及采样测试。 | [StatusNotifierItem](https://www.freedesktop.org/wiki/Specifications/StatusNotifierItem/)、[GVariant 文本格式](https://docs.gtk.org/glib/gvariant-text-format.html)、[dbus-monitor](https://dbus.freedesktop.org/doc/dbus-monitor.1.html) |
| Linux 主题图标 | 保留 GTK IconTheme / GdkPixbuf 公开 API；缺少可选依赖时不伪造图像，按协议支持降级为名称/占位图。 | [GTK IconTheme](https://docs.gtk.org/gtk3/class.IconTheme.html) |
| GitHub 更新 | 保留官方 releases/latest 重定向与 release 附件链接；不抓取 HTML、不调用第三方镜像、不需要令牌。独立校验 SHA-256 与大小。 | [GitHub Linking to releases](https://docs.github.com/repositories/releasing-projects-on-github/linking-to-releases) |
| 工作日日历 | 保留政府公布的年度安排；没有数据的年份明确报错，不猜测调休。 | [日历来源](china-work-calendar.md) |
| 安装和发布 | 保留 Inno Setup 与 GitHub Actions 自动测试、三架构打包和 Release；Windows App SDK 使用官方 NuGet 组件随应用部署。 | [Windows App SDK 自包含部署](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) |

托盘闪烁判断、图标匹配、重复提醒和 JSON Lines 是本项目的业务逻辑，不是应用厂商的未读消息 API；通知只描述实际观察到的托盘状态，不保证获知真实消息内容或未读数。

通知使用 Windows App SDK 2.5.1 稳定版所对应的 Foundation 2.3.12、InteractiveExperiences 2.1.9 与 Runtime 2.5.1。组件包的部署文件缺少通知注册需要的 `Microsoft.WindowsAppRuntime.Insights.Resource.dll`，构建时从同版本官方 Runtime NuGet 内、对应架构的 MSIX 中取出原始文件随程序部署；不修改 DLL、不依赖开发机已安装的 App Runtime。测试实际加载该文件，防止“能编译、不能注册”的缺包回归。

## 验证

- `dotnet run --project windows/VMNotify.Tests -c Release`：现有协议、SSH、规则、调度、更新校验和图标历史回归。
- `dotnet run --project windows/VMNotify -c Release -- --preview <目录>`：深浅主题、布局与原生窗口行为检查。
- `scripts/test-notifications.ps1 -PublishDir <发布目录> -OutputDirectory <测试目录>`：在隔离副本验证真实 App SDK 载荷生成、中文/XML 转义、唯一标签、到期时间及原生依赖，CI 自动运行。
- 交互桌面额外传入 `-LiveNotificationCenter`：静默发送两条测试通知，发送进程退出后从新进程查询系统通知历史，通过系统 COM 注册实际调用通知激活回调，再清理测试注册；CI 无交互桌面时不宣称完成系统留存验证。
- 点击通知的实际桌面行为、用户关闭通知权限及勿扰模式需在交互桌面验证，不能由载荷测试代替。
