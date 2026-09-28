# VMNotify

将虚拟机内的应用消息提醒转发到宿主机，让你无需切换窗口，也能及时获知新消息。

Forward application attention events from Linux virtual machines to your Windows desktop.

## 首版范围

- **Linux 采集端：Rust**。通过桌面会话 D-Bus 检测受支持应用，输出有版本号的 JSON Lines 事件。
- **Windows 接收端：C# / .NET 10 WPF**。参照 WSL Settings 的分栏设置界面，支持托盘通知、SSH 重连、应用开关、配置保存、可选登录启动。
- **系统主题与滚动**：跟随 Windows 应用浅色/深色主题，运行中自动切换；滚动条默认隐藏，滚动时显示细条，停止约 1 秒后淡出，并且不挤占内容宽度。
- **窗口与图标**：一体化标题栏保留最小化、最大化/还原、关闭按钮，以及原生拖动、双击最大化和边缘缩放。程序、安装包、任务栏、标题栏和托盘使用同一套 16–256 像素多分辨率图标。关闭窗口后继续在托盘运行。
- **应用发现**：每约 5 秒扫描受支持应用；向宿主机提供已安装/正在监听的应用列表。首次运行默认不选择任何应用，用户从识别列表中主动开启转发；后续保留已保存的选择，新发现的应用不会自动开启。
- **当前内置适配器只有蓝信**，依据托盘连续闪烁判断“需要关注”，不读取聊天内容，也不统计消息条数。支持其他应用需要适配与验证，不能因为应用出现在进程列表就保证能转发。
- Windows x86、x64、ARM64 各提供 EXE 安装程序和 Portable ZIP，均不包含 .NET 运行时。需要预先安装与包架构一致的 **.NET Desktop Runtime 10.0**（建议最新 10.0 补丁版），以及 Windows OpenSSH 客户端。
- Linux 提供 x86_64、aarch64 静态 musl 构建。现场验证以麒麟 V10 SP1 / UKUI / x86_64 为准，其他桌面需测试。

## 工作方式

```text
Linux: 应用注册表 → 安装/托盘发现 → 独立检测状态 → JSON Lines
                                                        │ SSH stdout
Windows: 设置/应用选择 ← 应用清单 ← 事件读取与去重 ←───────┘
                    └──────────────→ Windows 托盘通知
```

默认以普通桌面用户通过 SSH 启动采集进程，不额外开放端口、不需要 sudo。SSH 使用密钥认证及严格主机指纹检查。stdout 只传协议，诊断写 stderr。应用发现任务与事件监听分开，队列/行长度/应用数均有界。

## 使用

Windows 客户端版本以 `windows/VERSION` 为准；“关于”页面显示当前版本，“关于”还显示架构和 Git 构建标识。更新记录见 `CHANGELOG.md`。

“关于”支持手动检查 GitHub 最新正式 Release、下载对应程序架构的安装包、启动安装向导和删除已下载文件。首次打开不会自动联网检查。下载保存于系统当前用户的 `%LOCALAPPDATA%\VMNotify\Updates`，退出后仍可管理。下载完成和启动安装前均校验 GitHub Release 提供的 SHA-256；这是完整性校验，不是代码签名。未发布 Release、缺少对应架构附件、网络错误均会显示具体提示。

发布 Windows 更新时，修改 `windows/VERSION`（递增的 `主.次.补丁` 稳定版本号）并更新 `CHANGELOG.md`，将提交推送或合并到 `main`。GitHub Actions 检测到版本变化后，自动运行测试，构建 Windows x86/x64/ARM64 安装版和便携版、Linux x64/ARM64 采集端，校验附件，再创建 `v版本号` Release。附件先上传至草稿，全部成功后才公开；应用内更新会识别对应架构的安装包。

普通代码提交仍执行 CI，但不发布；PR 不发布。失败后可重跑工作流，或在 `main` 手动运行以补发当前版本。已公开的版本不会覆盖，同名标签指向不同提交时会停止；并发发布不会将较旧版本设为最新。发布使用仓库自带 `GITHUB_TOKEN`，无需配置个人令牌，仓库组织策略需允许发布任务使用 `contents: write`。Linux 代理版本取自 `agent/Cargo.toml`，可与 Windows 版本独立。

1. 将对应 Linux 架构的 `vmnotify-agent` 安装到虚拟机桌面用户的 `~/.local/bin/`，并赋予执行权限：

   ```sh
   mkdir -p ~/.local/bin
   chmod +x ~/.local/bin/vmnotify-agent
   ~/.local/bin/vmnotify-agent --demo
   ```

   `--demo` 仅输出模拟协议事件；不验证真实消息。麒麟若禁止运行自编译二进制，请按所在环境的应用准入流程授权该文件，不要关闭系统防护。

2. 在 Windows 终端先完成密钥登录，并通过可信途径核对首次连接的主机指纹：

   ```powershell
   ssh desktop-user@vm-host
   ```

   Windows 设置 → 可选功能可安装 OpenSSH 客户端。密钥可以由 ssh-agent 管理，或在 VMNotify 中填写私钥路径。应用不保存密码，不自动接受未知/变化的主机密钥。

3. 从 [微软官网下载页](https://dotnet.microsoft.com/download/dotnet/10.0) 安装对应 x86 / x64 / ARM64 的 **.NET Desktop Runtime 10.0**；普通 .NET Runtime、ASP.NET Core Runtime 或 .NET Framework 不能替代它。运行 Windows 安装包，或将 Portable ZIP 解压到可写目录后运行 `VMNotify.exe`。安装器在写入文件前检查对应架构的运行时，缺少时提示下载地址并阻止继续，可安装依赖后重试；不会自动下载运行时。便携版由程序启动器提示缺少依赖。旧自包含便携版请解压到新目录，只迁移 `settings.json`。
4. 填写主机、桌面用户名及端口。代理路径默认 `.local/bin/vmnotify-agent`（相对远端用户主目录，不要写 `~`），也可以填写绝对路径。
5. 点击“保存并连接”。连接后勾选需要转发的应用；选择即时生效并保存。空列表表示尚未发现受支持应用，不代表所有 Linux 应用均不支持通知。
6. 点击“测试本机通知”可检查 Windows 显示效果。首次检测到闪烁后提醒；持续闪烁期间每 3 分钟提示“仍有待查看的消息”。停止闪烁、取消应用选择、断开连接后不再重复。
7. 在“设置”开启“每天定时连接”，填写连接/断开时间（24 小时 `HH:mm`），点击“保存设置”。按 Windows 本机时间每天执行，支持如 `22:00–07:00` 的跨午夜时段。启用后优先于“启动后自动连接”，时段外保持断开。手动断开暂停当前时段，下个时段自动恢复；点击“保存并连接”可恢复当前时段连接。
8. 定时连接需要 VMNotify 保持运行：关闭窗口后继续在托盘运行；右键托盘退出后不再执行。不会自动启动已退出的程序或唤醒电脑，睡眠恢复或程序启动后会按当前时段判断是否连接。可配合“登录 Windows 时启动”使用。

安装版配置在 `%LOCALAPPDATA%\VMNotify\settings.json`；便携版通过 `portable.marker` 识别，配置保存在 EXE 旁。不要将个人设置或私钥打包发布。登录启动默认关闭，在“设置”勾选后点击“保存设置”才会设置。移动便携目录后应重新保存启动设置。

## 检测与限制

蓝信适配器监听 `org.kde.StatusNotifierItem.NewIcon`，通过 D-Bus 连接所属 PID 与 `/proc/PID/comm` 校验应用身份。信号只用于触发图像采样：独立任务读取 `IconPixmap` 并比较指纹，相同图像的重复重绘不计入闪烁。1.1 秒内收到至少 2 次、间隔至少 150 毫秒的真实图像变化视为闪烁；采集端同轮发送一次事件，宿主机每 3 分钟重复提醒，1.25 秒图像未变后复位。蓝信通常每约 0.5 秒切换图标，新一轮首次提醒仍需确认闪烁，不保证零延迟；Windows 通知显示也受系统策略影响。

- “闪烁停止”只是检测状态，不保证消息已读；持续闪烁期间的新消息不会逐条提醒，3 分钟提醒只代表仍有待查看消息，不代表有新增消息。
- 图标动画也可能满足此规则，因此默认仅对验证过的应用启用，新增应用需单独测试。
- 不提供发送人/正文、消息计数或远程回复；不支持任意应用的自动解析。
- 首版一台宿主机连接一台虚拟机；支持该虚拟机内多个适配应用。
- 连接中断后重新发现；若应用仍闪烁，可能再提醒一次。断开期间的事件不持久化补发。
- 主机通知受 Windows 勿扰模式和通知设置影响。ARM64 支持与 x86 支持不代表兼容 Windows 7；目标为 .NET 10 支持的 Windows 10/11 版本，具体见微软支持表。

## 扩展应用

`agent/src/adapters.rs` 是应用注册及检测器接口，`AttentionDetector` 将事件源与检测状态解耦。目前事件源为 StatusNotifierItem，未来新增标准桌面通知等后端时应沿用发现与事件协议。

对同样使用托盘连续闪烁的应用，可以使用 `agent/config.example.json` 配置进程名（Linux comm 最长 15 字节）和可选安装路径。`--config FILE` 替换内置规则，最多 64 条，应用 ID 必须唯一。运行时以实例拥有者的进程名识别，安装路径用于未运行时的发现。

Windows 端代理路径只接受可执行文件路径。自定义配置可通过一个普通用户启动脚本传入：

```sh
#!/bin/sh
exec "$HOME/.local/bin/vmnotify-agent" --config "$HOME/.config/vmnotify/apps.json"
```

## 构建与验证

```powershell
cargo test --workspace --locked
cargo fmt --all -- --check
dotnet run --project windows/VMNotify.Tests -c Release
dotnet build windows/VMNotify/VMNotify.csproj -c Release
# 需要 Inno Setup 7.1；输出默认为 artifacts/
./scripts/package-windows.ps1 -Iscc 'C:\path\to\ISCC.exe'
# 修改图标绘制代码后，重新生成多分辨率 ICO 与预览 PNG
dotnet run --project tools/VMNotify.IconBuilder -c Release -- windows/VMNotify/Assets
```

Linux 本机可使用 `cargo build --release --locked`。Windows 上 Rust 的 `--demo` 和单元测试可运行，真实采集必须在 Linux 运行。

GitHub Actions 构建两个 Linux 架构与六个 Windows 包并上传工作流 artifacts，在 main 分支版本变化时自动创建 Release。安装包暂未代码签名，首次运行可能出现 Windows 信誉提示。

## 协议 v1

每行一个 UTF-8 JSON 对象，最多 32 KiB。公共字段：`v:1`、`kind`、`timestamp_ms`。

| kind | 字段 / 含义 |
|---|---|
| ready | 代理启动或发现服务恢复 |
| apps | `apps:[{id,name,running,adapter,capability}]`，完整快照，最多 64 项 |
| attention | `app_id,app_name`，应用需要关注 |
| cleared | `app_id,app_name`，闪烁停止或实例消失 |
| heartbeat | 每 15 秒保持连接状态 |
| degraded | 桌面发现服务暂不可用 |

选择配置在宿主机过滤，不向虚拟机发送控制命令；采集端不发送消息内容。未来需要减少某些适配器的采集成本时，可在新协议版本中增加订阅命令。

## License

MIT

### 安装确认与设置分类

安装前确认页按已有版本与目标版本的数值关系识别安装、升级、重装或降级。显示当前版本、目标版本、原路径和目标路径；无法读取版本时显示未知，不猜测版本高低。点击最终操作按钮后才关闭旧进程并替换文件。

虚拟机配置包含地址、SSH 用户、端口、密钥和采集端路径，使用“保存并连接”；设置包含自动连接、登录启动和每日定时连接，使用“保存设置”。两页独立保存，已有配置自动沿用。