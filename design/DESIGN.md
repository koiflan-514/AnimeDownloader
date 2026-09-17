# AnimeDownloader · 暗房（Darkroom）界面改版设计规范

> 一次针对 WinUI 3 前端的结构性重设计。目标不是"换个配色"，而是让这个应用**不再看起来像一份 WinUI 模板**：
> 重新划分导航职责、重建色彩与字体体系、把三个核心页面按各自的真实任务重新排版，并保证全部改动可直接编译落地。

---

## 0. 改版前的诊断

对照 `E:\skills\UI\taste-skill`（redesign-skill / taste-skill-v1）的审计清单，原界面的问题不是"不够漂亮"，而是**结构性**的：

| 问题 | 表现 | 性质 |
|---|---|---|
| 导航与工具混住 | 320px 常开侧栏里同时住着"画廊/查看器/设置"和"图源/NSFW/刷新/清缓存/状态" | 结构 |
| 最常用的控件藏得最深 | 换图源要先展开侧栏、再点下拉 | 可用性 |
| "查看器"是一级导航 | 单图细看本是画廊里的下钻动作，却被提升为与画廊平级的目的地 | 信息架构 |
| 页面头部三条工具带 | 标题 / 标签搜索 / 工具栏各占一行，视觉重量全在边缘，中间网格最轻 | 视觉层级 |
| 卡片是"Pinterest 缩略图" | 180×180 方形、带描边、圆角 12、悬停弹按钮 | 俗套 |
| 设置页 = Win11 设置复刻 | 分组标题 + 一摞圆角卡片 | 俗套 |
| 颜色无层级 | 底色与卡片几乎同值，层级靠投影而非表面色阶；强调色出现在十余处，等于没有强调 | 视觉 |
| 数字用比例字 | 页码、分辨率、百分比都是普通字体 | 细节 |

---

## 1. 设计语言：暗房（Darkroom）

**一句话概念**：这个应用是一间暗房 —— 中性冷调的炭灰是"房间"，图片是唯一的光源，**你的 Windows 强调色是暗房里的安全灯**。

这个比喻不是修辞装饰，它直接决定了两条硬规则：

1. **全应用只有一个彩色：用户的系统强调色。** 而且它只在三种语义上亮起 —— *正在被选中 / 正在进行 / 需要你注意*。
   就绪状态是灰的（无光源），加载中是强调色（灯亮着），故障是红的。因此状态指示只需要三档语义，而不是五颜六色的标签。
2. **层级不用阴影，只用表面色阶 + 1px 极细线。** 暗房里没有投影，只有离灯远近不同的灰。

第三条规则来自"工具"而非"暗房"：**所有数字都用等宽字体**（页码、分辨率、缩放百分比、版本、快捷键、路径）。这一条几乎不花成本，却立刻把界面从"网页"拉到"仪器"。

### 与参考案例的关系

| 参考 | 借用的是什么 | 明确不借用的 |
|---|---|---|
| `linear.app` | 表面色阶做层级（canvas→surface→surface2）、display 负字距、单一彩色只用在一处 | 它的紫罗兰品牌色、营销页结构 |
| `raycast` | 极细线 + 无阴影、等宽键盘键位、单一高对比主操作 | 它的纯黑白单一色、Inter + ss03 |
| `apple`（HIG） | 图像类应用使用中性底衬、控件退让、内描边模拟折射边缘 | — |
| 反面清单 | — | AI 紫蓝渐变、三栏等宽卡片、到处 pill 徽章、`#000` 纯黑、外发光 |

**为什么强调色跟随 Windows，而不是硬编码一个品牌色**：这里曾经走过一段弯路 —— 为了让应用"有自己的身份"，改版一度把强调色钉死成琥珀（`AppEmber*`，`#E8AE4A`）。实物验证暴露了两个问题：其一，用户在 Windows 里选定的颜色被无视，应用在系统里显得"外人"，与其他窗口并排时不协调；其二，一个固定的暖色在两类底色上很难同时成立 —— 暗底要提亮、亮底要压暗，还得反推文字墨色，等于用手工常数去模拟系统早就做好的事。

最终决定：**不定义品牌色，强调色完全等于用户的 Windows 个性化颜色**。做法是把 `AppAccent*` 令牌接到 `SystemAccentColor*` 上，并由 `WindowsColorScheme` 服务在运行时读取 `UISettings` 的 `Accent / AccentLight1-3 / AccentDark1-3` 回写，同时按 WCAG 亮度自动选黑/白墨色（见 §2.2）。身份感改由**表面色阶 + 等宽数字 + 排版**承担 —— 这些才是这套设计里真正的签名，而它们不依赖某一个具体的色相。

---

## 2. 色彩体系

### 2.1 中性阶（冷调炭灰，单一灰阶家族）

| 令牌 | 暗（默认） | 亮 | 用途 |
|---|---|---|---|
| `AppBackgroundBrush` | `#0D0E11` | `#F7F7F9` | 房间底色（画布） |
| `AppRailBrush` | `#0A0B0D` | `#F1F1F4` | 导轨（比画布略深，读作"墙"） |
| `AppCardFillBrush` | `#14161A` | `#FFFFFF` | 相纸 / 卡片 |
| `AppCardFillHoverBrush` | `#1B1E24` | `#FBFBFD` | 悬停抬升 |
| `AppSurfaceSunkenBrush` | `#07080A` | `#EBEBEF` | 下沉面：输入框、分段器凹槽 |
| `AppViewerBackgroundBrush` | `#06070A` | `#16171B` | 灯箱台面（**亮色模式下依然是深的**，见 §3.2） |
| `AppHairlineBrush` | `rgba(255,255,255,.08)` | `rgba(0,0,0,.07)` | 唯一的"边框"语言 |
| `AppHairlineStrongBrush` | `rgba(255,255,255,.15)` | `rgba(0,0,0,.13)` | 分隔线强化 / 次按钮描边 |
| `AppSubtleHoverBrush` | `rgba(255,255,255,.06)` | `rgba(0,0,0,.04)` | 悬停填充（**只加白，不吃色相**） |
| `AppTextPrimaryBrush` | `#EDEEF1` | `#15161A` | 主要文本 |
| `AppTextSecondaryBrush` | `#9AA0AB` | `#5A606A` | 次要文本 |
| `AppTextTertiaryBrush` | `#6B7179` | `#878D96` | 眉标 / 说明 / 元数据 |
| `AppTextQuaternaryBrush` | `#474C55` | `#B3B7BE` | 禁用 / 空状态图形 |

### 2.2 强调色（唯一彩色 —— 等于你的 Windows 强调色）

这套令牌**不含任何硬编码色值**。除墨色外，全部指向 Fluent 的 `SystemAccentColor*` 基线，运行时由 `WindowsColorScheme` 服务按当前系统个性化设置回写。

| 令牌 | 暗 | 亮 | 用途 |
|---|---|---|---|
| `AppAccentBrush` | `SystemAccentColor` | `SystemAccentColorDark1` | 主操作填充、活动指示、忙碌状态 |
| `AppAccentStrongBrush` | `SystemAccentColorLight1` | `SystemAccentColorDark2` | 悬停 / 按压 |
| `AppAccentSoftBrush` | 强调色 @16% | 强调色 @12% | 选中底衬（导轨项、分段器、标签 chip） |
| `AppAccentLineBrush` | 强调色 @42% | 强调色 @45% | 选中描边、卡片悬停内描边 |
| `AppAccentInkBrush` | 自动取黑或白 | 自动取黑或白 | 压强调色块上的文字 |
| `AppFocusRingBrush` | = 强调色 | = `Dark1` | 焦点环（2px） |

**为什么暗色用原值、亮色降一档**：`SystemAccentColor` 是 Windows 为**深色底**挑的值，直接在浅底上当文字或描边会偏亮、对比不足。因此亮色模式整体下沉一档（`Dark1` / `Dark2`），这也是 WinUI 自身 `AccentFillColor*` 的处理方式。

**墨色自动判定**：`WindowsColorScheme.InkFor()` 用 WCAG 相对亮度公式算强调色的亮度，阈值 **0.4** —— 高于阈值给 `#141518`（深墨），低于给 `#FFFFFF`（白墨）。这样无论用户选的是明黄、薄荷还是深靛，实心按钮上的文字都保持可读，不需要为每个色相手工配一个墨色常数。注意判定用的是**该主题实际生效的强调色**，所以同一个明黄（`#FFB900`，亮度 0.56）在暗色下会转深墨，在亮色下因为先下沉到 `Dark1`（≈`#B38200`，亮度 0.26）又回到白墨 —— 一个颜色、两种底色、两套正确解。

**底衬透明度**：暗色 **16%**、亮色 **12%**（`AppAccentSoftBrush`）；描边 **42% / 45%**。这几个数字由服务统一写入，保证同一个强调色在几乎所有色相下都有相近的"底衬强度"。一个细节：**底衬始终用系统原色**（亮色下也是），只有描边、主色与墨色在亮色模式下沉到 `Dark1` —— 浅底上 12% 的淡衬需要"发光"，跟着压深会糊成一团灰。

**覆盖范围**：不止应用自定义组件。Fluent 自带的强调色一族（`AccentFillColor*`、`TextOnAccentFillColor*`、`FocusStrokeColor*`）本来就派生自 `SystemAccentColor*` —— 也就是我们 `AppAccent*` 引用的同一个源，所以 `ToggleSwitch` 滑块、`CheckBox` 对勾、`ProgressRing`/`ProgressBar`、`ComboBox` 选中项、`TextBox` 焦点下划线**天然与用户强调色一致**，不需要再写一行覆盖值，也不会出现"应用是紫的、原生控件是蓝的"这种割裂（详见 §2.4）。

### 2.3 状态三档

| 令牌 | 语义 | 值（暗） |
|---|---|---|
| `AppStatusIdleBrush` | 安静（无光源） | `#585E67` |
| `AppStatusBusyBrush` | 工作（安全灯亮） | = 强调色 |
| `AppStatusErrorBrush` | 故障 | `#E0575E` |

### 2.4 Fluent 基线覆盖

配色不在组件层零散覆盖，而是**在 `Styles/FluentTheme.xaml` 里改写 Fluent 的中性基线画刷**（该字典合并于 `XamlControlsResources` 之后，否则同名键不会被覆盖）。这样 `TextBox`、`ComboBox`、`Button`、`Flyout`、`ToolTip`、`ContentDialog`、`MenuFlyout`、`ScrollBar` 这些原生控件不用逐个改模板，就自动落在同一套炭灰语言里，不会漏出框架的默认中性色。

覆盖范围约 **55 个键**，分四组：表面/层级（`SolidBackgroundFillColor*`、`LayerFillColor*`、`CardBackgroundFillColor*`、`CardStrokeColor*`、`DividerStrokeColor*`）、控件填充与描边（`ControlFillColor*`、`ControlStrokeColor*`、`SubtleFillColor*`）、文本（`TextFillColor*`、`TextControl*`）、浮层与滚动条（`Flyout*`、`ToolTip*`、`ContentDialog*`、`MenuFlyout*`、`SmokeFillColorDefaultBrush`、`ScrollBar*`）。

**强调色一族刻意不覆盖**：`AccentFillColor*`、`TextOnAccentFillColor*`、`FocusStrokeColor*` 保持框架原样 —— 因为框架的实现本来就是从 `SystemAccentColor*` 派生的，也就是我们 `AppAccent*` 所引用的同一个源。于是 `ToggleSwitch` 的滑块填充、`CheckBox` 的对勾、`ProgressRing`/`ProgressBar`、`ComboBox` 选中项、`TextBox` 焦点下划线、`HyperlinkButton` **自动与用户强调色一致**，我们不需要（也不应该）在这里写任何值。这条正是"强调色跟随 Windows"这个决策最省力的部分：越少覆盖，越一致。

---

## 3. 字体体系

三族，各司其职，全部是 Windows 自带或随系统分发的字体，**零外部依赖**：

| 令牌 | 字族 | 用于 |
|---|---|---|
| `AppFontDisplay` | `Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI` | 页面标题、展板标题 |
| `AppFontText` | `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI` | 正文、控件、按钮 |
| `AppFontMono` | `Cascadia Mono, Consolas, Microsoft YaHei UI` | **所有数字与元数据** |

`ContentControlThemeFontFamily` 也被指向 Text 字面，让原生控件的默认文本一并归队。

### 字阶

| 样式 | 规格 | 用途 |
|---|---|---|
| `AppDisplayStyle` | 32 / SemiBold / 字距 −25 | 画廊展板标题 |
| `AppSectionTitleStyle` | 26 / SemiBold / 字距 −18 | 灯箱 / 设置页标题 |
| `AppEyebrowStyle` | 10.5 / SemiBold / **等宽** / 字距 +90 | 标题上方的分类眉标 |
| `AppCardTitleTextStyle` | 14 / SemiBold | 区块、卡片标题 |
| `AppBodyTextStyle` | 13 / 行高 20 | 正文 |
| `AppHintTextStyle` | 12.5 | 副标题、说明 |
| `AppLabelStyle` | 11.5 / SemiBold / 字距 +20 | 表单字段标签 |
| `AppCaptionTextStyle` | 11.5 / **等宽** / 字距 +10 | 状态、页脚、元数据 |
| `AppMonoStrongStyle` | 12 / SemiBold / **等宽** | 页码、计数、缩放百分比 |

字体回退链里显式写入 `Microsoft YaHei UI`，保证中英混排时中文字形的字重与西文协调。

---

## 4. 布局与导航结构

### 4.1 前后对比

**改版前**：一条 320px 常开侧栏同时承担导航与工具，内容区被吃掉四分之一宽度，而"图源"这个每次都要动的控件藏在侧栏深处。

**改版后**：按职责切成两层。

```
改版前                                    改版后
┌──────────┬──────────────────┐          ┌────┬────────────────────────────────┐
│ 侧栏 320 │                  │          │ 56 │ 命令条：图源 · 过滤 · 状态 · 操作│
│          │                  │          │    ├────────────────────────────────┤
│ ▤ 画廊   │                  │          │ ☰  │                                │
│ ▢ 查看器 │     内容         │          │ ▤  │            内容                │
│ ⚙ 设置   │                  │          │ ▢  │                                │
│          │                  │          │ ⚙  │                                │
│ 图源 ▾   │                  │          │    │                                │
│ NSFW ▾   │                  │          │ ◈  │                                │
│ 刷新     │                  │          └────┴────────────────────────────────┘
│ 清缓存   │                  │           导轨只回答「我在哪」
│ ● 就绪   │                  │           命令条只回答「我在看什么」
└──────────┴──────────────────┘
```

### 4.2 三层结构

**① 导轨（56px，`NavigationView` + `PaneDisplayMode="LeftCompact"`，默认收起）**
- 只负责"我在哪"：☰ 展开钮、三个导航项（画廊 / 灯箱 / 设置）各带图标与 2px 活动指示条、底部一枚暗化的应用标记。
- **永不抢宽度**：内容区相比旧版多出 264px。
- 展开态（☰）不是第二条侧栏，而是**应用标记 + 版本 + 快捷键速查表** —— 用一块真正有用的静态内容填补这块空间，而不是把控件搬回来。

**② 命令条（50px，常驻于内容上方）**
- 只负责"我在看什么"：`图源` 下拉、`过滤` 分段器、当前选中图的小缩略图、状态点 + 状态文本、`刷新`、`清理缓存`。
- 解决旧版最大的可用性问题：换图源不再需要先展开侧栏。
- 下缘 1px 极细线，与内容区分开，但不用色块或阴影。

**③ 页脚轨道（各页自带）**
- 同一套 `AppStatusBarStyle`：**透明底 + 只有上缘一条 1px 线**。状态栏从"一个盒子"退化成"一条轨道"，是把界面拉回编辑部排版的关键一步。

### 4.3 导航词汇的调整

`查看器` → **`灯箱`**。同一个目的地，但"灯箱"点明了它在这个设计语言里的角色：一张被照亮的台面，而不是"另一个页面"。

---

## 5. 核心页面

### 5.1 画廊 = 接触印相（Contact Sheet）

```
┌────────────────────────────────────────────────────────────────────┐
│ 接触印相                                                            │
│ 灵感画廊                                     ← 32px 展板标题        │
│ Gelbooru · 共 24 张图片                                             │
│ ────────────────────────────────────────────────────────────────   │
│ [🔍 标签搜索………] [标签 chip ×]   ┄ [随机模式] [自动刷新]           │
│                                                                    │
│    ▢     ▢     ▢     ▢     ▢                                       │
│    ▢     ▢     ▢     ▢     ▢          ← 4–6 列，栏距 10            │
│    ▢     ▢     ▢     ▢     ▢                                       │
│ ────────────────────────────────────────────────────────────────   │
│ ● 就绪  ⟳ ▊▊▊▊░░░░             第 3 页  ←  →  [取消] [下载全部]    │
└────────────────────────────────────────────────────────────────────┘
```

**结构变化**
- 旧版头部是三条并排功能带（标题 / 标签 / 工具栏）；新版压成"眉标 → 展板标题 → 说明 → 一条细线 → 筛选行"的编辑式层次。
- **翻页、批量下载、进度下放到页脚轨道**：这些是"关于这一批"的操作，不属于视图头部。头部因此只剩一个任务：说明你在看什么。
- 主操作（强调色实心）在整页只出现一次，就是页脚右侧的「下载全部」。

**网格变化**（`GalleryPage.xaml.cs`）
- 列宽除数（`IdealCardSize`）200 → **238**，栏距（`CardGutter`）8 → **10**，列数 `Clamp(…, 2, 8)`。
- **栏距的算法是这一轮修掉的一个真 bug**：旧写法把列宽算成 `(width − 10×(n−1)) / n`，却让卡片铺满整个瓦片 —— 于是间隙从没出现过（卡片彼此贴死），而算式里预留的那 `10×(n−1)` 变成了右侧空掉的一条。现在的规则是：**瓦片 = 行宽 ÷ 列数（精确铺满），卡片 = 瓦片 − CardGutter 并居中**。相邻两张之间自然留出 `CardGutter`，最外两侧各让出 `CardGutter / 2`，左右对称。
- 另一个配套修正：瓦片取 **`Math.Floor((width − 0.5) / columns)`** 而不是除法原值。`ItemsWrapGrid` 拿到的是 ScrollViewer 视口宽度，略小于 `GridView.ActualWidth`；若瓦片取原值，n 个瓦片会刚好超出视口，**症状是最右一列整列消失**（每行只排满 n−1 张），极易误判成"列数算错"。
- 结果是每一格读起来像"一张照片"，而不是"一个缩略图"。

**卡片变化**（`ImageCard.xaml`）
- 从"带描边的方形缩略图"变成**相纸**：圆角 12、只靠表面色阶与 1px 细线托住图片、默认不显示任何按钮。
- 悬停时 `HoverActions` 层同时亮起**整卡内描边（强调色 42%）**与右下角两枚玻璃快捷按钮 —— 因为代码只驱动这一层的 `Opacity`，"整卡提亮"与"按钮浮现"天然同步。
- 作者名与分辨率改为**等宽小字**；分辨率徽章用深色玻璃而不是纯黑块。

**状态覆盖**：加载中用微光扫过（低对比的一道光，只表示"正在显影"），失败用等宽错误说明，空状态是"一块压暗的相纸 + 一句下一步做什么"，而不是"暂无数据"。

### 5.2 灯箱 = Light Table

```
┌────────────────────────────────────────────────────────────────────┐
│ 灯箱                                                                │
│ Gelbooru · 1536×2048 · 3.2 MB      打开来源  SauceNAO  IQDB  ⧉     │
│ ═══════════════════════════════════════════════════════════════    │
│ 标签（点击浏览该标签下的图片）  [tag] [tag] [tag] …                 │
│ ┌────────────────────────────────────────────────────────────────┐ │
│ │                                                                │ │
│ │                        [ 图片 ]                                │ │
│ │                                                                │ │
│ │ [artist] [1536×2048]              ⟳  ⤓ │ − 100% + │ ⤢ ⬚ ⛶ │ ⛶ │ │
│ └────────────────────────────────────────────────────────────────┘ │
│ ● 就绪                                                             │
└────────────────────────────────────────────────────────────────────┘
```

**结构变化**
- 上下"边框"压到最薄：头部只有眉标 + 一行等宽元数据（没有大标题去占图片的垂直空间）；底部只有一条 42px 轨道。
- 图片台面 `Margin="20,16,20,14"` + 独立深色底衬 + 1px 细线 + 圆角 14，读作一块**嵌进桌面的灯箱凹槽**，而不是一个"图片区域"。
- **亮色模式下灯箱台面依然是深的** —— 这是刻意的：看图需要中性深底衬，否则浅底会污染对图片明度的判断。这是本设计里唯一"故意不统一"的地方，也是它不该被误读为配色事故的理由。
- 标签条用上下两道细线夹住，不再是卡片。
- 浮动工具栏：`CornerRadius="999"` 的玻璃条，像一块压在照片上的镇纸；等宽百分比数字。
- **F11 全屏是"三层一起收"**：窗口呈现器、外壳（导轨 + 命令条）与页内控件（工具栏 / 标签 / 状态条 / 圆角 / 内边距）同时让位，图片独占整屏。只看内容不看边框 —— 这是"暗房"隐喻的最后一格：关灯。Esc 或再按一次 F11 原样还原（窗口几何、最大化状态、最小尺寸约束都保留）。双击图片同样可进出。

### 5.3 设置 = 参数表（Spec Sheet）

```
┌────────────────────────────────────────────────────────────────────┐
│ 偏好设置                                                            │
│ 设置                                                                │
│ 调整图源、网络与下载行为，保存后立即生效                             │
│                                                                    │
│ 通用        NSFW 过滤                      [屏蔽|仅 NSFW|全部]      │
│ ─────────────────────────────────────────────────────────────────  │
│             主题                            [跟随系统|亮色|暗色]     │
│ ─────────────────────────────────────────────────────────────────  │
│             自动刷新                        [ 30 ]  ○             │
│             按设定间隔自动重新加载画廊                               │
│ ─────────────────────────────────────────────────────────────────  │
│ 网络与代理  手动代理地址                                            │
│             [ http://127.0.0.1:7890            ]                   │
│ ─────────────────────────────────────────────────────────────────  │
│ …                                                                  │
│ ─────────────────────────────────────────────────────────────────  │
│ [保存设置]  [恢复默认]   已保存                                     │
└────────────────────────────────────────────────────────────────────┘
```

**结构变化**
- 旧版是 Win11「设置」的复刻：分组标题 + 一摞圆角卡片。功能正确，但看起来像操作系统。
- 新版是**印刷品的双栏参数表**：左栏是分组名（页边注），右栏是逐行参数。
- **去卡片化**：`AppSettingsCardStyle` 被重新定义为"透明底 + 只有下缘一条 1px 线"。分组靠留白与眉标建立，不靠盒子。层级完全由留白与细线表达。
- 提交行用一条更强的细线与上方隔开，主操作是唯一一处强调色。

---

## 6. 组件库的关键重建

| 组件 | 规格 |
|---|---|
| `AppAccentButtonStyle` | 强调色实心 + 顶部 1px 高光（让实心块像被灯照到的实体）；悬停转 `AccentStrong`，按压降透明度并熄掉高光 |
| `AppPillButtonStyle` | 透明底 + 1px 细线；悬停加填充与描边；按压转强调色底衬 |
| `AppIconButtonStyle` | 32×32，悬停只加极淡填充并把图标提到主色，**不发光、不投影** |
| `AppOverlayIconButtonStyle` | 压在图片上的玻璃按钮（深底 + 白图标） |
| `AppSegmentRadioStyle` / `AppSegmentToggleStyle` | 细线分段器：容器只是凹槽，选中项用强调色底衬 + 强调色描边，未选中项完全透明 |
| `AppChipBorderStyle` / `AppTagChipButtonStyle` | 描边胶囊标签，等宽小字；悬停整枚转强调色 |
| `AppFloatingToolbarStyle` | 半透明玻璃 + 1px 内描边模拟折射边缘（不是 `backdrop-blur` 的平铺版） |
| `AppStatusBarStyle` | **透明底 + 仅上缘 1px 线**，圆角 0 |
| `AppSettingsCardStyle` | **透明底 + 仅下缘 1px 线**，圆角 0 |
| 状态点三档 | 灰 = 安静 / 强调色 = 工作 / 红 = 故障 |

**必须显式归零的默认值（`MinWidth` 陷阱）**：WinUI 的 `Button` / `RadioButton` 基线模板带 `MinWidth=120`、`MinHeight=32`。而 `FrameworkElement.Width` **会被 `MinWidth` 夹住** —— 所以导轨项写 `Width="56"` 实际渲染成 **120px 宽**，直接顶出 56px 导轨并压住右侧内容（这正是"侧栏被覆盖"的根因，不是层级问题）。凡是自定义尺寸的 `AppRailItemStyle`、`AppPillButtonStyle`、`AppIconButtonStyle` 都必须同时写 `MinWidth="0"` / `MinHeight="0"`。

**焦点可见性**：所有自定义模板都设 `UseSystemFocusVisuals="False"`，改用模板内的 2px `AppFocusRingBrush` 焦点环（`FocusStates` 组）。注意实心强调色块上的焦点环要改用 `AppAccentInkBrush` —— 否则强调色环压在强调色块上会完全看不见。

---

## 7. 动效与交互反馈

| 场景 | 处理 |
|---|---|
| 卡片入场 | 保留错峰（28ms/张），Y 位移 14 → 0 + 透明度渐入 |
| 卡片悬停 | 缩放（既有代码驱动）+ 整卡内描边提亮 + 快捷按钮浮现，三者同层同源 |
| 按钮按压 | 填充变暗 / 转强调色底衬 —— **不使用 `scale()`**（VisualState 的 Setter 无法表达 CSS 的 `transform`，这是本轮修掉的一个真实隐患） |
| 焦点 | 2px 强调色焦点环，非系统焦点框（实心强调块上改用墨色环） |
| 加载 | 持续微光扫过（低对比）而非旋转圈；进度用强调色进度条 |
| 空 / 错 / 忙 | 三态齐备，文案直接陈述（"换一批看看"、"加载失败：…"），不用"哎呀" |
| 全屏（F11） | 外壳与页内控件同时收起，图片独占整屏；进入时自动回到"适应屏幕"。与焦点无关（窗口级加速器）；Esc 只在全屏状态下才被根部接管 |

动效只作用于 `transform` 与 `opacity`，不触发重排。

---

## 8. 落地清单

### 重写
| 文件 | 改动 |
|---|---|
| `Styles/AppStyles.xaml` | 全量重写：令牌、字族、字阶、表面、按钮（含焦点环模板）、分段器、状态、标签、设置行 |
| `App.xaml` | 亮/暗两套 ThemeDictionaries 重写（中性阶 + `AppAccent*`→`SystemAccentColor*`）+ 全局字族 |
| `Styles/FluentTheme.xaml` | 约 55 个 Fluent 中性基线画刷覆盖；**合并顺序必须排在 `XamlControlsResources` 之后**，否则同名键不生效 |
| `MainWindow.xaml` | 320px 侧栏 → 56px 导轨 + 常驻命令条；面板内容改为应用标记 + 快捷键速查 |
| `Views/GalleryPage.xaml` | 三条工具带 → 编辑式头部 + 页脚轨道（翻页/批量下载下放） |
| `Views/ViewerPage.xaml` | 头部压薄、灯箱凹槽、玻璃药丸工具栏、细线标签条 |
| `Views/SettingsPage.xaml` | 卡片堆 → 双栏参数表（去卡片化） |
| `Controls/ImageCard.xaml` | 缩略图 → 相纸（无边框 + 悬停内描边 + 等宽元数据） |
| `Controls/SegmentedControl.xaml` | 胶囊容器 → 细线凹槽 |
| `Controls/GalleryViewerWindow.xaml` | 与灯箱对齐（玻璃药丸工具栏、细线标签条） |

### 小幅修改（保持行为不变）
| 文件 | 改动 | 原因 |
|---|---|---|
| `MainWindow.xaml.cs` | 删除 `ApplyPaneLayout`、`OnPaneOpening`、`OnPaneClosing` 及其调用；更新类注释；`Root.Loaded` 追加 `Win11Chrome.SetInitialBounds` | 命令条取代了侧栏里的控件区，面板展开逻辑不再需要；窗口需要一个明确的最小尺寸 |
| `Views/GalleryPage.xaml.cs` | 网格常量与算法：`CardGutter=10`、`IdealCardSize=238`、`MaxColumns=8`；瓦片 `Math.Floor((width−0.5)/columns)`，卡片 = 瓦片 − 栏距并居中 | 接触印相的节奏；同时修掉"间隙不出现 + 最右一列消失"（见 §5.1） |
| `Views/SettingsPage.xaml` | `ScrollViewer` 关闭横向滚动/缩放；内容 `MaxWidth=1000` + `HorizontalAlignment="Center"`；左栏 146px + 右栏 `*` | 修掉整页右移溢出（见下方已知陷阱） |
| `MainWindow.xaml` | 命令条列定义改 `Auto,Auto,Auto,*,Auto`；状态区包一层内层 `Grid`（圆点 `Auto` + 文本 `*` + `TextTrimming`）；导轨列命名 `RailColumn`、导轨 `RailHost`、命令条 `CommandBarHost`；根部加窗口级 `KeyboardAccelerator`（F11）与 `KeyDown`（Esc） | 窄窗口下只有状态文本收缩；让"全屏"能连外壳一起收，且 F11 与焦点无关地生效 |
| `Controls/ImageCard.xaml` | 根元素 `HorizontalAlignment="Center"` / `VerticalAlignment="Center"` | 卡片在瓦片内居中，栏距才真正可见 |
| `Win11Chrome.cs` | 标题栏底色 `#121216` → `#0D0E11`（亮色 `#F6F6F9` → `#F7F7F9`）；新增 `SetInitialBounds`：`PreferredMinimumWidth/Height` = 980×620 逻辑像素（× `RasterizationScale`），首选 1360×860，并按工作区 − 48px 边距收口；新增 `IsFullscreen` / `SetFullscreen`（含几何与最大化状态还原、退出全屏后重设最小尺寸） | 让系统标题栏与新画布连成一片；避免窗口被拖到命令条挤成一团；把 F11 的窗口侧动作集中到一处 |
| `Views/ViewerPage.xaml.cs` | 不再自己调 `AppWindow.SetPresenter`，改为实现 `IImmersivePage` 并把 F11 交给外壳；删除页内 `EnterFullscreen` / `ExitFullscreen` | 见下方"全屏"两条坑位 |
| `Controls/GalleryViewerWindow.xaml.cs` | F11 改用 `Win11Chrome.SetFullscreen` | 独立大图窗口同样需要还原窗口几何 |
| `MainWindow.xaml.cs` | 新增 `IsFullscreen` / `SetFullscreen` / `ToggleFullscreen` / `ExitFullscreen` / `ApplyImmersiveChrome` 与两个键盘处理；`NavigateTo` 先退全屏；新增 `IImmersivePage` 接口 | 全屏开关的唯一入口；避免"全屏中换页"后外壳再也回不来 |
| `Views/GalleryPage.xaml` | 标签联想弹层 `TagSuggestPopup` 由头部 `StackPanel` 末尾移到根 `Grid` 的第一个子元素 | 让偏移量的坐标原点变成页面原点（见下方"弹层"坑位） |
| `Views/GalleryPage.xaml.cs` | `PositionTagSuggestPopup` 的纵向下移量 `ActualHeight + 6` → `ActualHeight` | 面板顶边与输入框底边重合；原来固定留 6px，读起来是"悬在半空"而不是"挂在输入框上" |

**坑位记录（本轮实测出来的）**
- **`ScrollViewer` + `HorizontalAlignment=Stretch` + `MaxWidth` 组合会让整页右移**：内容既想拉伸又要限宽时，布局会向左偏移并让右缘溢出（实测分隔线跑到 x=1430，超出 1440 窗口的可用区）。改用 `HorizontalAlignment="Center"`（对齐目标明确）后左右边距恢复对称。
- **窗口尺寸必须用物理像素**：`OverlappedPresenter.PreferredMinimumWidth` 接受的是物理像素，逻辑值要乘 `XamlRoot.RasterizationScale`，否则在高 DPI 下最小尺寸形同虚设。
- **"全屏"是外壳的能力，不是页面的**：`MainWindow` 的导轨与命令条挂在窗口外壳上，页面只知道自己那一层。原本只有查看器收起自己的工具栏，于是 F11 之后窗口确实满屏了，左边却仍立着 56px 导轨、顶上仍压着命令条 —— 看起来就是"全屏有问题"。修法是把全屏开关上移到 `MainWindow.SetFullscreen`，并由它统一处理三层：窗口呈现器、外壳（导轨列宽 + 命令条）、页内控件（`IImmersivePage`）。
- **`SetPresenter(Overlapped)` 会丢掉最小尺寸**：退出全屏时框架给的是**新的** `OverlappedPresenter` 实例，`SetInitialBounds` 里设过的 `PreferredMinimumWidth/Height` 挂在被替换掉的旧实例上。不重设的话，F11 进出一轮窗口就又能被拖到命令条互相压盖 —— 等于把刚修好的问题放回去。这条是上一轮"设最小尺寸"埋下的回归，只有真实按一次 F11 才会暴露。
- **F11 要用 `KeyboardAccelerator`，不能用页面 `KeyDown`**：`KeyDown` 只在有焦点的元素上向父级冒泡，焦点落在启动时空着的内容区就完全收不到；加速器是窗口级的，与焦点无关。反过来 **Esc 要用 `KeyDown` 且只在全屏时接管** —— 那样被 `ComboBox` / 弹层消化掉的 Esc 不会冒泡到根部，不会被误当成"退出全屏"。
- **`Popup` 的偏移量不是窗口坐标，而是「父容器内容原点 + 自身布局槽位」**（文档只写"距窗口左边缘"，实测不是）。关键在于：那个**槽位原点已经包含了排在它前面的兄弟元素高度**。标签联想弹层原本挂在头部 `StackPanel` 的末尾，槽位原点已经累积了眉标 / 标题 / 说明 / 细线 / 筛选行的全部高度（实测 172px），而代码又把整份"相对页面"的坐标加了上去 —— 偏移被加了两遍：弹层落到搜索框下方约 200px 处，横向还偏右 26px（1440×900 @125% 实测：`TagBox` 底边 y=336，弹层顶边 y=589，而正确答案是 y≈341）。
  修法**不是**去减掉那 172px（那是把布局算式硬编码进代码，头部一改就再次失准），而是**把弹层挂到"原点与页面重合"的根 `Grid` 下、并让它成为第一个子元素** —— 首个子元素的槽位原点就等于父容器内容原点，于是 `TagSearchHost.TransformToVisual(this)` 算出来的页面坐标直接就是正确答案，offset 的语义被钉死为单一解释。`Popup` 的 `DesiredSize` 为 0，落在第 0 行不会撑高行高（实测头部与网格位置零位移）。
- **这类问题静态看不出来**：`PositionTagSuggestPopup` 里的 `TransformToVisual(this)` 本身没有任何可疑之处，只有真的打开一次弹层、量一次屏幕坐标，才知道它被画到了哪儿。

### 零破坏保证
- 所有 `x:Name` 与事件处理器签名**逐一保留**，代码后置无需改动即可编译。
- 唯一删除的元素（`PaneControls`、`PaneFooterExpanded`、`PaneFooterCompact`、`ClearCacheButtonText`、`RefreshButtonText` 的标签）经全仓检索确认**没有任何代码引用**。
- `AppAccent*` 令牌在 `App.xaml` 中**指向** `SystemAccentColor*` 基线，`WindowsColorScheme` 注册时立即 `Apply()` 一次并在系统主题/强调色变化时重写 —— 强调色从此刻起是活的，不再是常数。

### 验证
```
dotnet build src\AnimeDownloader.App\AnimeDownloader.App.csproj -c Debug
→ 已成功生成。0 个警告，0 个错误
```

### 实机验证（本轮补做，非静态检查）

用 UI Automation（`RawViewWalker` DFS 按 `AutomationId` 定位）驱动真实窗口，逐页截图并采样像素：

| 验证项 | 方法 | 结果 |
|---|---|---|
| 强调色 == Windows 强调色 | 读注册表 `DWM\AccentColor`，与截图采样比对 | 两侧都是 `#BF0077`（品红），一致 |
| 导轨未被压住 | 采样导轨背景横向跨度 | 导轨底 `#0A0B0D` 覆盖 x=0..77，内容自 x=78 起；选中项底色恰为强调色 |
| 图库栏距与列数 | 采样卡片横向边界 | 1440×900 下 4 列，相邻间隙 10–12px |
| 设置页居中 | 采样分隔线横向范围 | x=548..1187，左右边距 162/163，对称 |
| 命令条窄窗不塌 | 1260×780 下逐列测量 | 固定列完整，仅状态文本截断 |
| 标签联想弹层贴合输入框下沿 | 触发联想后，在接缝处做纵向逐行像素采样 | 输入框下边框 y=320、弹层上边框 y=321、卡片底自 y=322 起 —— **中间没有任何背景色行**；左边缘弹层 138 / 输入框 138.5 |
| 亮色模式 | 以 `ANIMEDOWNLOADER_CONFIG_DIR` 指向亮色配置启动 | 白底、强调色压到 `Dark1`、墨色自动转深 |
| F11 全屏（画廊） | 向真实窗口发送 F11，用 UIA 统计外壳控件数量 | 窗口 1440×900 → **1920×1200 @0,0**（= 显示器全幅）；外壳控件 **6/8 → 0/8**（导轨三项 + 图源 + 刷新 + 清缓存全部离开 UIA 树），再按 F11 全部回来 |
| F11 全屏（灯箱） | 同上，另测工具栏全屏按钮与 Esc | F11 / 按钮 / Esc 三条路径均正确进出；外壳同样 6/8 ↔ 0/8 |
| 退出全屏后最小尺寸仍在 | F11 进出一轮后请求 `SetWindowPos(600×400)` | 被夹到 **1225×775**（= 980×620 × 1.25 DPI），约束未丢 |
| 构建 | `dotnet build -c Debug` | `exit=0`，0 错误 0 警告 |

### 仍需人工确认
1. **多显示器**：F11 全屏落在窗口当前所在的那块显示器上（`FullScreenPresenter` 的行为），跨显示器拖动后再进全屏是否符合预期未实测；退出时按记录的 `RectInt32` 还原，若期间显示器拓扑变化（拔掉外接屏）还原点可能落在屏外 —— 已按"记录值优先"处理，未做兜底夹取。
2. `Cascadia Mono` 未安装时的回退（已配 `Consolas → Microsoft YaHei UI`）。
3. 用户在**应用运行期间**切换 Windows 强调色时，`UISettings.ColorValuesChanged` 的刷新时机（已实现回写，但未逐帧实测时序）。
4. 亮色模式下灯箱台面的深底衬是否符合预期（设计意图见 §5.2）。
5. 多显示器 + 混合 DPI 下 `SetInitialBounds` 依据哪个显示器的 `RasterizationScale`（当前取窗口所在 XamlRoot）。

---

## 9. 参考来源

- `E:\skills\UI\awesome-design-md\design-md\linear.app\DESIGN.md` — 表面色阶、display 负字距、单一彩色的克制用法
- `E:\skills\UI\awesome-design-md\design-md\raycast\DESIGN.md` — 极细线 + 无阴影、键位等宽、单一高对比主操作
- `E:\skills\UI\taste-skill\skills\redesign-skill\SKILL.md` — 审计清单（俗套识别、状态齐备、动效只用 transform/opacity）
- `E:\skills\UI\taste-skill\skills\taste-skill-v1\SKILL.md` — AI 特征禁令（禁纯黑、禁紫蓝渐变、禁等宽三栏卡片、禁外发光、禁 `#000`）
