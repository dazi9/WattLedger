# WattLedger

[English](README.md) · **简体中文**

**面向 Windows 和 Linux 笔记本的本地能量账本。**

WattLedger 记录电池实际输出了多少能量，将活跃使用与睡眠分开，在硬件支持时把系统功率拆成 package / CPU / GPU / DRAM，并保存长期的电池健康历史。基础电池读数不需要内核驱动或管理员权限，不联网，也不含遥测。

当前源码：**0.2.1** · [更新日志](CHANGELOG.md) · [Releases](https://github.com/dazi9/WattLedger/releases)

![WattLedger 仪表盘（合成演示数据）](docs/screenshot.png)

*合成演示数据。*

## 为什么还要另一个电池工具？

Windows 只显示一个百分比，`powercfg /batteryreport` 只给静态快照。HWiNFO 和 MSI Afterburner 是优秀的传感器面板，但它们不是电池账本：它们回答不了“今天的 Wh 都去哪了”，也回答不了“待机一夜掉了多少电”。

WattLedger 刻意做得窄：

- **实时功率**：电池放电 / 充电功率、电压、剩余能量、预计剩余时间。
- **能量核算**：对电池功率做梯形积分，并用电池剩余容量计数器校准。
- **会话账本**：接电、充电、电池和 Modern Standby 时段，包含起止电量、时长、Wh 和平均功率。
- **硬件拆分**：在支持的 Intel / AMD 机器上，用 Windows Energy Meter（RAPL）计数器读取 package、CPU、GPU 和 DRAM 功率。
- **电池健康**：设计容量、满充容量、循环次数和保存的健康历史。
- **长期历史**：SQLite，本地保存，可导出 CSV、JSON 或 Markdown。
- **桌面端 + CLI**：Windows 用 WPF 仪表盘，Linux 用 Avalonia 桌面端，另有一个可脚本化的命令行工具。
- **英文与简体中文界面**：在底部选择 System、English 或 简体中文；System 跟随系统语言，选择会保存。

### 对比

| 工具 | 实时功率 | 长期账本 | 待机核算 | 按应用能耗 | 免驱动 / 管理员 |
|---|---|---|---|---|---|
| Windows 设置 / battery report | 部分 | 否 | 部分 | 较新版本 | 是 |
| HWiNFO64 | 是 | 仅 CSV | 手动 | 否 | 部分 |
| MSI Afterburner | 偏重 GPU | 否 | 否 | 否 | 是 |
| Powir | 否 | 电池报告 | 否 | 否 | 是 |
| Watt's Left | 是 | 30 分钟 | 否 | 否 | 是 |
| **WattLedger** | **是** | **是** | **是** | 计划中 | **是** |

第三方工具的信息来自其公开文档；使用前请核对当前版本。

## 系统要求

- Windows 10 或 11，x64。
- Ubuntu 24.04 或 Debian 12 / 13，x64。GNOME 是已测试桌面；其他桌面需要安装 [Linux 显示后端](docs/linux-display-backends.md) 中列出的原生库。
- 从源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。
- 笔记本电池提供受支持的 Windows 电池接口，或 Linux sysfs 电池条目。

Linux 桌面支持原生 Wayland（实验性）和 X11。自动选择会在 Wayland 会话中优先使用 Wayland；X11 / XWayland 兼容使用 `--backend=x11`。托盘图标可选，需要 StatusNotifier / AppIndicator 宿主；没有宿主时保持应用窗口打开。

基础电池读数不需要管理员权限或内核驱动。Linux 传感器可用性取决于可读的内核 sysfs 接口。

## 快速开始

Windows 上，在 PowerShell 或终端中：

```sh
git clone https://github.com/dazi9/WattLedger.git
cd WattLedger

dotnet build WattLedger.slnx -c Release

# 单次状态
dotnet run --project src/WattLedger.Cli -- status

# 持续记录（Ctrl+C 停止）
dotnet run --project src/WattLedger.Cli -- watch

# 今天的账本
dotnet run --project src/WattLedger.Cli -- today

# JSON / Markdown / CSV 导出
dotnet run --project src/WattLedger.Cli -- export --format md --output report.md
dotnet run --project src/WattLedger.Cli -- export --format csv --output samples.csv

# 硬件 / 数据库诊断
dotnet run --project src/WattLedger.Cli -- diag

# 桌面端（托盘 + 仪表盘）
dotnet run --project src/WattLedger.App
```

Linux 上使用平台专用解决方案和桌面项目：

```sh
dotnet build WattLedger.Linux.slnx -c Release
dotnet run --project src/WattLedger.Cli -- status
dotnet run --project src/WattLedger.Linux
```

Linux 桌面接受 `--db <path>`、`--tray` 和 `--backend auto|wayland|x11`。省略命令行参数时，`WATTLEDGER_BACKEND` 提供默认值。原生库、回退行为和当前限制见 [Linux 显示后端](docs/linux-display-backends.md)。数据默认写入 `$XDG_DATA_HOME/wattledger`（或 `~/.local/share/wattledger`）。

`wattledger status` 只读传感器：不创建数据库、不记录采样，也不取得采样器的写入租约；桌面端或 `watch` 正在记录时仍可使用。桌面端的 `--db` 路径在启动时会转为绝对路径，因此开启登录自启动后仍使用同一个数据库。如果另一个采样器占用了数据库，Windows 应用即使以 `--tray` 启动也会报告启动失败并显示 `NOT RECORDING`。

## 图表操作

图表按实际采样时间展开，绿色为带符号功率，蓝色虚线为电池百分比。正功率表示放电，负功率表示充电，实时卡片和托盘提示使用同一方向。

- **缩放**：在图上滚动滚轮、在精密触控板上捏合、使用 Ctrl + 滚轮、**+/−** 按钮或按键；**Fit today**、双击或 Home 显示今天已加载的范围。
- **平移**：在 **Pan** 模式下左键拖动图表。Shift + 拖动或中键拖动可临时平移；方向键和横向滚动也可平移。
- **选择**：切到 **Select** 后在采样点之间拖动，或在 From / To 输入本地时间（`HH:mm:ss`）并点击 **Calculate**。**Fit selection** 放大到该区间；Escape 清除选区。
- **跟随**：按 End 或 **Follow live**。拖动或导航会暂停跟随。
- **查看**：悬停显示原始采样的时间、功率和电量。

原生 Wayland 捏合使用随包构建的 Avalonia 补丁后端，需要 compositor 暴露 pointer gestures。X11 / XWayland 保留滚轮、Ctrl + 滚轮、按钮和键盘缩放。

图表绘制会把采样点压缩到可用像素，但保留每个像素列的极值；区间计算始终使用原始采样，而不是压缩后的曲线。

范围统计是功率估算，和经过容量校准的账本合计是两套口径。缺失功率、超过 60 秒的间隔，以及已记录的睡眠缺口（包括已确认的短睡眠）不参与积分；平均功率只使用有覆盖的秒数。没有可用功率的区间显示为不可用。

报告和导出在后台运行。图表历史增量加载，窗口隐藏或最小化时推迟报告 / 图表刷新，恢复后补齐。

## Linux 电池读数

Linux 电池值来自内核 sysfs，并从微单位转换为 WattLedger 内部的 mWh、mW 和 mV。缺失的 sysfs 值保持不可用。`power_now` 缺失或为零时，回退到可用的 `current_now` 和电压。电池没有可用功率时，用容量变化估算能量；量化步长和固件更新频率会让短区间看起来为零。

Linux 不提供 RAPL、亮度和前台进程读数。`SleepGap` 由时间戳检测；同时观察到 logind 的睡眠和唤醒事件时，备注会记录该确认，否则明确标记为推断。容量缺失时标记为不可用，报告摘要会把待机能量标为不完整。

基础实时读数和界面启动已在 Ubuntu 24.04.5 GNOME 上检查。具体机型的传感器覆盖和睡眠行为见 [硬件支持](docs/supported-hardware.md)，后端设计见 [Linux 显示后端](docs/linux-display-backends.md)。

## 数据存放位置

Windows 桌面端和 CLI 共用同一个数据库：

```text
%LocalAppData%\WattLedger\wattledger.db
```

Linux 默认数据库为 `$XDG_DATA_HOME/wattledger/wattledger.db`，未设置时为 `~/.local/share/wattledger/wattledger.db`。两个平台都接受 `--db` 指定位置。

复制或删除数据库前，先退出桌面端和 `watch`。备份时复制整个数据目录，包括仍存在的 `-wal` 和 `-shm` 文件；需要在线备份时使用 SQLite 的备份接口。删除文件即可重置历史；想保留时先导出。

## 演示数据

本 README 的截图来自合成数据，不是真实用户的历史：

```sh
python tools/seed-demo.py --db demo.db
dotnet run --project src/WattLedger.App -- --db demo.db
```

脚本默认拒绝写入名为 `wattledger.db` 的数据库，除非加 `--force`。

## 自包含构建

Linux x64：

```sh
dotnet publish src/WattLedger.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-linux-x64
dotnet publish src/WattLedger.Linux -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-linux-x64
bash tools/package-linux.sh artifacts/cli-linux-x64 artifacts/desktop-linux-x64 artifacts/wattledger-linux-x64.deb
```

Windows x64，在 PowerShell 中：

```powershell
dotnet publish src/WattLedger.Cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-win-x64
dotnet publish src/WattLedger.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-win-x64
```

Debian 打包脚本需要 `dpkg-deb` 和 Python 3，生成 `amd64` 包。Linux 构建仍需要 [Linux 显示后端](docs/linux-display-backends.md) 中列出的图形、字体和显示库。

## 工作原理

```text
Windows battery IOCTL + Energy Meter/RAPL ─┐
Linux sysfs battery + logind events ───────┼─> sampler / session tracker
Windows power events ──────────────────────┘            │
                                                       ▼
                                            SQLite (local WAL database)
                                                       │
                                  ┌────────────────────┴───────────────────┐
                                  ▼                                        ▼
                          CLI reports/exports                    WPF / Avalonia desktops
```

- **电池**：Windows 上用 SetupAPI 枚举，并通过 `IOCTL_BATTERY_QUERY_STATUS` / `IOCTL_BATTERY_QUERY_INFORMATION` 读取 mWh、mW、mV、设计容量、满充容量和循环次数；Linux 读取 sysfs。
- **系统功率**：电池放电率是基准。Intel / AMD 机器上，Windows Energy Meter 性能计数器无需驱动即可提供 package、CPU、GPU 和 DRAM 功率。
- **能量**：对每个采样区间的净电池功率做梯形积分；固件不提供速率时回退到剩余容量变化。
- **睡眠缺口**：超过 60 秒的采样缺口在 Windows 上保留 `ModernStandby` 分类以兼容历史数据，在 Linux 上标为 `SleepGap`。只有同时观察到 logind 的睡眠和唤醒事件时，Linux 会话备注才记录为已确认；否则保留推断。缺口能量在可用时用剩余容量差估算；不可用能量在库中保留数值零，但备注和报告会标为不完整。
- **存储**：SQLite WAL 模式，每个采样一行、每个会话一行、每天一个健康快照。

公式和边界情况见 [docs/METHODOLOGY.md](docs/METHODOLOGY.md)。

## 项目结构

```text
src/WattLedger.Core/      原生读取、能量引擎、SQLite、报告
src/WattLedger.Desktop/   共享的无 UI 图表数学 / 状态、仪表盘加载、主题偏好
src/WattLedger.Cli/       可脚本化 CLI（status / watch / today / export / diag）
src/WattLedger.App/       Windows WPF 托盘应用与仪表盘
src/WattLedger.Linux/     Linux Avalonia 桌面应用
tests/                    积分与会话状态单元测试
docs/                     方法、隐私、硬件与平台说明
```

核心库不依赖 UI；CLI 和两个桌面端都是同一引擎的轻客户端。共享图表数学和交互状态位于 `WattLedger.Desktop`。

## 隐私

WattLedger 按本地优先设计：

- 不建立网络连接；
- 没有账户或遥测；
- 没有后台云同步。

本地数据包括 SQLite 数据库、偏好设置、SQLite WAL / SHM 文件、日志、登录自启动项和 .NET 解压缓存。不收集文档内容、窗口标题或网址。Windows 会在采样中记录前台进程名；Linux 不采集。`wattledger diag` 可能包含设备名、路径、行数和固件序列号，分享前请检查。

详见 [docs/PRIVACY.md](docs/PRIVACY.md)。

## 路线图

- **0.1**：Windows 能量账本、托盘应用、CLI、CSV / JSON / Markdown 导出、电池健康。
- **0.2.x**：共享图表导航与统计、主题、日边界安全报告、原生 Wayland 后端、精密触控板捏合、英文 / 简体中文界面。
- **下一步**：更广的硬件、compositor 与设备覆盖。
- **后续**：历史 / 多日图表浏览、按应用能耗估算、异常待机提醒、更多打包方式与架构、标准化电池放电测试。

## 限制

- 电池固件采样粒度粗：有些机器按 1% 步进更新容量，功率读数也有噪声。WattLedger 显示硬件实际提供的数据，并区分测量值与估算值。
- 有些笔记本不提供放电率，此时使用剩余容量回退。
- RAPL / Energy Meter 的可用性因 CPU 和固件而异。`wattledger diag` 会显示你的机器暴露了哪些值。
- 原生 Wayland 捏合取决于 compositor 是否暴露 pointer gestures；X11 / XWayland 保留滚轮、Ctrl + 滚轮、按钮和键盘缩放。
- Windows 精密触控板手势取决于设备和 Windows 实际传递的输入；滚轮、Ctrl + 滚轮、按钮和键盘缩放始终可用。
- 按应用能耗尚未实现。Windows 没有精确的公开按应用能耗 API；后续版本会用 CPU / GPU 时间占比估算，并标明这是估算。
- 应用未运行时的睡眠区间无法重建。Linux 的 `SleepGap` 不能证明机器真的睡眠。要保持连续历史，请让应用持续运行；Linux 支持用户级 XDG 自启动。
- Windows 主要在 MECHREVO WUJIE（Intel Core Ultra 5 226V，Windows 11 build 26200）上测试。Linux 启动已在 Ubuntu 24.04.5 GNOME 上检查。其他机型见 [硬件支持](docs/supported-hardware.md)，欢迎提交报告。

## 贡献

欢迎贡献。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。提交 bug 时请附上：

```sh
dotnet run --project src/WattLedger.Cli -- diag
```

以及笔记本型号和 Windows 版本。未经检查，不要上传个人数据库文件。

## 许可证

MIT。参见 [LICENSE](LICENSE)。
