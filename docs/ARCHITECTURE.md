# AnimeDownloader 架构

AnimeDownloader 是一个参考 CatgirlDownloader（GTK4/Python）功能、使用
**WinUI 3 + C# (.NET 10)** 重写的动漫图片浏览与下载应用。界面与实现均为
全新设计，不照搬参考项目 UI，但功能对其保持对齐并有所增强。

## 项目结构

```
AnimeDownloader/
├── AnimeDownloader.sln
├── Directory.Build.props          # 公共编译属性（Nullable、警告即错误、确定性构建）
├── src/
│   ├── AnimeDownloader.Core/      # 纯 .NET 类库：不依赖 WinUI，可独立单元测试
│   │   ├── Models/                # NsfwMode、ImageItem 等数据模型
│   │   ├── Sources/               # IImageSource 接口 + 各图源适配器
│   │   └── Services/              # 设置持久化、代理检测、HttpClient 工厂、图片下载
│   └── AnimeDownloader.App/       # WinUI 3 应用（未打包桌面应用）
│       ├── Views/                 # 画廊页、查看器页、设置页
│       ├── Controls/              # 缩略图卡片等自定义控件
│       ├── App.xaml / MainWindow.xaml
│       └── app.manifest
├── tests/
│   └── AnimeDownloader.Core.Tests/  # xUnit 测试（不联网，验证参数构造与配置读写）
└── docs/
    └── ARCHITECTURE.md
```

## 分层职责

### Core（src/AnimeDownloader.Core）

| 组件 | 职责 |
| --- | --- |
| `Models/NsfwMode` | NSFW 三态：`ShowEverything` / `OnlyNsfw` / `BlockNsfw`（对应参考项目的 NSFWOption） |
| `Models/ImageItem` | 单个图片条目：URL、艺术家、来源链接、建议文件名、扩展名、原始元数据 |
| `Sources/IImageSource` | 图源抽象：随机单图、批量取图、分页取图、`SupportsPaging` 标志、文件名建议 |
| `Sources/*` | 7 个图源适配器：NekosMoe、WaifuIm、Danbooru、Lolicon、Dmoe、Safebooru、YandeRe |
| `Services/SettingsStore` | JSON 配置持久化，键名与默认值与参考项目保持一致 |
| `Services/ProxyDetector` | 环境变量 + Windows 注册表 Internet Settings 的系统代理检测 |
| `Services/HttpClientFactory` | 共享 HttpClient/Handler 工厂，注入检测到的代理，统一 UA 与超时 |
| `Services/ImageDownloader` | 流式下载图片字节，支持取消与进度回调 |

### App（src/AnimeDownloader.App）

- **主窗口**：`NavigationView` 三页导航 —— 画廊 / 查看器 / 设置。
- **画廊页**：GridView 缩略图网格 + 工具栏（图源下拉、NSFW 下拉、随机/分页切换、
  页码导航、刷新、自动刷新开关、批量下载）。
- **查看器页**：大图预览（异步加载 + 占位动画）、上一张/下一张、保存、全屏。
- **设置页**：NSFW 模式、自动刷新间隔、画廊每页数量、各图源标签、主题切换。
- **图片卡片**：180×180 封面裁剪、异步加载、点击打开查看器。
- 所有网络操作在后台执行，通过 `DispatcherQueue` 回到 UI 线程；用取消令牌
  防止竞态（对应参考项目 gallery.py 的代际 token 思路）。

## 关键设计决策

1. **图源协议统一**：所有图源实现 `IImageSource`，核心 UI 只依赖接口，
   新增图源只需注册一个新适配器。
2. **随机 vs 分页**：`SupportsPaging=false` 的源（如 dmoe）隐藏分页控件并显示提示，
   与参考项目行为一致。
3. **未打包部署**：`WindowsPackageType=None`，`dotnet build` 后直接运行，
   无需 MSIX 安装；self-contained 选项保证目标机器无需预装 Windows App SDK。
4. **警告即错误**：`TreatWarningsAsErrors=true` + 推荐分析级别，保障可读性与整洁。
5. **可测试性**：网络无关逻辑（参数构造、URL 生成、配置合并、文件名建议）
   全部下沉到 Core，用 xUnit 覆盖。

## 图源一览

| ID | 显示名 | 端点 | 分页 | 标签 |
| --- | --- | --- | --- | --- |
| nekosmoe | Catgirl | `https://nekos.moe/api/v1/random/image` | 否 | 否 |
| waifuim | Waifu | `https://api.waifu.im/images` | 是 | 否 |
| danbooru | Danbooru | `https://danbooru.donmai.us/posts.json` | 是 | 是 |
| lolicon | Lolicon | `https://api.lolicon.app/setu/v2` (POST) | 否 | 是 |
| dmoe | Sakura Random | `https://www.dmoe.cc/random.php` | 否 | 否 |
| safebooru | Safebooru | `https://safebooru.org/index.php` | 是 | 是 |
| yandere | Yande.re | `https://yande.re/post.json` | 是 | 是 |
