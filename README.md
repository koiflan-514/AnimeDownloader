# AnimeDownloader

基于 **WinUI 3 + C# (.NET 10)** 的动漫图片浏览与下载应用，功能参考
[CatgirlDownloader](https://github.com/NyarchLinux/CatgirlDownloader)（GTK4/Python），
界面与实现均为全新设计。

## 功能 / Features

- 多个图源可选（9 个）：
  - **Catgirl** — https://nekos.moe
  - **Waifu** — https://waifu.im
  - **Danbooru** — https://danbooru.donmai.us（自定义标签，自动过滤受限标签）
  - **Lolicon** — https://api.lolicon.app（国内可直连，Pixiv 原图，标签搜索与 R18 过滤）
  - **Sakura Random / 樱花随机图** — https://www.dmoe.cc（国内可直连）
  - **Safebooru** — https://safebooru.org（仅全年龄内容，标签搜索）
  - **Gelbooru** — https://gelbooru.com（标签搜索与 NSFW 分级过滤）
  - **Konachan** — https://konachan.com（标签搜索与 NSFW 分级过滤）
  - **Yande.re** — https://yande.re（标签搜索与 NSFW 分级过滤）
- 画廊网格浏览：随机 / 分页两种模式，网格随窗口宽度自适应（2-8 列）
- NSFW 三态过滤（全部 / 仅 NSFW / 屏蔽 NSFW）
- 全屏图片查看器（上一张 / 下一张 / 保存 / 全屏），支持键盘导航与下一张预加载
- 查看器页：适应屏幕 / 实际大小 / 拉伸填充 / 缩放（25%–400%，Ctrl+滚轮或 +/-）、空格换图、
  Ctrl+S 保存、F11 全屏、来源链接直达原帖
- 批量下载整个画廊到指定文件夹（带进度与取消，自动跳过已存在文件）
- 非查看器界面只加载缩略图：有原生缩略图的源直接用小图；无缩略图的源（nekos.moe / dmoe / waifu.im）
  首次取原图字节后本地按小尺寸解码并缓存，重复加载零请求；可选配置压缩代理模板进一步降低首载流量
- 缩略图缓存（内存 LRU + 磁盘），重复加载画廊几乎零等待；主窗口标题栏与查看器工具栏显示当前图片缩略图
- 并发下载限流、瞬时故障自动重试、可配置请求超时
- 自动刷新（可配置间隔）
- 系统代理自动检测（环境变量 + Windows 注册表），支持手动代理与直连
- 设置持久化（JSON，`%LocalAppData%/AnimeDownloader/config.json`）
- 设置页内置“检测源连通性”：逐个探测 9 个图源的直连与代理可达性，标出哪些源需要科学上网
- 亮 / 暗 / 跟随系统主题，Win11 Mica 材质，窄窗口自适应导航

## 构建 / Building

要求：Windows 10 1809+、.NET 10 SDK（10.0.302 或更新）。应用为未打包桌面应用
（`WindowsPackageType=None`），`dotnet build` 后即可直接运行，
目标机器无需安装 Windows App SDK（自包含运行时已随输出目录分发）。

```bash
dotnet restore
dotnet build AnimeDownloader.slnx -c Debug
dotnet test  AnimeDownloader.slnx -c Debug
```

运行（GUI 应用）：

```bash
dotnet run --project src/AnimeDownloader.App -c Debug
# 或直接运行输出目录中的 exe：
# src/AnimeDownloader.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/AnimeDownloader.App.exe
```

可选：设置环境变量 `ANIMEDOWNLOADER_CONFIG_DIR` 可把配置与缩略图缓存目录重定向到指定位置
（默认 `%LocalAppData%/AnimeDownloader`），便于便携部署。

## 打包安装包 / Packaging

安装包为简体中文界面，面向普通用户：向导包含许可协议、自定义安装目录（可浏览更改）、
安装完成后提示卸载方式（设置 → 应用 → 已安装的应用，或控制面板 → 程序和功能）。

```powershell
# 不搭载 .NET Runtime 的版本（目标机器需装 .NET 10 Desktop Runtime）
.\tools\installer\publish-framework-dependent.ps1 -Architecture win-x64

# 自带 .NET Runtime 的版本（开箱即用，包更大）
.\tools\installer\publish-self-contained.ps1 -Architecture win-x64
```

产物输出到 `publish\`，需要 WiX Toolset（`dotnet tool install --global wix`）。

## 项目结构

```
src/AnimeDownloader.Core/    纯 .NET 类库：模型、图源适配器、设置、代理、下载器、缓存、批量下载（可单测）
src/AnimeDownloader.App/     WinUI 3 应用：画廊页、查看器页、设置页、自定义控件、集中式样式
tests/AnimeDownloader.Core.Tests/  xUnit 测试（不联网）
docs/ARCHITECTURE.md         架构说明
```

## 许可 / License

GPL-3.0-or-later（与参考项目一致）
