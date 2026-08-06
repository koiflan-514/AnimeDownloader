# AnimeDownloader

基于 **WinUI 3 + C# (.NET 10)** 的动漫图片浏览与下载应用，功能参考
[CatgirlDownloader](https://github.com/NyarchLinux/CatgirlDownloader)（GTK4/Python），
界面与实现均为全新设计。

## 功能 / Features

- 多个图源可选 / Multiple image sources:
  - **Catgirl** — https://nekos.moe
  - **Waifu** — https://waifu.im
  - **Danbooru** — https://danbooru.donmai.us（自定义标签）
  - **Lolicon** — https://api.lolicon.app（国内可直连，Pixiv 原图，标签搜索与 R18 过滤）
  - **Sakura Random / 樱花随机图** — https://www.dmoe.cc（国内可直连）
  - **Safebooru** — https://safebooru.org（仅全年龄内容，标签搜索）
  - **Yande.re** — https://yande.re（标签搜索与 NSFW 分级过滤）
- 画廊网格浏览：随机 / 分页两种模式
- NSFW 三态过滤（全部 / 仅 NSFW / 屏蔽 NSFW）
- 全屏图片查看器（上一张 / 下一张 / 保存）
- 自动刷新
- 系统代理自动检测（环境变量 + Windows 注册表）
- 设置持久化（JSON，`%LocalAppData%/AnimeDownloader/config.json`）

## 构建 / Building

要求：Windows 10 1809+，.NET 10 SDK（10.0.302 或更新）。
应用为未打包桌面应用（`WindowsPackageType=None`），`dotnet build` 后即可直接运行，
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
# src/AnimeDownloader.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/AnimeDownloader.App.exe
```

## 项目结构

```
src/AnimeDownloader.Core/   纯 .NET 类库：模型、图源适配器、设置、代理、下载器（可单测）
src/AnimeDownloader.App/    WinUI 3 应用：画廊页、查看器页、设置页
tests/AnimeDownloader.Core.Tests/  xUnit 测试（不联网）
docs/ARCHITECTURE.md        架构说明
```

## 许可 / License

GPL-3.0-or-later（与参考项目一致）
