# AnimeDownloader 架构

AnimeDownloader 是一个参考 CatgirlDownloader（GTK4/Python）功能、使用
**WinUI 3 + C# (.NET 10)** 重写的动漫图片浏览与下载应用。界面与实现均为全新设计，
不照搬参考项目 UI，但功能对其保持对齐并有所增强。

## 项目结构

```
AnimeDownloader/
├── AnimeDownloader.slnx          # .NET 10 解决方案（新 XML 格式）
├── Directory.Build.props          # 公共编译属性（Nullable、警告即错误、确定性构建）
├── src/
│   ├── AnimeDownloader.Core/      # 纯 .NET 类库：不依赖 WinUI，可独立单元测试
│   │   ├── Models/                # NsfwMode、ImageItem 等数据模型
│   │   ├── Sources/               # IImageSource 接口 + 各图源适配器（9 个）
│   │   └── Services/              # 设置持久化、代理检测、HttpClient 工厂、下载器、缓存、批量下载
│   └── AnimeDownloader.App/       # WinUI 3 应用（未打包桌面应用）
│       ├── Views/                 # 画廊页、查看器页、设置页
│       ├── Controls/              # 缩略图卡片、全屏画廊查看器窗口
│       └── Styles/                # 集中式主题资源（圆角、卡片、状态栏样式）
├── tests/
│   └── AnimeDownloader.Core.Tests/  # xUnit 测试（不联网，覆盖参数构造、URL 生成、缓存、下载器）
└── docs/
    └── ARCHITECTURE.md
```

## 分层职责

### Core（src/AnimeDownloader.Core）

| 组件 | 职责 |
| --- | --- |
| `Models/NsfwMode` | NSFW 三态：`ShowEverything` / `OnlyNsfw` / `BlockNsfw` |
| `Models/ImageItem` | 单张图片条目：URL、缩略图 URL、艺术家、来源链接、ID、扩展名、原始元数据；含 `SuggestFileName` |
| `Sources/IImageSource` | 图源抽象：随机单图、批量取图、分页取图、`SupportsPaging`、`SupportsTags`、`LastError` |
| `Sources/MoebooruSourceBase` | Moebooru 系图源公共实现（Yande.re / Konachan），含随机重试 |
| `Sources/*` | 9 个图源适配器：NekosMoe、WaifuIm、Danbooru、Lolicon、Dmoe、Safebooru、Gelbooru、Konachan、YandeRe |
| `Services/SettingsStore` | JSON 配置持久化；损坏文件自动备份 |
| `Services/ProxyDetector` | 环境变量 + Windows 注册表 Internet Settings 的系统代理检测 |
| `Services/HttpClientFactory` | 共享 HttpClient：注入代理、统一 UA / Accept、可配置超时 |
| `Services/ThumbnailResolver` | 缩略图解析：原生缩略图 → 可选代理模板 → 原图（客户端按小尺寸解码并缓存） |
| `Services/ImageDownloader` | 流式下载到内存或文件；并发限流（SemaphoreSlim）、瞬时故障重试（退避）、200MB 上限、缩略图缓存（内存 LRU + 磁盘）、原子写盘 |
| `Services/BatchDownloader` | 批量下载整个图集到目录：进度回调、跳过已存在文件、失败计数不中断 |

### App（src/AnimeDownloader.App）

- **主窗口**：顶部全局工具栏（图源下拉、NSFW 过滤、刷新、状态）+ `NavigationView` 三页导航
  （画廊 / 查看器 / 设置）；标题栏显示当前图片的缩略图预览。窗口宽度 < 700px 时自动切换为左侧紧凑导航。
- **模式页契约**：`IModePage`（Attach / OnSourceChanged / OnNsfwChanged / Reload），
  主窗口统一驱动各页面的源 / NSFW 变更与全局刷新。
- **页面缓存**：三页实例由主窗口缓存复用，切换导航不重建页面、不丢失状态。
- **画廊页**：`CommandBar` 工具栏（随机/分页切换、上一页/下一页、下载全部、自动刷新）；
  `GridView` + `ItemsWrapGrid` 按窗口宽度自适应正方形卡片尺寸（2-8 列）；
  批量下载进度条 + 取消；空结果 / 失败时透传 `LastError`。
- **查看器页**：大图预览（异步加载 + 占位动画）、缩放/平移、键盘快捷键
  （空格/右箭头换图、Ctrl+S 保存、F11 全屏、Esc 退出）、来源链接、保存进度。
- **全屏查看器窗口**：浏览画廊缩略图列表中的大图；支持上一张/下一张、键盘导航、
  下一张预加载、保存进度、全屏切换与 Mica 背景；工具栏同样显示当前图片缩略图。
- **缩略图卡片**：通过共享下载器的缩略图缓存加载；`DecodePixelWidth=360` 限制解码内存；
  悬停缩放 + 作者角标；失败占位。无缩略图时显示“无缩略图”占位，绝不回退下载原图。
- **设置页**：NSFW 模式、主题、自动刷新间隔、每页数量、代理与超时、批量下载目录、
  并发数、缩略图缓存开关与清理、各图源标签（Danbooru 受限标签实时提示）。
- **集中式样式**：`Styles/AppStyles.xaml` 定义卡片圆角、卡片边框、状态栏等主题资源，
  全部使用 `ThemeResource` 系统画刷，支持亮 / 暗 / 高对比度。
- 所有网络操作在后台执行，通过 `DispatcherQueue` / `Progress<T>` 回到 UI 线程；
  用取消令牌与代际 token 防止竞态。

## 关键设计决策

1. **图源协议统一**：所有图源实现 `IImageSource`，核心与 UI 只依赖接口；
   新增图源只需注册一个新适配器。`LastError` 透传让 UI 能区分“没有结果”与“请求失败”。
2. **随机 vs 分页**：`SupportsPaging=false` 的源（如 dmoe）隐藏分页控件并显示提示。
3. **未打包部署**：`WindowsPackageType=None`，`dotnet build` 后直接运行，
   `WindowsAppSDKSelfContained=true` 保证目标机器无需预装 Windows App SDK。
4. **性能与网络 IO**：除查看器外一律只加载缩略图——有原生缩略图的源直接用小图；
   无原生缩略图的源（nekos.moe / dmoe.cc / waifu.im）由 `ThumbnailResolver` 回退到原图字节，
   首次取回后按 360px 解码并写入内存 + 磁盘两级缓存，之后重复浏览零请求（不依赖任何外部压缩代理；
   设置中可配置 `{url}` 代理模板，若用户网络可达则进一步压缩首载流量）；
   全局 `SemaphoreSlim` 限制并发；
   位图按缩略尺寸解码；`GridView` 虚拟化；批量下载为逐文件流式写盘 + 原子改名，保存的始终是原图。
5. **健壮性**：下载器对 408/429/5xx/IO 瞬时故障自动重试（指数退避）；
   下载超过 200MB 直接中止；配置文件损坏时备份原文件并回退默认值。
6. **警告即错误**：`TreatWarningsAsErrors=true` + 推荐分析级别，保障可读性与整洁。
7. **可测试性**：网络无关逻辑（参数构造、URL 生成、配置读写、缓存、下载器、文件名建议）
   全部下沉到 Core，用 xUnit 覆盖（不联网）。

## 图源一览

| ID | 显示名 | 端点 | 分页 | 标签 |
| --- | --- | --- | --- | --- |
| nekosmoe | Catgirl | `https://nekos.moe/api/v1/random/image` | 否 | 否 |
| waifuim | Waifu | `https://api.waifu.im/images` | 是 | 否 |
| danbooru | Danbooru | `https://danbooru.donmai.us/posts.json` | 是 | 是 |
| lolicon | Lolicon | `https://api.lolicon.app/setu/v2` (POST) | 否 | 是 |
| dmoe | Sakura Random | `https://www.dmoe.cc/random.php` | 否 | 否 |
| safebooru | Safebooru | `https://safebooru.org/index.php` | 是 | 是 |
| gelbooru | Gelbooru | `https://gelbooru.com/index.php` | 是 | 是 |
| konachan | Konachan | `https://konachan.com/post.json` | 是 | 是 |
| yandere | Yande.re | `https://yande.re/post.json` | 是 | 是 |
