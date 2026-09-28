# 安装器启动调查（2026-09-28）

## 测量方法

Windows 11 x64，Inno Setup 7.1.0，未签名的本地文件，无 Zone.Identifier。隐藏启动安装包并记录 /LOG，轮询 TWizardForm 原生窗口，随后结束测试进程，不执行安装。测量的是窗口创建时间，不是可见首帧或资源管理器双击耗时。未重启清空缓存，各变体的首次测量不能视为等价冷启动。

| 配置 | 本轮首次 / ms | 后续两次 / ms |
|---|---:|---:|
| 原单文件 EXE，x86 加载器，LZMA2 | 3154 | 441 / 433 |
| 重新构建的基线（已运行过相同内容） | 540 | 420 / 371 |
| x64 加载器 | 5113 | 382 / 403 |
| 关闭压缩 | 2581 | 380 / 388 |
| 磁盘分卷 | 2310 | 389 / 368 |
| 禁用外层加载器，仅供诊断 | 279 | 70 / 72 |

原包首次启动请求时间 13:36:09.803，日志开始于 13:36:12.902。约 3099 ms 在日志初始化之前，其后约 55 ms 创建窗口。延迟定位于正常向导初始化之前，尚不能归因于某个系统组件。日志含 DetectorsAppHealth 兼容模式，但没有证据说明其独立耗时。

## 官方资料与适用边界

- [Inno 加载器](https://jrsoftware.org/ishelp/topic_setup_usesetupldr.htm)：单文件包需向 TEMP 解出内部安装器并启动。关闭加载器虽然实测更快，但官方将其主要用于调试，且存在签名/卸载器注意事项，因此不作为正式方案。
- [安装器架构](https://jrsoftware.org/ishelp/topic_setup_setuparchitecture.htm)：改变原生架构或模拟执行方式，本轮未证明 x64 有稳定优势。
- [磁盘分卷](https://jrsoftware.org/ishelp/topic_setup_diskspanning.htm)：可分离有效载荷，官方建议用来缓解大型包签名验证延迟；本轮小型未签名包没有体现出足够收益。
- [SmartScreen 信誉](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)：下载来源、文件及发布者信誉影响系统处理。签名不等于立即获得信誉。本地无下载标记的测量不能代表浏览器下载后的启动；未具备证书进行签名对照。
- [Defender 性能分析](https://learn.microsoft.com/en-us/defender-endpoint/tune-performance-defender-antivirus)：管理员级录制可定位扫描成本。本会话未提权，尚不能确认 Defender 是延迟来源。

需要区分的因素包括：外层加载器与子进程初始化、磁盘/TEMP 路径和文件缓存、后台 CPU/I/O 负载、系统安全与兼容检查，以及安装脚本的同步初始化逻辑。包体积不是唯一指标。本轮没有逐项隔离所有因素。

## 已合入优化

启用 [Files 的 notimestamp](https://jrsoftware.org/ishelp/topic_filessection.htm)，避免只因重新复制输入文件、时间戳改变而产生不同安装包。

验证：仅修改 README.md 的 LastWriteTime，原配置生成不同 SHA-256（F34C144E… / A744499A…）；启用后两份包完全一致，均为 AFAC8ED068EAD5D82D45C9DA18E6A370C489A602D0313140ACC0DE7B1C07644E。

这证明消除了时间戳造成的无效身份变化，不保证系统缓存必然复用，也不保证真正新版本首次启动低于两秒。改变实际内容仍会改变哈希。

复用测量脚本增加了进程父子关系验证，包含额外 CIM 开销：首份固定哈希包 2123 / 570 / 554 ms；另一位置同哈希包 708 / 597 / 614 ms。不要与前述较低开销探针直接比较。

保留单文件正式安装器、压缩方式和系统安全设置。运行环境检查仍在 PrepareToInstall 阶段，首个界面不执行网络下载或依赖检测。

## 复测

```powershell
./scripts/measure-installer-startup.ps1 -Exe ./artifacts/VMNotify-0.1.0-win-x64-setup.exe -OutputDirectory ./artifacts/startup-check
```

脚本隐藏启动并结束测试实例，不执行安装。WizardFound=false 是超时，不是有效测量。重启冷启动、可见首帧、下载信誉、签名发布及管理员 ETW 跟踪仍未验证。
