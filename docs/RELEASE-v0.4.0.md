# AnimeDownloader v0.4.0

基于 **C# / .NET 10 + WinUI 3** 的动漫图片浏览与下载桌面应用。
v0.4.0 是自 v0.2.0（首次开源发布）以来的功能大版本：**代理隧道、标签体系、Windows 原生配色**三大升级，
另有画廊卡片动效、并行批量下载等体验提升，以及一批图源与打包修复。

> 相对上一公开版本 v0.2.0 的完整变更如下（commit 范围 `v0.2.0..v0.4.0`）。

---

## 🚀 网络与代理：真正的"开箱即连"

- **自研 SOCKS5 代理隧道**：.NET 不原生支持 `socks5://`，新增 `Socks5Connector` + `ProxyTunnelConnector` 自研实现，
  统一支持 **HTTP(S) CONNECT 与 SOCKS5**、代理认证（`user:password@`）、`no_proxy` 旁路。
- **智能代理回退链**：自动检测到的系统代理先探测 SOCKS5 → 协议不符自动回退 HTTP CONNECT →
  节点传输故障 / 假死超时再回退直连；**手动指定的代理则严格遵从**，不擅自绕过。
- **图源连通性修复**：dmoe（樱花随机图）图床接口修复；Waifu 解析修正。
- **Gelbooru 官方 API 凭据支持**：Gelbooru 自 2022-09 起停用匿名 API，设置页填写 `user_id + api_key` 后即可正常搜索。
- **新增 `tools/SourceSmoke` 联网冒烟测试工具**：逐图源探测 + 真实拉取 + 图片字节校验，
  含 dmoe 图床、SOCKS5 隧道等专项探测子命令（`dotnet run --project tools/SourceSmoke`）。

## 🏷️ 标签体系升级：看得懂、选得快

- **查看器全标签展示**：图片的全部标签（含类别：角色 / 画师 / 作品 / 内容…）以彩色圆点标注展示，
  热门标签带**中文译名**；点击标签直达画廊的同标签浏览。
- **智能联想输入**：标签输入框按**热度**联想，同时显示中文译名 / 原名 / 帖子数，勾选即加入搜索，并给出**相关标签推荐**。
- **内嵌 5000 条热门标签中英对照表**（离线内置），本地联想兜底，无网络也能正常联想。
- **画廊标签快速搜索**：支持标签的图源（Danbooru / Gelbooru / Safebooru / Konachan / Yande.re / Lolicon），
  回车应用并自动保存，活动标签以胶囊展示、一键清除。

## 🎨 UI 打磨：原生 Windows 观感

- **配色完全跟随 Windows 个性化**：强调色取自系统 Accent Color（不再硬编码），亮 / 暗模式跟随系统；
  运行中修改系统的强调色或深浅色，应用**即时跟随**（UISettings 监听），Win11 Mica 材质。
- **Win11 设置风格界面**：设置页重构为卡片式设置行（标题 + 描述 + 右侧控件）、Fluent 字阶，
  分段式选择控件（NSFW 三态过滤、主题切换：跟随系统 / 浅色 / 深色）。
- **画廊卡片升级**：错峰入场动画、悬浮放大高亮、分辨率徽章、艺术家信息条、悬浮快捷保存按钮。
- **侧栏与工具栏打磨**：展开 / 收起导航时工具栏自动切换完整 / 紧凑形态，统一页面对齐。

## 🖼️ 查看器增强

- 标签栏移至图片下方，进入全屏时自动隐藏，浏览更沉浸。
- **一键复制图片直链**；**SauceNAO / IQDB 以图搜图**一键跳转。
- 修复联想弹层抢输入焦点的问题，键盘操作不被打断。

## ⚡ 性能

- 批量下载整图集**按 CPU 核数并行铺开**（底层并发闸门统一限流），逐文件流式写盘 + 原子改名，落盘始终是原图。
- 非查看器界面只加载缩略图：无原生缩略图的源（nekos.moe / dmoe / waifu.im）首次取回后
  本地按小尺寸解码，写入**内存 LRU + 磁盘两级缓存**，之后重复浏览**零网络请求**。

## 🛠️ 构建与质量

- 修复安装包脚本：WiX 7 打包显式接受 EULA（`AcceptEula=wix7`），一键出 .msi 开箱即用。
- 新增 `SourceFixAndProxyTests`、`TagFeaturesTests` 两组自动化测试，覆盖代理回退、图源解析、标签联想与本地化
  （网络无关逻辑全部下沉纯 .NET Core 层，xUnit 测试不联网）。

---

## 安装

**系统要求**：Windows 10 1809+ / Windows 11，x64。

发布时挂载两种安装包（见下方 Assets）：

| 安装包 | 说明 |
| --- | --- |
| `AnimeDownloader-0.4.0-self-contained-x64.msi` | 自带 .NET Runtime，目标机**免装运行库**，开箱即用（体积较大） |
| `AnimeDownloader-0.4.0-framework-dependent-x64.msi` | 需先安装 .NET 10 Desktop Runtime（体积小） |

安装向导为简体中文，支持自定义安装目录，卸载走系统"已安装的应用"即可。
手动构建安装包见 `tools/installer/publish-*.ps1`（依赖 WiX Toolset：`dotnet tool install --global wix`）。

## 完整提交（v0.2.0..v0.4.0）

```
ab8632e chore: 版本升级 0.4.0（代理隧道 / 标签体系升级 / Windows 配色适配 / Win11 设置风格 UI）
3c22583 fix: 查看器标签栏移至图片下方并全屏隐藏；联想弹层不抢输入焦点；本地联想兜底
4ff322f feat: 标签体系升级——查看器全标签展示、复选框智能联想、热门标签中文本土化
46135fe feat: SOCKS5 代理隧道与图源修复、配色跟随 Windows、Win11 设置风格 UI
c97c0ff feat: 画廊卡片动效与信息展示、标签搜索、分段过滤、主题切换、并行下载提速
52c45fe style: 侧栏与工具栏 UI 打磨，统一页面对齐
```

## 许可

GPL-3.0-or-later。功能参考 [CatgirlDownloader](https://github.com/NyarchLinux/CatgirlDownloader)（GTK4/Python），
界面与实现均为全新设计。
