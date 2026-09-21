# AnimeDownloader · Avalonia

> 用 **Avalonia 12 + C# (.NET 10)** 重写的动漫图片浏览与下载桌面应用。
> 功能参考 [CatgirlDownloader](https://github.com/NyarchLinux/CatgirlDownloader)（GTK4/Python），
> 界面与实现均为全新设计。
>
> **这是本仓库的 `avalonia` 分支。** `main` 分支是同一应用的 WinUI 3 版本 ——
> 两个分支共用同一份 Core，只有 App 层的前端框架不同。

![平台](https://img.shields.io/badge/platform-Windows%2010%201809%2B-0f6cbd)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![界面框架](https://img.shields.io/badge/UI-Avalonia%2012-8B44AC)
![分支](https://img.shields.io/badge/branch-avalonia-8B44AC)
![版本](https://img.shields.io/badge/version-0.4.0-6e7681)
![许可](https://img.shields.io/badge/license-GPL--3.0--or--later-3fb950)

---

## 目录

- [分支说明](#分支说明)
- [项目简介](#项目简介)
- [依赖与运行环境](#依赖与运行环境)
- [Linux 适配度](#linux-适配度)
- [构建与启动](#构建与启动)
- [项目结构](#项目结构)
- [亮点](#亮点)
- [界面](#界面)
- [功能](#功能)
- [键盘快捷键](#键盘快捷键)
- [图源一览](#图源一览)
- [配置与数据目录](#配置与数据目录)
- [测试](#测试)
- [迁移说明：与 WinUI 版的差异](#迁移说明与-winui-版的差异)
- [相关文档](#相关文档)
- [许可](#许可)

---

## 分支说明

| 分支 | 内容 | 技术栈 |
| --- | --- | --- |
| `main` | WinUI 3 版（原有主线，未受本分支影响） | `net10.0-windows10.0.19041.0` + Windows App SDK 1.8 |
| **`avalonia`（本分支）** | Avalonia 12 版 | `net10.0`（平台中立）+ Avalonia 12.1.2 |

**为什么是分支，而不是同一个分支下的子目录。** 两个前端各自带一份解决方案文件
（同名 `AnimeDownloader.slnx`）、`Directory.Build.props`、`.gitignore` 与 `NuGet.Config`。
挤进同一棵树会互相干扰：同名项目、同名输出目录、`dotnet build` 不带参数时的默认目标都会变成歧义。
分成分支之后，两个版本各自「克隆下来就是可构建的仓库」，互不影响。

**没有重写任何历史。** `avalonia` 从 `main` 的最新提交 `7c64d2c` 派生；
`main` 与两个发布标签（`v0.2.0` / `v0.4.0`）保持原样。本分支的推送是**新增一条分支引用**，
不带 `--force`，不触碰任何既有引用。

**两个分支的差异只落在 App 层。** `src/AnimeDownloader.Core` 与 `tests/AnimeDownloader.Core.Tests`
在两个分支里是同一套代码；要看前端到底改了什么，diff 这一个目录就够：

```bash
git fetch origin
git diff --stat origin/main..origin/avalonia -- src/AnimeDownloader.App
```

**取用本分支：**

```bash
git clone -b avalonia git@github.com:koiflan-514/AnimeDownloader.git
```

---

## 项目简介

同一个应用的第二个前端。**Core 不动、功能不删、视觉不降** —— 只重写「框架强绑定」的那一层：
XAML、控件、窗口外壳、平台服务。业务逻辑（图源协议、代理隧道、下载器、缩略图缓存、标签联想）
在两个分支里是同一份代码，因此行为天然一致，单元测试也直接共用。

| | `main`（WinUI 3） | **`avalonia`（本分支）** |
| --- | --- | --- |
| App 目标框架 | `net10.0-windows10.0.19041.0` | `net10.0`（平台中立） |
| UI 框架 | WinUI 3 / Windows App SDK 1.8 | Avalonia 12.1.2 |
| 运行环境额外要求 | Windows App SDK（未打包部署时随输出分发） | 无（只用 .NET 运行时） |
| Core 层 | `Models` / `Sources` / `Services` | **同一套代码**（`src/AnimeDownloader.Core`） |
| 单元测试 | 复用 | 复用（xUnit，不联网，81 个用例） |
| 构建产物 | `AnimeDownloader.App.exe` | `AnimeDownloader.exe` |

> 目标框架是平台中立的，Windows 专属能力（读注册表取系统强调色色阶、DWM 标题栏着色）
> 都收敛在 `Platform/` 下并用 `OperatingSystem.IsWindows()` 分支，非 Windows 上有回退实现。
> **主力验证平台是 Windows 10/11 x64；Linux 侧已在 WSL2 的 Ubuntu、Fedora 与 Arch 上实测可用**
> （见 [Linux 适配度](#linux-适配度)），macOS 与真机 Linux 仍属「能编译、未验证」。

---

## 依赖与运行环境

### 运行环境（跑构建产物所需）

| 项 | 要求 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 10 1809+ / Windows 11，**x64**；Linux x64 | Windows 侧做过逐页验收；Linux 侧已在 WSL2 的 Ubuntu / Fedora / Arch 上实测（见 [Linux 适配度](#linux-适配度)） |
| .NET 运行时 | **.NET 10 Runtime**（`Microsoft.NETCore.App` 10.0） | 构建产物是框架依赖部署（`runtimeconfig.json` 只声明 `Microsoft.NETCore.App`）。**不需要 Windows Desktop Runtime，也不需要 Windows App SDK** —— 这是与 `main` 分支在部署上最大的差别 |
| 字体 | 无强制要求 | 界面数字走等宽字体，优先 `Cascadia Mono`，未安装时回退 `Consolas` → `Microsoft YaHei UI`。Windows 上这几个通常都在；Linux 上由 fontconfig 代换（中文落到 `Noto Sans CJK`，不会缺字）。**图标字形不依赖系统字体** —— 已把等码位的开源图标字体打进程序集，见 [Linux 适配度](#linux-适配度) |

### 构建环境

| 项 | 要求 |
| --- | --- |
| .NET SDK | **.NET 10 SDK**（开发与验证使用 `10.0.401`） |
| 编辑器 | 任意。Visual Studio / Rider / VS Code 均可（VS Code 建议装 Avalonia 扩展以获得 XAML 补全） |
| 额外 workload | 无 |

### 依赖的 NuGet 包（全部来自 nuget.org，见 `NuGet.Config`）

| 项目 | 包 | 版本 |
| --- | --- | --- |
| `AnimeDownloader.App` | `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` | 12.1.2 |
| `AnimeDownloader.Core.Tests` | `xunit` / `xunit.runner.visualstudio` | 2.9.3 / 3.1.4 |
| `AnimeDownloader.Core.Tests` | `Microsoft.NET.Test.Sdk` / `coverlet.collector` | 17.14.1 / 6.0.4 |
| `AnimeDownloader.Core` | **无第三方包** | 内嵌资源 `Resources/tag_zh.json` |

> `Avalonia.Diagnostics`（F12 调试面板）在 12.1.2 这一档 **nuget 上没有发布对应包**，
> 引入会让整个 restore 直接失败（NU1102）。本项目也不需要它 —— 验证走的是
> 「跑起来 + 截图 + 像素探针」那条路，不依赖 DevTools。

---

## Linux 适配度

目标框架是平台中立的 `net10.0`，Core 层零 UI 依赖，Windows 专属能力（系统强调色、DWM 标题栏）
都收敛在 `Platform/` 下并带 `OperatingSystem.IsWindows()` 回退分支 —— 因此**代码是按可跨平台写的**。

**当前实测结论：WSL2 上的三个发行版都能完整跑通。** 原生构建 0 警告 0 错误、
81/81 单元测试通过、GUI 窗口真实出现在 Windows 桌面、启动零输出、图标字形全命中。
差别只剩「开箱即用程度」——三个镜像的「裸」的程度差得很远：

| 发行版 | 一开始缺什么 | 现在的状态 |
| --- | --- | --- |
| Ubuntu 26.04.1 | `libicu` + `libice6` + `libsm6` + `fontconfig` + 中文字体 | 补齐后**不需要任何变通** |
| Fedora Linux 44 | 只有 `libICE` / `libSM`（X11 客户端库它是齐的） | 补齐后不需要任何变通 |
| Arch Linux | **最裸**：X11 五件套 + `libICE`/`libSM` + `fontconfig` 全缺（但中文字体反而是装好的） | 补齐后不需要任何变通 |

下面每一条都是实测（2026-09-21），不是推测。实测环境：WSL `2.7.12.0` / WSLg `1.0.73.2` /
内核 `6.18.33.2-microsoft-standard-WSL2`，三个发行版均为 **x86_64**。

### 发行版适配状态

| 发行版 | 版本 | 适配状态 | 实测到哪一步 |
| --- | --- | --- | --- |
| **Ubuntu** | 26.04.1 LTS（Resolute Raccoon） | **✅ 全程通过，无需变通** | 系统库齐备（`libicu78` / `libice6` / `libsm6` / `fontconfig` / 全套 X11 / 中文字体）后，构建 0 警告 0 错误、测试 81/81、窗口正常出现 |
| **Fedora Linux** | 44（WSL） | **✅ 全程通过，无需变通** | 补上 `libICE` / `libSM`（唯一的缺口）后，同样构建 0 警告 0 错误、测试 81/81、窗口正常出现 |
| **Arch Linux** | 滚动版（WSL） | **✅ 全程通过，无需变通** | 补上 `fontconfig` + `libICE` / `libSM` + X11 客户端库（共 11 个包）后，同样构建 0 警告 0 错误、测试 81/81、窗口正常出现 |

> 三者里只有 **Arch 开箱就带 .NET**（它的官方仓库有 `dotnet-sdk-10.0`）；Ubuntu 与 Fedora 的
> WSL 镜像都不预装 .NET，需要先装 SDK 才能真正走「原生构建 + 单测」这条路。
> 实测用的 .NET 10 SDK 版本：Ubuntu `10.0.112` / Fedora `10.0.111` / Arch `10.0.112`。

### 哪些库是「启动必需」的（逐项实测）

用「把一个 `.so` 从库里拿掉、再启动一次，看 15 秒后进程还在不在」这个办法逐项验过。
结果分三档 —— 注意**「缺了也能启动」不等于「不该装」**，Avalonia 对多数 X11 扩展库是
**按需加载**的：

| 库 | 启动是否必需 | 说明 |
| --- | --- | --- |
| `libfontconfig.so.1` | **必需** | 缺了 SkiaSharp 直接加载失败 —— **最裸镜像上的第一道坎** |
| `libICE.so.6` / `libSM.so.6` | **必需** | Avalonia 的 X11 后端硬依赖 |
| `libX11.so.6` | **必需** | X11 通信本身 |
| `libXext` / `libXrender` / `libXfixes` | 启动不吃，建议装 | 随 `libxcursor` / `libxrandr` 一起被拉进来 |
| `libXcursor` / `libXrandr` / `libXi` / `libxkbcommon` | 启动不吃，建议装 | 缺了会退掉光标主题 / DPI 变化通知 / 触控 / 键盘布局映射 |
| `libGL.so.1` | 一直缺也无所谓 | WSLg 下走软件渲染，实测不影响启动 |


### 已验证的功能

| 项 | Ubuntu 26.04.1 | Fedora Linux 44 | Arch Linux |
| --- | --- | --- | --- |
| 原生构建 `dotnet build AnimeDownloader.slnx -c Debug` | **0 警告 0 错误** | **0 警告 0 错误** | **0 警告 0 错误** |
| 原生单元测试 `dotnet test` | **通过 81 / 失败 0** | **通过 81 / 失败 0** | **通过 81 / 失败 0** |
| GUI 窗口 | `AnimeDownloader (Ubuntu)` 出现 | `AnimeDownloader (FedoraLinux-44)` 出现 | `AnimeDownloader (archlinux)` 出现 |
| 启动输出 | stdout / stderr **各 0 字节** | 同左 | 同左 |
| 进程存活 | 满 70 秒无退出 | 正常（未单独计时） | **满 170 秒无退出** |
| 中文字体覆盖 | 30 个 CJK 字体族，`fc-match ":charset=4e2d"` 命中 `Noto Sans CJK` | 27 个，同样命中 | 80 个，同样命中 |
| 图标字形 | 19/19 命中内置图标字体（解析为 `Symbols`） | 19/19 | 19/19 |
| 交叉发布 | Windows 侧 `dotnet publish -r linux-x64 --self-contained` 成功，产物自带 .NET 运行时与 Skia / HarfBuzz 原生库 | 同左 | 同左（目标发行版连 .NET 都不用装） |

窗口都由 WSLg 的 `msrdc.exe` 承载，标题带发行版后缀 —— 多个窗口可以同时开着。
Core 与测试**原样复用**：81 个用例在三个发行版上都是 81/81，**没有为 Linux 改过一行业务代码**。

> 判定窗口用的是 Windows 侧的 `Get-Process msrdc` 标题。**WSLg 对每个发行版只起一个常驻 `msrdc`**
> （PID 从头到尾不变，实测是同一个 10120），标题反映的是「当前显示的窗口」而不是进程身份 ——
> 所以**别拿 PID 判断新旧**，要用「杀掉应用后标题是否清空」做一次反证。本项目这么验过：
> 应用停止后标题确实变成空，说明抓到的标题属于当时正在运行的那个窗口。


**没验证的也要说清楚**：三页（画廊 / 灯箱 / 设置）的实际交互、图片加载与下载、批量下载、
代理隧道，**都只在 Windows 上做过逐页验收**；Linux 侧验证到「构建 + 测试 + 窗口成功创建并呈现」。
字体覆盖是用 `fc-match` 查字形（确认不会缺字），但没有做逐像素的界面比对。

### 图标：靠内置字体兜底

界面上的二十多个图标**全部**是 `Segoe Fluent Icons` 的私用区（PUA）字形，而该字体是
**Windows 专有**的。在 Linux / macOS 上它不存在，字体管理器会把这条字族代换成
**某个不含这些码位的字体**，于是**整组图标一起变空白**。
这一条特别难查：字符有内容、占位也正确，光看布局几乎发现不了。

> 代换成谁**是环境相关的**，别把具体名字写死进结论：同一条字族链，实测 Ubuntu / Fedora 上
> 解析成 `Noto Sans`、Arch 上（装齐依赖后）也是 `Noto Sans`，而 Arch 在**缺 fontconfig 配置**时
> 曾解析成 **`Noto Color Emoji`**。三次都是 **0/19 命中**。要写就写「解析成一个不含这些码位的
> 字体，命中 0」。顺带这也就回答了「能不能拿 emoji 字体顶替」：**不能** —— 它即使被选中也是 0 命中。

修法是往程序集里打一份**码位与 Segoe 一致**的开源替代字体（Uno FluentUI Assets，
Apache-2.0），字族链改为 `内置字体 → Segoe Fluent Icons → Segoe MDL2 Assets`：

| 字族写法 | 解析结果 | 图标命中 |
| --- | --- | --- |
| 修复前：只写 `Segoe Fluent Icons, Segoe MDL2 Assets` | 一个不含 PUA 字形的字体 | **0 / 19** |
| 现在：链首内置字体 | 内置的 `Symbols` | **19 / 19** |

这张表来自实测，而且**不是靠截图** —— 是直接问 Avalonia 的字体解析结果
（`FontManager` → `GlyphTypeface.CharacterToGlyphMap`），三个发行版上都是全命中，
对照组（只写 Segoe）则稳定是 `0/19`。同一份数据同时证明了「测试有区分力」与「修复生效」，
也正因为对照组能稳定复现出 `0/19`，这条结论才算落地。


> 内置字体覆盖 **1410** 个码位，`Segoe Fluent Icons` 是 **1999** 个 —— **它是子集，不是超集**。
> 实测 `E9A4` 在 Segoe 里有、在内置字体里是空的。新增图标时必须逐码位实测，不能照抄对照表。

### 顺手清掉的四个码位笔误（与 Linux 无关，Windows 上同样错）

排查「图标空白」时把全部在用码位对着官方名字表过了一遍，发现四处**码位存在、字形也渲染得出来、
但画的根本不是那个意思**的笔误 —— 这类问题「字形有没有」的检查全绿，只有把字形画出来才看得见：

| 用途 | 原来是 | 官方名 → 实际画的是 | 现在改用 |
| --- | --- | --- | --- |
| 拉伸填充 | `E78F` | **不存在** → 空白（Windows 上也空白） | `E799` AspectRatio |
| 缩小（Ctrl+-） | `E8A2` | **AttachCamera** → 一台相机 | `E71F` ZoomOut |
| 适应屏幕（Ctrl+0） | `E7C3` | **Page** → 一张文档 | `E9A6` FitPage |
| 实际大小 | `E738` | **Remove** → 一根短横线 | 等宽文字 **`1:1`**（不再占码位） |

> 「实际大小」没有改成别的码位，是因为 Segoe 里**没有**「1:1 / ActualSize」这个语义的字形
> （`E71E` Zoom 是放大镜加号，会与放大重复）。改成等宽数字既零歧义，也正好符合本设计
> 「所有数字走等宽」的既有规则 —— 与同排的 `100%` 读数同族。
>
> 余下 16 个码位语义都正确（`E721` Search 用在标签搜索框前缀是对的）。官方码位对照表：
> <https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font>

> 内置字体是本仓库唯一的第三方二进制资产，Apache-2.0；许可原文随字体一起放在
> `src/AnimeDownloader.App/Assets/Fonts/LICENSE-Uno-FluentUI-Assets.md`，字体文件未做任何修改。

### 已知限制

| 限制 | 实测现象 | 处理 |
| --- | --- | --- |
| **缺 `libICE` / `libSM`** | 启动即抛 `DllNotFoundException: libICE.so.6`，栈顶在 `ICELib.IceAddConnectionWatch` —— Avalonia 的 X11 后端**硬依赖**这两个库。Fedora 与 Arch 的 WSL 镜像默认都没有 | 装 `libICE libSM`；或把两个 `.so` 放到任意目录，运行时用行内 `LD_LIBRARY_PATH` 指过去 |
| 缺 `libicu` | .NET 直接 `FailFast` 退出，退出码 **134**，报 `Couldn't find a valid ICU package installed on the system` | 装 `libicu`；或设 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`（代价是失去区域性支持） |
| 缺 `fontconfig` | SkiaSharp 原生库加载失败。**这是最裸镜像（Arch）上的第一道坎** —— 比 X11 更早爆，因为 Skia 一初始化就要它 | 装 `fontconfig` |
| 缺 X11 客户端库 | 与缺 `libICE` 同类；`libX11` 缺了必崩，`libXcursor` / `libXrandr` / `libXi` / `libxkbcommon` 则是**按需加载**（缺了能启动，但退掉光标主题 / DPI 变化通知 / 触控 / 键盘布局） | 装 `libx11 libxcursor libxrandr libxi libxext libxrender libxfixes libxkbcommon` |
| 缺中文字体 | 界面字族（含 `Microsoft YaHei UI`）经 `fc-match` 全落到 `DejaVu Sans` —— 该字体**不含中文字形**，中文会显示成方框 | 装 `fonts-noto-cjk` / `google-noto-sans-cjk-fonts`。**注意 `fc-list :lang=zh \| wc -l` = 0 可能是假阴性**：Arch 上 `noto-fonts-cjk` 明明装着，但 fontconfig 没装（`fc-list` 命令本身不存在，连 `/etc/fonts` 都没有），指标照样是 0 —— 先确认 `command -v fc-match` 有输出，再看字体指标 |
| 无 GPU 加速 | 窗口标题**有时**被 WSLg 加上 `[WARN:COPY MODE]` 前缀（RDP 呈现层退到软件渲染「拷贝模式」） | 呈现层行为，不影响功能；时有时无。三个发行版实测都不需要 `libGL`（没有也照跑） |
| Fedora 的 `systemd` 用户会话 | **冷启动**进入时提示 `Failed to start the systemd user session for 'kokoro'` | 与该镜像的 systemd 配置有关，本应用不依赖它，命令与 GUI 都不受影响 |
| 非 Windows 回退分支未做像素级验收 | 系统强调色改由 `IPlatformSettings` 派生、DWM 标题栏着色被跳过 | 功能可用、界面能起，但未逐像素比对 |

### 在 WSL2 上跑起来

两条路都实测过：**A) 在发行版内原生构建**（推荐 —— 顺带能把那 81 个测试跑掉）；
**B) 在 Windows 侧交叉发布自包含产物**（目标发行版连 .NET 都不用装）。

```bash
# A) 发行版内原生构建、测试、启动（三个发行版通用；仓库在 /mnt/<盘符>/ 下即可）
cd /mnt/d/Downloder/AnimeDownloader-Avalonia
dotnet build AnimeDownloader.slnx -c Debug          # 期望：0 警告 0 错误
dotnet test  AnimeDownloader.slnx -c Debug          # 期望：通过 81 / 失败 0
src/AnimeDownloader.App/bin/x64/Debug/net10.0/AnimeDownloader

# B) Windows 侧交叉发布（-r linux-x64，产物自带运行时）
dotnet publish src/AnimeDownloader.App/AnimeDownloader.App.csproj \
  -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
```

系统库是唯一的额外步骤（`DISPLAY` 由 WSLg 提供，不需要配置）：

```bash
# Ubuntu / Debian 系：镜像默认四样都缺，一次补齐
sudo apt install -y libicu78 libice6 libsm6 fontconfig fonts-noto-cjk

# Fedora 系：只差 libICE / libSM（libicu、fontconfig、libX11、中文字体都已齐）
sudo dnf install -y libICE libSM
# 换新镜像时的完整清单：
#   sudo dnf install -y libicu fontconfig libICE libSM \
#     libX11 libXcursor libXrandr libXi libXext google-noto-sans-cjk-fonts

# Arch Linux：镜像最裸，X11 客户端库 + libICE/libSM + fontconfig 全缺
# （中文字体 noto-fonts-cjk 反而是装好的，缺的只是发现它们的 fontconfig）
sudo pacman -S --needed fontconfig libice libsm libx11 libxext libxrender \
    libxfixes libxcursor libxrandr libxi libxkbcommon
# freetype2 / libpng / libxcb / xorgproto / xkeyboard-config 等由 pacman 自动拉齐。
# libicu 不用装：Arch 的 dotnet-sdk-10.0 会把 icu 作为依赖带上来。

# 真装不上 libICE / libSM 时的变通：把这两个 .so 放好，运行时指过去
LD_LIBRARY_PATH=$HOME/extra-libs ./AnimeDownloader
```


> 若系统装不了 `libicu`，可以退化运行：`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ./AnimeDownloader`。
> 这条路实测可行，但会失去区域性支持，只建议用于验证界面，不作为日常使用方式。

---

## 构建与启动

```bash
# 1) 取本分支（main 是 WinUI 版，注意 -b avalonia）
git clone -b avalonia git@github.com:koiflan-514/AnimeDownloader.git
cd AnimeDownloader

# 2) 还原
dotnet restore

# 3) 构建 —— 实测 0 个警告，0 个错误
dotnet build AnimeDownloader.slnx -c Debug

# 4) 单元测试 —— 实测 81 通过 / 0 失败（不联网）
dotnet test AnimeDownloader.slnx -c Debug

# 5) 启动
dotnet run --project src/AnimeDownloader.App -c Debug
```

也可以直接启动构建产物 —— 注意 `bin` 下多一层平台目录，因为解决方案把 App 项目固定为 `x64`：

```
src\AnimeDownloader.App\bin\x64\Debug\net10.0\AnimeDownloader.exe
```

几点说明：

- 单独构建 App 项目（`dotnet build src/AnimeDownloader.App/AnimeDownloader.App.csproj -c Debug`）
  会落到 `bin\Debug\net10.0\`，两种写法都可用。
- `dotnet run` 起的是 GUI 进程，把输出重定向到文件时**不会有任何 stdout** ——
  别用「日志为空」判断它没起来，要查进程。
- 构建开启了 `TreatWarningsAsErrors`，分析级别 `latest-recommended`，`Nullable` 全开，`Deterministic` 打开。
  任何警告都会中断构建，这是刻意的。

---

## 项目结构

```
AnimeDownloader/                       # 仓库根（@ avalonia 分支）
├── AnimeDownloader.slnx               # .NET 10 解决方案（新 XML 格式）
├── Directory.Build.props              # 公共编译属性（Nullable、警告即错误、确定性构建、版本号）
├── NuGet.Config                       # 固定包源，清掉机器级 fallback 目录
├── src/
│   ├── AnimeDownloader.Core/          # 纯 .NET 类库：零 UI 依赖，两个分支共用
│   │   ├── Models/                    # NsfwMode、ImageItem、ImageTag
│   │   ├── Sources/                   # IImageSource + ImageSourceBase / SourceRegistry + 9 个图源适配器
│   │   ├── Services/                  # 设置持久化、代理检测、SOCKS5 隧道、HttpClient 工厂、
│   │   │                              # 下载器、缩略图解析、批量下载、标签联想与本地化、连通性探测
│   │   └── Resources/tag_zh.json      # 内嵌的 5000 条热门标签中英对照表
│   └── AnimeDownloader.App/           # Avalonia 12 应用（本分支重写的部分）
│       ├── Program.cs                 # AppBuilder 入口（Avalonia 没有生成代码注入的 Main）
│       ├── App.axaml(.cs)             # 资源装配 + FluentTheme + 全局 Style
│       ├── MainWindow.axaml(.cs)      # 外壳：56px 导轨 + 常驻命令条 + 页面缓存 + 全屏三层
│       ├── Views/                     # GalleryPage / ViewerPage / SettingsPage + PageContracts
│       ├── Controls/                  # ImageCard、GalleryViewerWindow、SegmentedControl、
│       │                              # TileWrapPanel、ConfirmDialog、InfoBar、ProgressRing、FontIcon
│       ├── Platform/                  # WindowChrome（标题栏 / 最小尺寸 / 全屏）
│       │                              # WindowsColorScheme（强调色色阶 + 墨色自动判定）
│       ├── Styles/                    # Tokens / FluentOverrides / AppStyles / AppElementStyles
│       └── Assets/Fonts/              # 内置图标字体（Apache-2.0）+ 许可原文
├── design/
│   ├── DESIGN.md                      # 暗房设计语言规范（色彩 / 字体 / 布局 / 组件 / 动效）
│   └── preview.html                   # 可交互样张（浏览器打开）
├── docs/
│   ├── ARCHITECTURE.md                # 分层职责、关键设计决策、图源协议
│   └── RELEASE-v0.4.0.md              # v0.4.0 发布说明
├── icons/                             # 应用图标（.ico 嵌入 exe，.png 供窗口与导轨标记使用）
└── tests/
    └── AnimeDownloader.Core.Tests/    # xUnit 测试（不联网，两个分支共用）
```

`main` 分支另有、本分支**不含**的内容：`docs/images/`（README 截图）与 `tools/`
（图标生成器 `IconGen`、联网冒烟工具 `SourceSmoke`、WiX 安装包脚本）——
它们服务于 WinUI 版的分发方式，与 Avalonia 版无关。

### 目录职责速查

| 目录 | 职责 | 改动频率 |
| --- | --- | --- |
| `src/AnimeDownloader.Core` | 图源协议、代理隧道、下载器、缓存、标签联想。**零 UI 依赖**，因此能在 `net10.0` 下独立测试 | 低（两个分支共用） |
| `src/AnimeDownloader.App` | 全部界面与平台适配。本分支重写的就是这里 | 高 |
| `src/AnimeDownloader.App/Styles` | 设计令牌与控件主题（下文有四个文件的分工表） | 中 |
| `src/AnimeDownloader.App/Platform` | 唯一允许出现 `OperatingSystem.IsWindows()` 的地方 | 低 |
| `design/` | 设计规范与可交互样张。**视觉有争议时以它为准** | 低 |
| `tests/AnimeDownloader.Core.Tests` | 网络无关逻辑的 xUnit 覆盖，不联网 | 中 |

### 样式文件的分工

四个文件，职责不重叠 —— 搞混了最容易出现「改了没生效」：

| 文件 | 内容 | 装配位置 |
| --- | --- | --- |
| `Styles/Tokens.axaml` | 设计令牌：表面色阶、文本、强调色、状态色（纯数据，不引用兄弟字典） | `Application.Resources` |
| `Styles/FluentOverrides.axaml` | 把 Fluent 的中性灰阶收进暗房的炭灰阶梯 | `Application.Resources` |
| `Styles/AppStyles.axaml` | **带 `x:Key`** 的 `ControlTheme`：按钮、分段器、卡片、状态栏、设置行… | `Application.Resources` |
| `Styles/AppElementStyles.axaml` | **无 `x:Key`** 的全局隐式 `Style`：`ComboBox` / `TextBox` / `ToolTip` 这类原生控件的形状与字号归位 | `Application.Styles`，**必须排在 `FluentTheme` 之后** |

---

## 亮点

| | |
| --- | --- |
| **9 个图源开箱可用** | 随机 / 分页两种浏览模式，可标签搜索、可按 NSFW 分级过滤；新增图源只需实现一个适配器 |
| **配色等于你的 Windows** | 强调色取自系统 Accent Color，运行中改 Windows 强调色或深浅色，应用即时跟随 |
| **标签看得懂** | 灯箱展示全部标签（中文译名 + 类别圆点）；输入框按热度联想、勾选即加入搜索；内嵌 5000 条热门标签中英对照表，离线也能联想 |
| **网络真的能连上** | 自研 SOCKS5 / HTTP CONNECT 隧道；系统代理探测 → 协议不符回退 → 传输故障再回退直连的三级链路 |
| **省流量、秒回看** | 非灯箱界面只加载缩略图，无原生缩略图的源本地小尺寸解码；内存 LRU + 磁盘两级缓存，重复浏览零网络请求 |
| **暗房设计语言** | 不是「换个配色的 Fluent 模板」：层级只用表面色阶 + 1px 极细线，所有数字走等宽字体 |

---

## 界面

### 设计语言：暗房（Darkroom）

一句话概念：**这个应用是一间暗房** —— 中性冷调的炭灰是「房间」，图片是唯一的光源，
**用户的 Windows 强调色是暗房里的安全灯**。

这个比喻直接决定三条硬规则：

1. **全应用只有一个彩色：系统强调色。** 且只在三种语义上亮起 —— *正在被选中 / 正在进行 / 需要你注意*。
   就绪是灰的（无光源），加载中是强调色（灯亮着），故障是红的。
2. **层级不用阴影，只用表面色阶 + 1px 极细线。** 暗房里没有投影，只有离灯远近不同的灰。
3. **所有数字都用等宽字体** —— 页码、分辨率、缩放百分比、版本、快捷键、路径。

### 强调色跟随 Windows，而不是硬编码品牌色

这里刻意没有定义品牌色。`AppAccent*` 令牌接到 Fluent 的 `SystemAccentColor*` 基线，
再由 `Platform/WindowsColorScheme.cs` 在运行时回写：

| 令牌 | 暗色 | 亮色 | 用途 |
| --- | --- | --- | --- |
| `AppAccentBrush` | `SystemAccentColor` | `SystemAccentColorDark1` | 主操作填充、活动指示、忙碌状态 |
| `AppAccentStrongBrush` | 派生 Light1 | 派生 Dark2 | 悬停 / 按压 |
| `AppAccentSoftBrush` | 强调色 @16% | 强调色 @12% | 选中底衬（导轨项、分段器、标签 chip） |
| `AppAccentLineBrush` | 强调色 @42% | 强调色 @45% | 选中描边、卡片悬停内描边 |
| `AppAccentInkBrush` | 自动取黑或白 | 自动取黑或白 | 压强调色块上的文字 |
| `AppFocusRingBrush` | = 强调色 | = `Dark1` | 焦点环（2px） |

几个刻意的细节：

- **暗色用原值、亮色降一档。** `SystemAccentColor` 是 Windows 为深色底挑的值，
  直接放到浅底上当描边会偏亮、对比不足；亮色模式整体下沉（`Dark1` / `Dark2`）。
- **色阶是在本地算出来的。** Avalonia 只提供 `SystemAccentColor` 一个键（`Light1-3` / `Dark1-3`
  在框架里没有对应物），所以本工程直接从注册表 `AccentPalette` 取 Windows 自己用的那八档；
  取不到时回退到 `IPlatformSettings` 的 `AccentColor1` 并按线性混合派生。
- **底衬始终用系统原色**（亮色下也是）。浅底上 12% 的淡衬需要「发光」，跟着压深会糊成一团灰。
- **墨色按 WCAG 相对亮度自动判定**（阈值 `0.4`）：高于阈值给深墨 `#141518`，低于给白。
  于是无论用户选的是明黄、薄荷还是深靛，实心按钮上的文字都可读。

身份感由**表面色阶 + 等宽数字 + 排版**承担，不依赖某一个具体色相。

> 完整设计规范见 [`design/DESIGN.md`](design/DESIGN.md)；
> [`design/preview.html`](design/preview.html) 是可交互样张（浏览器打开，可真实悬停 / 切换亮暗 / 点选强调色色板）。

---

## 功能

### 图源与浏览

- **9 个图源**（见[图源一览](#图源一览)），统一实现 `IImageSource`，UI 只依赖接口。
- **随机 / 分页两种模式**；不支持分页的源（如 Sakura Random）自动隐藏分页控件并给出提示。
- **画廊网格**：随窗口宽度自适应 **2–8 列**，卡片错峰入场动画、悬浮放大高亮、分辨率徽章、
  艺术家信息条、悬浮快捷保存；栏距 10px 恒定，卡片在瓦片内居中。
- **标签快速搜索**：支持标签的图源显示内联搜索框，回车应用并自动保存，活动标签以可清除胶囊展示。
- **分段式 NSFW 三态过滤**：屏蔽 NSFW / 仅 NSFW / 全部。
- **自动刷新**：可配置间隔。
- **首启引导**：首次运行在画廊底部显示一条可关闭的提示条（「从这里开始」），并提供直达设置的入口。

### 标签体系

- **灯箱全标签展示**：图片的全部标签（含类别：角色 / 画师 / 作品 / 内容…）以圆点标注，
  热门标签带**中文译名**；点击标签直达该标签的画廊视图。
- **智能联想**：按热度联想，显示中文译名 / 原名 / 帖子数，勾选即加入搜索，并给出相关标签推荐。
- **内嵌 5000 条热门标签中英对照表**（`Core/Resources/tag_zh.json`，编译期嵌入），本地联想兜底。

### 灯箱

- 适应屏幕 / 实际大小 / 拉伸填充 / 缩放（25%–400%，`Ctrl` + 滚轮或 `+` / `-`）。
- 空格换图、`Ctrl+S` 保存、`F11` 全屏、`Esc` 退出全屏。
- **一键复制图片直链**；**IQDB / SauceNAO 以图搜图**一键跳转；来源链接直达原帖。
- 左下角作者与分辨率信息胶囊。
- 独立大图窗口（`Controls/GalleryViewerWindow`）：深色沉浸背景 + 底部浮动工具栏，
  支持键盘导航、下一张预加载与全屏。
- 亮色模式下**灯箱台面依然是深的** —— 这是刻意的：看图需要中性深底衬，
  否则浅底会污染对图片明度的判断。

### 下载

- **批量下载整个画廊**到指定文件夹：按 CPU 核数并行铺开（底层并发闸门统一限流），
  带进度与取消，自动跳过已存在文件，逐文件流式写盘 + 原子改名 —— 落盘始终是原图。
- 并发下载限流、瞬时故障自动重试（指数退避）、可配置请求超时、200MB 单文件上限。

### 网络与代理

- **系统代理自动检测**：环境变量 + Windows 注册表 Internet Settings。
- **自研代理隧道**：.NET 不原生支持 `socks5://`，本项目自研实现，统一支持
  **HTTP(S) CONNECT 与 SOCKS5**、代理认证（`user:password@`）与 `no_proxy` 旁路。
- **三级回退链**：自动检测到的系统代理先探测 SOCKS5 → 协议不符回退 HTTP CONNECT →
  节点传输故障 / 假死超时再回退直连。**手动指定的代理则严格遵从**，不擅自绕过。
- **Gelbooru API 凭据**：Gelbooru 已停用匿名 API，在设置页填写 `user_id + api_key` 即可正常搜索。
- 设置页内置**检测源连通性**：逐个探测 9 个图源的直连与代理可达性。

### 性能

- 非灯箱界面**只加载缩略图**：有原生缩略图的源直接用小图；无缩略图的源（nekos.moe / dmoe.cc / waifu.im）
  由 `ThumbnailResolver` 回退到原图字节，首次取回后按小尺寸解码并写入**内存 LRU + 磁盘两级缓存**，
  之后重复浏览零网络请求；设置中可配置 `{url}` 压缩代理模板进一步降低首载流量。
- 卡片位图按实际显示尺寸**有界解码**（`Bitmap.DecodeToWidth`，180–360px 之间按显示尺寸取），
  不会为了显示 200px 的卡片解码一张 4K 原图。
- 网格用 `ScrollViewer` + 自写的 `TileWrapPanel` 平铺（Avalonia 侧没有 WinUI `GridView` 的容器回收），
  单页最多 48 张、一次性铺开，开销由缩略图两级缓存与有界解码兜住。

### 界面与个性化

- 主题三态：**跟随系统 / 浅色 / 深色**；窗口最小尺寸 980×620（逻辑像素）。
- **全屏（F11）是三层一起收**：窗口呈现器、外壳（导轨列宽归零 + 命令条）、页内控件
  同时进入沉浸态；`Esc` 退出时还原进全屏前的窗口几何与最大化状态。
- 设置持久化为 JSON，保存后立即生效。

---

## 键盘快捷键

| 快捷键 | 作用 |
| --- | --- |
| `←` / `→` | 上一页 / 下一页 |
| `Space` | 换一张（灯箱）/ 刷新当前批（画廊） |
| `Ctrl` + `S` | 保存当前图片 |
| `Ctrl` + `0` | 适应屏幕 |
| `Ctrl` + `+` / `Ctrl` + `-` | 放大 / 缩小 |
| `F11` | 全屏（进入 / 退出） |
| `Esc` | 退出全屏 |

导轨底部有一枚**快捷键速查**按钮，运行时随时可查。

---

## 图源一览

| ID | 显示名 | 端点 | 分页 | 标签 |
| --- | --- | --- | --- | --- |
| `nekosmoe` | Catgirl | `https://nekos.moe/api/v1/random/image` | 否 | 否 |
| `waifuim` | Waifu | `https://api.waifu.im/images` | 是 | 否 |
| `danbooru` | Danbooru | `https://danbooru.donmai.us/posts.json` | 是 | 是 |
| `lolicon` | Lolicon | `https://api.lolicon.app/setu/v2` | 否 | 是 |
| `dmoe` | Sakura Random | `https://www.dmoe.cc/random.php` | 否 | 否 |
| `safebooru` | Safebooru | `https://safebooru.org/index.php` | 是 | 是 |
| `gelbooru` | Gelbooru | `https://gelbooru.com/index.php` | 是 | 是 |
| `konachan` | Konachan | `https://konachan.com/post.json` | 是 | 是 |
| `yandere` | Yande.re | `https://yande.re/post.json` | 是 | 是 |

- **Lolicon / Sakura Random** 国内可直连。
- **Gelbooru** 需要在设置页填写 API 凭据。
- **Danbooru** 会自动过滤受限标签，设置页实时提示。
- 国内访问 **Danbooru / Gelbooru / Konachan / Yande.re** 通常需要代理，可用设置页的「检测源连通性」确认。

---

## 配置与数据目录

默认写入 `%LocalAppData%\AnimeDownloader`：

| 文件 / 目录 | 内容 |
| --- | --- |
| `config.json` | 全部设置（图源、NSFW、主题、代理、下载目录、并发数…） |
| 缩略图缓存目录 | 磁盘级缩略图缓存 |

配置文件损坏时会**自动备份原文件**并回退默认值，不会静默清空。

可选：设置环境变量 `ANIMEDOWNLOADER_CONFIG_DIR` 可把配置与缓存目录重定向到指定位置，便于便携部署。

```cmd
set ANIMEDOWNLOADER_CONFIG_DIR=D:\Portable\AnimeDownloader
```

> **两个分支共用同一份配置。** `avalonia` 与 `main` 读写的是同一个 `config.json`，
> 因此可以交替启动两个版本对比界面，设置不会互相重置。

---

## 测试

`tests/AnimeDownloader.Core.Tests` 为 xUnit 测试，**不联网** —— 网络无关逻辑
（参数构造、URL 生成、配置读写、缓存、下载器、文件名建议、标签联想与本地化、代理回退）
全部下沉到 Core 层：

```bash
dotnet test AnimeDownloader.slnx -c Debug
```

实测输出：`已通过! - 失败: 0，通过: 81，已跳过: 0，总计: 81`。

Core 是纯 `net10.0` 类库、零 UI 依赖，所以这套测试**两个分支共用**，不需要为 Avalonia 重写。

---

## 迁移说明：与 WinUI 版的差异

只列「机制上必须换掉」的部分 —— 业务逻辑没有任何对应改动：

| 能力 | WinUI 3（`main`） | Avalonia 12（本分支） |
| --- | --- | --- |
| 页面导航 | `Frame` + 页面缓存 | `ContentControl` + 代码持有页面实例字典（切页不重建，且不引入导航栈） |
| 网格容器 | `GridView` + `ItemsWrapGrid`（带容器回收） | `ScrollViewer` + 自写 `TileWrapPanel`（照 `ItemsWrapGrid` 的测量语义写，单页一次性铺开） |
| 全屏快捷键 | 窗口级 `KeyboardAccelerator` | `Window.KeyDown`（路由事件从焦点元素冒泡到窗口，焦点在哪儿都能收到） |
| 全屏实现 | `AppWindow.SetPresenter(FullScreen)`，需自己存/还原几何 | `WindowState.FullScreen`，窗口对象不被替换，`MinWidth/MinHeight` 一直有效 |
| 最小尺寸 | `PreferredMinimumWidth` 收**物理像素**，逻辑值要乘 `RasterizationScale` | `Window.MinWidth` 本来就是 DIP，框架替我们换算 |
| 系统强调色 | 框架内建 `SystemAccentColor` + `Light1-3` / `Dark1-3` 六个派生阶 | 只有 `SystemAccentColor` 一个键，**整条色阶要自己算**（读注册表 `AccentPalette`） |
| 模态确认框 | `ContentDialog` | 自绘 `ConfirmDialog`，作为根 Grid 最后一个子元素铺满整窗 |
| 侧边导航 | `NavigationView`（收起态会裁内容、挤压菜单项） | 手绘 56px `Grid` 导轨，只保留图标 + 2px 活动指示条 |
| 主题字典 | `XamlControlsResources` + `ThemeDictionaries` | `FluentTheme` + `Tokens.axaml`（亮暗各一套，`RequestedThemeVariant="Default"` 跟随系统） |
| 部署依赖 | 未打包部署需随输出分发 Windows App SDK | 只需 .NET 10 运行时 |
| 调试面板 | WinUI 侧无 | **没有 Avalonia.Diagnostics** —— 12.1.2 档 nuget 上没有对应包（NU1102），引入会让 restore 直接失败 |

迁移过程中被实机验证抓出来的一批框架陷阱（都需要专门规避），已整理成技能与文档，
不在 README 展开：见 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) 与
[`design/DESIGN.md`](design/DESIGN.md) 的「坑位记录」。

### 验收方式

构建通过只是底线。本项目在迁移后**跑真实窗口**做了逐页验收：
UI Automation 按 `AutomationId` 遍历元素树 + 逐页截图 + 像素采样，
校验项包括强调色是否等于系统强调色（读注册表比对）、导轨宽度是否被内容压住、
栏距与列数、设置页是否居中、以及 F11 全屏时外壳与页内控件是否**双双离开 UIA 树**
（`6/6 → 0/6 → 6/6`）。这类「静态看不出来、只有真跑一次才暴露」的问题，
在迁移里抓到过不止一个（例如无限循环动画会让进程直接崩掉）。

---

## 相关文档

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) —— 分层职责、关键设计决策、图源协议
- [`design/DESIGN.md`](design/DESIGN.md) —— 暗房设计语言、色彩 / 字体 / 布局规范、组件与动效
- [`design/preview.html`](design/preview.html) —— 1:1 可交互样张
- [`docs/RELEASE-v0.4.0.md`](docs/RELEASE-v0.4.0.md) —— v0.4.0 发布说明

---

## 许可

GPL-3.0-or-later（与参考项目一致）。

仓库里另有一份**第三方二进制资产**：`src/AnimeDownloader.App/Assets/Fonts/uno-fluentui-assets.ttf`
（Uno FluentUI Assets，Copyright © nventive，**Apache-2.0**）。它是为了让非 Windows 平台也能显示
图标字形而内置的，字体文件**未做任何修改**；许可原文见同目录下的
`LICENSE-Uno-FluentUI-Assets.md`（随程序集一起分发以满足 Apache-2.0 第 4 条）。
