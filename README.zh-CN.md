# WattLedger

[English](README.md) · **简体中文**

**面向 Windows 和 Linux 笔记本的本地能量账本。**

WattLedger 持续记录电池功率、能量变化、充放电会话与电池健康，帮助你了解一天用了多少 Wh、哪些时段在放电，以及睡眠间隔可能消耗了多少电量。Windows 提供 WPF 桌面端，Linux 提供 Avalonia 桌面端，两端与 CLI 共用采样、存储和报告引擎。

基础电池读数不需要内核驱动或管理员权限。应用不上传数据、不接入账户，也不包含遥测或云同步。

![Windows 桌面界面（合成数据）](docs/screenshot.png)

截图使用合成数据，不能作为真实续航或精度测试结果。浅色与深色图表预览见下方图表章节。

## 当前功能

当前源码版本为 **0.2.1**，版本号以 [Directory.Build.props](Directory.Build.props) 为准。已发布的下载包可能早于当前源码。

- **实时读数**：电池百分比、充放电功率、电压、剩余能量、健康度、循环次数及预计剩余时间，具体项目取决于硬件暴露的数据。
- **会话账本**：区分接电、充电、使用电池与睡眠间隔，记录起止时间、电量、持续时间、Wh 和平均功率。
- **按日报告**：跨午夜会话按本地日边界拆分；夏令时切换日按实际 23 / 25 小时计算。
- **交互图表**：左右平移、动态缩放、原始采样点悬停、拖动取点、自定义时间区间与能量统计，两端共享图表数学和交互状态机。
- **外观设置**：浅色、深色、跟随系统，设置可保存，图表与桌面控件同步切换。
- **本地历史与导出**：SQLite 存储，支持 CSV、JSON、Markdown；桌面提供托盘和登录自启动设置。
- **平台传感器**：Windows 在可用时读取 Energy Meter / RAPL；Linux 从内核 sysfs 读取电池数据，并使用 logind 辅助识别睡眠间隔。
- **Linux 显示后端**：原生 Wayland（实验性）与 X11，可自动选择，也可显式指定。

当前桌面界面主要使用英文。本文件提供完整中文使用说明；应用内简体中文支持计划在下一轮加入。按应用能耗归因、周报、异常待机提醒等仍是后续方向，**不属于当前已实现功能**。

## 平台与验证范围

| 项目 | 当前情况 |
|---|---|
| Windows | Windows 10 / 11 x64，WPF 桌面与 CLI；基础电池读数依赖 Windows 电池接口 |
| Linux | x64 桌面与 CLI；以 Ubuntu / Debian 为当前构建、打包目标，传感器依赖可读的 sysfs 接口 |
| Linux 显示 | Avalonia 12.1.4，原生 Wayland 实验后端与 X11 / XWayland 兼容路径 |
| 构建验证 | Windows、Ubuntu 24.04、Debian 12 / 13 的 CI 构建已通过；这不等于覆盖这些系统上的所有硬件与桌面环境 |
| 本机 Wayland | Ubuntu 24.04.5 GNOME 下已在移除 `DISPLAY` 的情况下以原生 Wayland 启动，并确认继续采样 |
| 尚待实机验证 | 更广泛的 GNOME / KDE、显卡与缩放组合、物理触控板、真实睡眠 / 唤醒和受控电池放电测试 |

从源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/)。自包含发布包自带 .NET 运行时，但仍需要系统图形、字体及加密等原生库。

Linux 托盘依赖 StatusNotifier / AppIndicator 宿主，部分 GNOME 配置需要相应扩展。没有托盘宿主时，窗口仍可使用；不要依赖隐藏到不存在的托盘。无图形会话的机器可以使用 CLI `watch`。

## 下载与安装

从 [Releases](https://github.com/dazi9/WattLedger/releases) 获取**已公开发布**的包，并与同一版本的 `checksums.txt` 核对 SHA-256。仓库中的发布流程先生成草稿，再由维护者公开，因此并非每次提交都有可下载版本。

- Windows：解压对应的 CLI 或桌面包，运行 `wattledger.exe` 或 `WattLedger.exe`。
- Linux：解压自包含包后运行 `./wattledger` 或 `./WattLedger.Linux`；如果可执行位丢失，使用 `chmod +x` 恢复。
- Debian / Ubuntu：下载包后可运行 `sudo apt install ./wattledger-linux-x64.deb`，再通过应用菜单或 `wattledger-desktop` 启动。

安装软件包可能需要管理员权限，正常采样不需要。

Linux 仍需 fontconfig、FreeType、ICU、OpenSSL 等原生库。Wayland 需要 libwayland-client、libwayland-cursor、libwayland-egl、libxkbcommon 以及可用的 EGL / OpenGL 驱动；X11 路径需要对应的 X11 客户端库。Debian 包声明了这些依赖，XWayland 是推荐项而非原生 Wayland 的强制依赖。其他发行版需按本发行版的包名安装依赖，目前没有提供 RPM 或 Arch 专用安装包。

## 从源码快速开始

```sh
git clone https://github.com/dazi9/WattLedger.git
cd WattLedger
```

### Linux

使用 Linux 专用解决方案，避免在 Linux 上直接构建 WPF 项目：

```sh
dotnet build WattLedger.Linux.slnx -c Release
dotnet run --project src/WattLedger.Cli -- status
dotnet run --project src/WattLedger.Linux
```

### Windows

在 PowerShell 中运行：

```powershell
dotnet build WattLedger.slnx -c Release
dotnet run --project src/WattLedger.Cli -- status
dotnet run --project src/WattLedger.App

# 仅托盘启动；登录自启动也可从桌面端设置
dotnet run --project src/WattLedger.App -- --tray
```

Windows 和 Linux 桌面均支持 `--db <path>` 与 `--tray`。Linux 没有可用托盘宿主时会保留可访问的窗口。桌面端会将自定义数据库路径转换为绝对路径，便于登录自启动继续使用同一文件。

### CLI 命令

以下示例使用已安装的 `wattledger`；从源码运行时，将命令前缀替换为 `dotnet run --project src/WattLedger.Cli --`。Windows 使用 `wattledger.exe`。

```sh
wattledger status
wattledger watch
wattledger today
wattledger report --date 2026-10-26
wattledger export --format csv --output samples.csv --date 2026-10-26
wattledger export --format json --output report.json
wattledger export --format md --output report.md
wattledger diag
wattledger init
wattledger --help
```

| 命令 | 用途 |
|---|---|
| `status` | 单次读取实时传感器状态，也是未指定命令时的默认操作 |
| `watch` | 持续采样，通常使用电池时每 5 秒一次、接电时每 30 秒一次；Ctrl+C 停止 |
| `today` | 查看今天的账本 |
| `report --date yyyy-MM-dd` | 查看指定本地日期的报告；省略日期时使用今天 |
| `export` | 按本地日期导出 CSV 采样或 JSON / Markdown 报告；支持 `--format`、`--output`、`--date` |
| `diag` | 输出硬件与数据库诊断信息 |
| `init` | 创建或初始化数据库，不启动持续采样 |

所有 CLI 命令可附加 `--db <path>` 指定数据库，例如：

```sh
wattledger watch --db ./my-ledger.db
wattledger report --date 2026-10-26 --db ./my-ledger.db
```

`status` 只读传感器：不创建数据库、不写入采样，也不取得采样器写入租约，可以在桌面端或 `watch` 记录时并用。其他命令可能初始化数据库。

**同一数据库只允许一个采样器写入。** 桌面端和 `watch` 不能同时记录到同一文件。冲突时应停止其中一个再启动另一个；Windows 即使使用 `--tray` 启动，也会提示失败并显示 `NOT RECORDING`，避免误以为正在记录。

## Linux：选择 Wayland 或 X11

安装 Debian 包后：

```sh
wattledger-desktop --backend=auto
wattledger-desktop --backend=wayland
wattledger-desktop --backend=x11
WATTLEDGER_BACKEND=x11 wattledger-desktop
```

从源码运行可使用 `dotnet run --project src/WattLedger.Linux -- --backend=wayland`；解压版使用 `./WattLedger.Linux` 替代 `wattledger-desktop`。

- `auto` 是默认值：根据 Wayland 会话及可用的 compositor socket 优先选择原生 Wayland；同时有 `DISPLAY` 时可使用 Avalonia 的 X11 回退路径。
- `wayland` 显式要求原生 Wayland，不会静默改用 X11；需要有效的 `WAYLAND_DISPLAY` / `XDG_RUNTIME_DIR` 与图形驱动。
- `x11` 显式使用 X11；在 Wayland 桌面中需要 XWayland，也可用于原生 X11 或转发的 X11 会话。
- 命令行优先于 `WATTLEDGER_BACKEND`。登录自启动会保存当时选择的后端。

原生 Wayland 在上游仍属于实验性功能，KDE 全局菜单、窗口图标等集成尚有限制。启动失败时先查看终端错误，在有 `DISPLAY` 的环境尝试 `--backend=x11`。第二次启动会激活已有实例，更换后端或数据库前应先退出已有进程。细节见 [显示后端说明](docs/linux-display-backends.md)。

## 桌面图表与主题

两端图表使用相同的区间计算和交互状态。绿色曲线表示电池功率，蓝色虚线表示电量百分比；**正功率为放电，负功率为充电**，实时卡片与托盘使用相同方向。

| 操作 | 效果 |
|---|---|
| 默认 `Pan` 模式下左键拖动 | 左右平移；向右拖可查看更早数据 |
| `Earlier` / `Later` 或左右方向键 | 平移可见时长的 20%，到边界时按钮禁用并显示提示 |
| 触控板横向滚动事件 | 已处理左右平移；真实设备兼容性仍待确认 |
| 纵向滚轮或缩放按钮、`+` / `−` | 缩放，滚轮以指针位置为中心 |
| 切换 `Select` 后左键拖动 | 在两个原始采样点之间选择区间 |
| Shift + 左键拖动，或中键拖动 | 在选择模式中临时平移 |
| `Fit today`、双击或 Home | 显示今天已加载的全部范围，并暂停实时跟随 |
| `Follow live` 或 End | 恢复跟随最新采样；全范围视图恢复跟随时使用最近 30 分钟 |
| `Fit selection` / `Clear selection`、Escape | 放大到选区或清除选区；Escape 清除选区 |

触控板横向滚动已有事件处理和自动化覆盖，但真实触控板缩放 / 捏合手势尚未完成验证。Linux 上的缩放反馈与 Windows 手势行为会在下一轮一起调查、对齐，不能把鼠标滚轮缩放等同于完整触控板手势支持。

初次打开默认跟随今天最近 30 分钟，记录不足时显示已有范围。实际拖动、平移和缩放会暂停跟随，单纯点击平移模式不会暂停。后台仍继续记录；切换到新的一天后恢复跟随当天数据。全部数据已能放入视图时，需要先放大才有左右平移空间。

也可在 From / To 输入本地时间 `HH:mm` 或 `HH:mm:ss`，点击 Calculate 自定义区间。当前图表只浏览**今天**，手动范围必须位于同一天；跨日和历史日期报告可先用 CLI 查看。

选区显示原始点数、时长、放电 / 充电 Wh、有符号净 Wh、有效时长内的平均 W、覆盖率和最小 / 最大功率。悬停可检查原始时间、功率与电量。图表为显示而减少绘制点数，但统计始终使用原始采样。

图表选区是功率积分估算，和经过容量变化校准的会话账本是两种口径。缺失功率、超过 60 秒的间隔及已记录睡眠边界不参与选区积分；覆盖率不足时不能把结果当作完整耗电量，没有有效数据也不代表耗电为零。

底部 Theme 可选 `System`、`Light`、`Dark`，默认跟随系统，选择立即生效并保存。Windows 监听系统应用主题变化；Linux 取决于桌面提供的外观信息，没有系统偏好时使用框架默认值。主题不会修改采样或账本数据。

报告与导出在后台处理，图表增量加载，隐藏或最小化时暂停界面报告刷新，采样继续。恢复窗口后会刷新报告；部分窗口管理器恢复场景中，实时卡片可能等待下次采样，托盘打开会立即应用最新读数。

相同合成数据的浅色与深色图表：

![浅色图表](docs/theme-light.png)

![深色图表](docs/theme-dark.png)

## 数据、备份与隐私

| 内容 | Windows | Linux |
|---|---|---|
| 默认数据库 | `%LocalAppData%\WattLedger\wattledger.db` | `$XDG_DATA_HOME/wattledger/wattledger.db`，未设置时为 `~/.local/share/wattledger/wattledger.db` |
| 主题偏好 | `%LocalAppData%\WattLedger\preferences.json` | `$XDG_CONFIG_HOME/wattledger/preferences.json`，未设置时为 `~/.config/wattledger/preferences.json` |
| 登录自启动 | 桌面端的 Windows 自启动设置 | `$XDG_CONFIG_HOME/autostart/wattledger.desktop`，未设置时为 `~/.config/autostart/wattledger.desktop` |

使用自定义 `--db` 后，数据库位于指定路径；主题偏好仍使用平台配置路径。程序还可能生成本地日志、SQLite WAL / SHM 文件、实例通信端点，以及 .NET 的本地提取缓存。

备份前先从托盘退出桌面端，并停止 `watch`。随后复制数据库所在目录，保留存在的 `-wal` / `-shm` 文件；不要只复制一个仍在写入的 `.db`。需要在线备份时使用 SQLite 的备份接口。导出的 CSV / JSON / Markdown 是独立文件，不等于完整数据库备份。

卸载 Debian 包可运行 `sudo apt remove wattledger`。删除解压版程序或卸载包不会自动替你清空用户历史；卸载前先关闭登录自启动。若要彻底清除，退出全部采样器后再删除相应数据与配置目录，自定义数据库和导出文件需单独处理。

应用运行不联网、不自动同步，也不读取文档内容、浏览历史或窗口标题。Windows 可记录采样时的前台进程名；Linux 当前不采集该信息。`diag` 可能包含设备名、数据库路径、行数，以及固件暴露的序列号，提交公开问题前请检查并删去个人信息。

## 测量口径与已知限制

- 电池放电率是整机电池侧功耗的主要读数，不等于插座或充电器输入功率。RAPL 只覆盖其暴露的硬件域，不代表屏幕等所有部件。
- 引擎对电池功率做梯形积分，硬件缺少速率时可回退到剩余容量变化；粗粒度容量读数会让很短区间出现零变化或噪声。
- Linux 从 sysfs 转换微单位到 mWh / mW / mV；缺失或零 `power_now` 可回退到有效的电流与电压。Linux 当前没有 RAPL、亮度、前台进程读数。
- Linux `Charging` 可作为接电证据，即使电源 `online=0`。若固件为 `Full` / `Unknown` 且没有明确充电、放电或接电证据，仍可保存读数，但会保守地暂停会话跟踪，不推断成放电。
- 超过 60 秒的采样间隔在 Windows 保留 `ModernStandby` 分类，Linux 标为 `SleepGap`。只有观察到成对 logind 睡眠 / 唤醒事件，Linux 才将转换标为已确认；单纯时间间隔不能证明机器真的睡眠。
- 睡眠能量依赖间隔两端的容量。缺失容量时报告会标记为不可用 / 不完整，不能把数据库里的数值零当作没有耗电。程序未运行时的缺失历史不能凭空恢复。
- 手动时间字段只到整秒，夏令时重复小时不能手选 UTC 偏移；需要精确时间时可拖动原始采样点。真实睡眠和受控放电精度仍需实机验证。

详细方法、误差与小问题分别见 [测量方法](docs/METHODOLOGY.md)、[已知问题](docs/KNOWN-ISSUES.md)、[硬件验证](docs/supported-hardware.md)。

## 自包含发布与 Debian 打包

Linux x64：

```sh
dotnet publish src/WattLedger.Cli -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-linux-x64
dotnet publish src/WattLedger.Linux -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-linux-x64
bash tools/package-linux.sh artifacts/cli-linux-x64 artifacts/desktop-linux-x64 artifacts/wattledger-linux-x64.deb
```

打包脚本需要 `dpkg-deb` 和 Python 3，拒绝覆盖已有输出。`.deb` 版本来自 `Directory.Build.props`。原生库必须随发布目录保留，或使用上述 `IncludeNativeLibrariesForSelfExtract=true` 打入单文件。

Windows x64，在 PowerShell 中：

```powershell
dotnet publish src/WattLedger.Cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-win-x64
dotnet publish src/WattLedger.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-win-x64
```

项目的 package 工作流支持版本标签和手动触发，release 工作流生成带校验和的草稿，公开发布由维护者手动完成。

## 开发与贡献

```text
src/WattLedger.Core/     原生传感器、能量引擎、SQLite、按日报告
src/WattLedger.Desktop/  两端共享的无 UI 图表数学、交互状态、增量加载和主题偏好
src/WattLedger.Cli/      命令行工具
src/WattLedger.App/      Windows WPF 桌面与托盘
src/WattLedger.Linux/    Linux Avalonia 桌面与平台集成
tests/                  核心、共享逻辑与桌面定向测试
tools/                  打包、隔离启动检查与演示数据脚本
docs/                   测量方法、隐私、平台与验证记录
```

Core 不依赖 UI；新增图表区间和交互规则应放入共享桌面逻辑，而不是在两端重复实现。构建与测试步骤见 [贡献指南](CONTRIBUTING.md)；分支使用 `<GitHub 用户名>/<主题>` 命名。

先退出已有桌面实例，再生成隔离的演示数据：运行 `python tools/seed-demo.py --db demo.db`，随后用桌面的 `--db demo.db` 打开。脚本默认拒绝写入名为 `wattledger.db` 的数据库，不要把演示数据混入个人历史。

下一轮优先处理两端应用的简体中文界面，以及 Linux / Windows 触控板缩放和捏合手势。具体拆分见 [下一轮迭代计划](docs/NEXT-ITERATION.md)。其他后续方向包括历史日期图表、更多发行版与硬件验证、按应用能耗估算、周报和标准化续航测试；具体实现状态以代码和 [更新日志](CHANGELOG.md) 为准，不将计划视作已发布功能。报告问题时请附系统版本、笔记本型号和已检查过的 `wattledger diag` 输出。

## 许可证

MIT。参见 [LICENSE](LICENSE) 与 [第三方组件说明](THIRD_PARTY_NOTICES.md)。
