# AnimeDownloader · Android

> 基于 **.NET 10 for Android + 原生 Android View** 的动漫图片浏览与下载应用。
> 功能参考 [CatgirlDownloader](https://github.com/NyarchLinux/CatgirlDownloader)，界面与实现均为全新设计。

![平台](https://img.shields.io/badge/platform-Android%207.0%2B-3ddc84)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![界面](https://img.shields.io/badge/UI-%E5%8E%9F%E7%94%9F%20Android%20View-3ddc84)
![版本](https://img.shields.io/badge/version-0.4.0-6e7681)
![许可](https://img.shields.io/badge/license-GPL--3.0--or--later-3fb950)

| 画廊 | 图片查看 | 设置 |
| --- | --- | --- |
| ![画廊](docs/images/gallery.png) | ![查看](docs/images/viewer.png) | ![设置](docs/images/settings.png) |

---

## 这是哪条分支

本分支是 **Android 版**。同一个仓库里还有两套桌面实现，三者的关系是：

| 分支 | 形态 | 说明 |
| --- | --- | --- |
| [`main`](https://github.com/koiflan-514/AnimeDownloader/tree/main) | WinUI 3 桌面应用 | **主线**，Windows 10 1809+，原生 Mica 材质 |
| [`avalonia`](https://github.com/koiflan-514/AnimeDownloader/tree/avalonia) | Avalonia 桌面应用 | 同一套界面的跨平台重写（Windows / Linux） |
| `android`（本分支） | .NET for Android | 手机端，触控优先的独立版式 |

三条分支共用同一份 `AnimeDownloader.Core`（图源适配器、设置、代理探测、标签体系）
与 `AnimeDownloader.Download`（下载编排）—— 平台差异全部收敛在各自的应用层。
所以**改 Core 要三边一起改**，这也是把它们放在同一个仓库而不是拆成三个仓库的原因。

想看两个版本之间的差异：

```bash
git diff main..android -- src/AnimeDownloader.App
```

---

## 目录

- [亮点](#亮点)
- [界面](#界面)
- [功能](#功能)
- [构建与运行](#构建与运行)
- [部署到真机](#部署到真机)
- [项目结构](#项目结构)
- [配置与数据目录](#配置与数据目录)
- [测试](#测试)
- [常见问题](#常见问题)
- [相关文档](#相关文档)
- [许可](#许可)

---

## 亮点

| | |
| --- | --- |
| **与桌面版同源** | 图源、设置、代理、标签全部复用桌面版那套 Core；手机端只重写了界面与触控，不另起一套逻辑 |
| **9 个图源开箱可用** | 随机 / 分页两种浏览模式，可标签搜索、可按 NSFW 分级过滤 |
| **为「别 OOM」而设计** | 缩略图先读尺寸再按 2 的幂次采样解码（1500×2000 解到卡片上内存降到 1/64），位图按**字节数**做 LRU，并限 3 路并发解码 |
| **手势是原生实现** | 双指捏合 / 双击缩放、放大态平移带惯性、未放大时下滑关闭、横滑切换 —— 全部走 Android 原生手势检测器，没有自己算指针 |
| **后台下载** | 前台服务 + 通知，带批量进度；可设「仅 Wi-Fi 下载」「存进系统相册 / 应用目录」 |
| **配色跟随系统** | 强调色取自系统的动态配色（Material You，API 31+）或主题 `colorAccent`，绝不硬编码品牌色 |
| **深色是一等公民** | 暗房设计语言：三阶炭灰表面 + 1px 极细线，层级不用阴影；所有数字走等宽 |

---

## 界面

### 设计语言：暗房（Darkroom）

与桌面版同一套语言，但**手机上的暗房是深色的**，不跟随系统切浅色 —— 原因是手机大多在暗环境
使用、OLED 省电，且浅色下三阶表面在 6 英寸屏上几乎分不出层次。

- **强调色**：API 31+ 读系统动态配色（`system_accent1_500`），更低版本读主题 `colorAccent`。
  用户换壁纸、换系统主题，应用跟着变 —— 应用不该有自己的颜色。
- **层级**：只有表面色阶（`#0F0F10` / `#18181A` / `#232325`）+ 1px 极细线（`#2E2E30`）。
  **没有阴影，没有投影**；`Elevation` 一律显式归零。
- **状态三档**：灰 = 安静 / 强调色 = 工作 / 红 = 故障。互斥且优先级固定。
- **数字全部等宽**：分辨率、计数、百分比、版本、路径 —— 这条几乎不花成本，
  却立刻把界面从「网页」拉到「仪器」。
- **图标**：用系统字体里的单色符号（几何图形、箭头、dingbats），**不塞 PUA 字体**。
  系统字体确实没有语义正确的字形（比如筛选用的是「三线递减滑杆」、菜单是「两条等长线」）时，
  才用 `LinesIconView` 自己画 —— 宁可口径明确，也不拿 `≡` 冒充。

版式的两处取舍，是手机特有的：

1. **底部导航做成悬浮胶囊**，而不是占满底部的实心栏。屏幕竖向空间在手机上是最稀缺的资源，
   悬浮胶囊让内容可以从底下穿过，视觉上少切一刀。
2. **筛选/搜索拆成独立两行**。挤在一行里在 360dp 宽上必然折行，旧版就是这么坏掉的。

---

## 功能

### 图源与浏览

- 9 个图源：Catgirl、Waifu、Danbooru、Lolicon、Sakura Random、Safebooru、Gelbooru、Konachan、Yande.re
- **随机**与**分页**两种模式（顶部 Chip 切换，设置里即时生效）
- 标签搜索：支持标签的图源显示搜索框，空格分隔多标签
- NSFW 三档过滤：屏蔽 / 仅 NSFW / 全部
- 下拉刷新；图源条自动滚到当前选中的源（图源多时选中的会被挤出屏幕）

### 查看器

- 全屏大图，顶部与底部是**渐变遮罩**（不是实心条），图片是唯一焦点
- 双指捏合缩放（1×–8×）、双击在 1× 与 2× 间切换、放大态拖动平移带惯性
- 未放大时：下滑关闭、横滑切上一张 / 下一张
- 底部显示作者、尺寸、标签（点标签即按该标签搜索）；绿色主按钮下载 + 分享 + 收藏

### 下载

- 批量下载：并发数可配（1–8），失败项单独计数，中途可「取消全部」
- 前台服务 + 通知栏进度（下载中应用可以退到后台）
- 仅 Wi-Fi 下载、完成后通知、保存到系统相册或应用目录（Android 特有，见 `AndroidPrefs`）
- 进度按**字节**而不是条目数计算 —— 一张 8MB 的图和一张 200KB 的图不该各算一格

### 网络与代理

- 复用桌面版的三级链路：系统代理探测 → 协议不符回退 → 传输故障再回退直连
- 手动填写的代理会被**严格遵从**（避免「以为走了代理其实没走」），想强制直连勾「忽略代理直连」
- 可配请求超时

### 性能

- 缩略图：只读尺寸 → 按 2 的幂次采样解码 → 位图按字节数 LRU（上限 = 可用堆的 1/8，封顶 48MB）
- 磁盘缓存复用 Core 的 `ImageDownloader.DownloadCachedAsync`（内存 LRU + 2000 文件磁盘层）
- 解码并发限 3：快速滑动会瞬间产生几十个请求，不设限会触发低内存回收（症状是「滑两下应用被杀」）
- 网格用 RecyclerView：列数随屏宽变化时 `GridView` 要么重建适配器要么复用错位

### 界面与个性化

- 暗房深色界面，三阶表面 + 1px 极细线
- 强调色跟随系统动态配色，运行中改系统主题即时跟随
- 状态行只报「正在做什么」，不报「一切正常」

---

## 构建与运行

**环境要求**

| | |
| --- | --- |
| .NET SDK | 10.0 |
| Android SDK | Platform 36 + Build-Tools（`AndroidSdkDirectory` 默认读 `%LocalAppData%\Android\Sdk`） |
| JDK | 17（.NET Android 自带/要求） |
| 最低系统 | Android 7.0（API 24） |

```bash
# 构建（默认会还原）
dotnet build src/AnimeDownloader.App/AnimeDownloader.App.csproj -c Debug

# 只出 APK
dotnet build src/AnimeDownloader.App/AnimeDownloader.App.csproj -c Release \
  -p:RuntimeIdentifier=android-arm64 -t:SignAndroidPackage
```

产物在 `src/AnimeDownloader.App/bin/<配置>/net10.0-android/<RID>/`：

| 文件 | 说明 |
| --- | --- |
| `com.koiflan.animedownloader-Signed.apk` | 已签名，可直接装 |
| `com.koiflan.animedownloader.apk` | 未签名 |

默认保留三种 ABI（`android-arm64;android-arm;android-x64`）。**只发一款包时务必带
`-p:RuntimeIdentifier=`**，否则多 ABI 全打进同一个 APK，体积翻好几倍。

---

## 部署到真机

⚠️ **Debug 包默认走 Fast Deployment：程序集不打进 APK。** 这时只用 `adb install` 装 APK，
启动会立刻 abort，日志是：

```
Abort message: 'No assemblies found in '.../files/.__override__/arm64-v8a' ...
                Assuming this is part of Fast Deployment. Exiting...'
```

两种解法，推荐第二种（不依赖 adb 守护进程状态，更可控）：

```bash
# 解法一：让 MSBuild 自己装（会顺带推送程序集）
dotnet build src/AnimeDownloader.App/AnimeDownloader.App.csproj -c Debug -t:Install \
  -p:RuntimeIdentifier=android-arm64 -p:AdbTarget="-s <设备序列号>"

# 解法二（推荐）：打成自包含 APK，再用 adb 装
dotnet build src/AnimeDownloader.App/AnimeDownloader.App.csproj -c Debug \
  -t:SignAndroidPackage -p:RuntimeIdentifier=android-arm64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType=
adb -s <设备序列号> install -r -d \
  "src/AnimeDownloader.App/bin/Debug/net10.0-android/android-arm64/com.koiflan.animedownloader-Signed.apk"
```

自包含包会从 ~12MB 涨到 ~78MB，**这是正常的**（程序集内嵌）。

---

## 项目结构

```
AnimeDownloader/
├── AnimeDownloader.slnx                  # .NET 10 解决方案
├── Directory.Build.props                 # 公共编译属性（Nullable、警告即错误、确定性构建）
├── src/
│   ├── AnimeDownloader.Core/             # 纯 .NET 类库，与桌面版逐字节相同
│   │   ├── Models/                       # NsfwMode、ImageItem、ImageTag
│   │   ├── Sources/                      # IImageSource 接口 + 9 个图源适配器
│   │   └── Services/                     # 设置、代理探测、HttpClient 工厂、图片下载器、标签
│   ├── AnimeDownloader.Download/         # 下载编排
│   │   ├── Contracts/                    # IDownloadModule、快照/进度模型、命名与落盘策略
│   │   └── Android/                      # 前台服务、通知、网络监视、MediaStore 落盘
│   └── AnimeDownloader.App/              # Android 应用
│       ├── Views/                        # 画廊、图片查看、下载、设置
│       ├── Controls/                     # 设计系统控件（按钮、开关、行、下拉刷新…）
│       ├── Design/                       # Dk 令牌 + 图标常量
│       ├── Platform/                     # 边到边内边距、屏幕尺寸档与字阶
│       ├── Thumbnails/                   # 采样解码 + 位图 LRU + 并发闸门
│       └── Settings/                     # Android 特有的偏好（AndroidPrefs）
├── design/
│   └── MOBILE_UI_REDESIGN.md             # 移动端重设计说明（版式、组件、实现映射）
├── docs/images/                          # README 截图
├── tests/
│   ├── AnimeDownloader.Core.Tests/       # xUnit（不联网）
│   └── AnimeDownloader.Download.Tests/   # xUnit（下载规划与命名策略）
└── tools/IconGen/                        # 启动图标生成脚本
```

---

## 配置与数据目录

全部落在**应用私有目录**下（无需存储权限）：

| 位置 | 内容 |
| --- | --- |
| `files/config.json` | 与桌面版同格式的全部设置（图源、NSFW、代理、并发…） |
| 应用私有缓存目录 | 缩略图磁盘缓存（上限 2000 个文件） |
| 系统相册 / 应用目录 | 下载的图片（默认存进系统相册，可在设置里改） |

因为与桌面版**共用同一份配置格式**，配置文件在两者之间是可读的 ——
但 Android 上无法用 `ANIMEDOWNLOADER_CONFIG_DIR` 重定向（该变量属于桌面端的便携部署）。

---

## 测试

```bash
dotnet test AnimeDownloader.slnx -c Debug
```

两个测试工程都**不联网**：网络无关逻辑（参数构造、URL 生成、配置读写、缓存、
文件名建议、标签联想与本地化、下载规划与命名）全部下沉到 Core / Download 层，
所以能脱离设备跑。目前 **81/81（Core）+ 84/84（Download）通过**。

界面与触控没有自动化测试 —— 那部分靠真机验证：装包 → 三页往返 → 看日志无崩溃。

---

## 常见问题

**Q：装完打开就闪退，日志里是 `No assemblies found ... Fast Deployment`。**
Debug 包没把程序集打进 APK。见[部署到真机](#部署到真机)，用 `EmbedAssembliesIntoApk=true` 重新打包。

**Q：`dotnet build -t:Install` 报 `XA0010: 所选设备未运行`，但设备明明连着。**
该目标依赖 adb 守护进程状态，容易误判。改用上面「解法二」：自己打自包含包再 `adb install`。

**Q：某个图源一直转圈。**
Danbooru / Gelbooru / Konachan / Yande.re 在国内通常需要代理；Catgirl / Lolicon /
Sakura Random 一般可直连。图源失败与「没有结果」是两种状态，空状态会区分显示。

**Q：Gelbooru 搜不出东西。**
Gelbooru 已停用匿名 API，需要在设置页填 `user_id` 与 `api_key`。

**Q：为什么设置页没有「深色模式」的三态选择？**
手机端固定在深色。桌面版的三态（跟随系统 / 亮色 / 暗色）是为 Windows 桌面环境准备的；
手机上浅色版式没有做，所以这一档被收敛掉了。

**Q：切到某些图源后搜索框消失了。**
那些图源（如 Catgirl）本身不支持标签检索，搜索行会整行收起而不是留一个空壳。

---

## 相关文档

- [`design/MOBILE_UI_REDESIGN.md`](design/MOBILE_UI_REDESIGN.md) —— 移动端版式、组件清单、导航逻辑与原生实现映射
- [`docs/ARCHITECTURE.md`](https://github.com/koiflan-514/AnimeDownloader/blob/main/docs/ARCHITECTURE.md) —— 分层职责、关键设计决策、图源协议（在 `main` 上；Core 部分同样适用）
- [`design/DESIGN.md`](https://github.com/koiflan-514/AnimeDownloader/blob/main/design/DESIGN.md) —— 暗房设计语言完整规范（在 `main` 上）

---

## 许可

GPL-3.0-or-later（与参考项目一致）。完整条款见 [LICENSE](LICENSE)。
