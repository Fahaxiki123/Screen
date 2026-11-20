# Screen UI 改版实施方案 · v4「克制的玻璃」

> 本文是交给实施者（AI 编码助手或开发者）的完整说明。视觉目标以 [`docs/design/ui-preview-v4.html`](design/ui-preview-v4.html) 为准，用 Edge / Chrome 打开，按空格可在「现在 / 优化后」之间切换。
>
> 一句话目标：**功能一个不少，界面更轻、更干净；用中性深色磨砂玻璃加克制的细节，做出苹果式的质感。**

---

## 0. 实施者须知（必读）

1. **只改 UI/UE，不改业务逻辑。** 截图、OCR、翻译、录屏编码、设置持久化、凭据存储的行为保持不变；第 5.7 节的 GIF 是唯一新增的业务能力。
2. **功能对等。** 现有每个按钮、选项、快捷键都必须保留，只改样式、排布和文案。下面明确列出的删除项除外（模式栏、摄像头占位）。
3. **分阶段提交。** 按第 9 节的阶段推进，每个阶段单独提交，提交前运行 `./verify.ps1`（构建、测试、格式检查都要通过）。
4. **兼容 Windows 10 21H2+。** 只有 Windows 11 才有的效果（Mica）必须带回退方案。
5. **样式只写在 XAML 资源里。** C# 中不再出现颜色字面量、圆角、阴影参数，统一用 `SetResourceReference` / `FindResource` 引用 token 或 style。
6. **不引入新的 NuGet 包。** 动画用 WPF 自带的 Storyboard 和 Easing 实现。
7. 测试里有 5 个文件手动注册了 `Brush.Accent`、`Brush.OverlayBase`、`Brush.OverlayBorder`、`Brush.OverlayText`、`Brush.OverlayTextDim`、`Brush.OverlayIcon`、`Brush.Neutral500`。**这些 key 必须保留**（可以作为别名指向新值），否则测试会失败。

---

## 1. 范围

### 要做
| 区域 | 主要文件 |
| --- | --- |
| 设计 token 与控件样式 | `Themes/Tokens.xaml`、`Themes/GraphiteControls.xaml`、`App.xaml`，新建 `Themes/Controls.xaml` |
| 截图浮层 | `CaptureOverlayWindow.*.cs`、`CaptureSurface.Rendering.cs`（选区和放大镜配色）、`ToolbarIcons.cs` |
| 文字 / 翻译卡片 | `OcrCard.cs`、`TranslationCard.cs`、`InPlaceTranslationRenderer.cs`（只改配色） |
| 录屏 | `CaptureOverlayWindow.UI.cs`（`BuildRecordingPanel`）、`RecordingCountdownWindow.cs`、`RecordingControlWindow.cs`、`RecordingBorderWindow.cs`、`RecordingPreviewWindow*.cs`、`RecordingController.cs`、`Screen.Core/Models.cs` |
| 设置窗口 | `SettingsWindow.xaml`、`SettingsWindow.xaml.cs`、`SettingsWindow.View.*.cs`（`SettingsWindow.ViewModel.cs` 尽量不动） |
| 反馈 | `HintToastWindow.cs`（改造），新建 `CaptureThumbnailWindow.cs` |
| 其他窗口 | `PinWindowManager.cs`（菜单）、`GraphiteToolStripRenderer.cs`（托盘菜单）、`LongScreenshotEditorWindow.cs`、`ScrollCaptureController.cs`、`TextPromptWindow.cs`：只做 token 换色 |
| FFmpeg | `tools/ffmpeg/build-ffmpeg.sh`、`Build-Ffmpeg.ps1`、第三方声明（为支持 GIF） |

### 不做
- 不改截图、OCR、翻译、编码的算法。
- 不做设置搜索框（原型里的搜索框只是装饰）。
- 不做摄像头画中画，相关占位直接删除。
- 不做浅色主题（这一版统一深色）。

---

## 2. 设计规范

### 2.1 颜色 token（替换 `Themes/Tokens.xaml`）

界面本身是中性灰，颜色只来自三处：**内容本身、彩色图标、唯一的蓝色强调色**。录制红只出现在录制相关的地方。

| Token | 值 | 用途 |
| --- | --- | --- |
| `Color.Accent` | `#0A84FF` | 唯一强调色，与 App 图标同色系 |
| `Color.AccentTop` / `Color.AccentBottom` | `#3D9BFF` / `#0A7AEE` | 主按钮的竖向渐变 |
| `Color.Record` | `#FF453A`（渐变 `#FF6259` → `#E8352B`） | 录制按钮、录制状态 |
| `Color.Success` | `#32D74B` | 连接成功等状态 |
| `Color.Warning` | `#FFD60A` | 警告 |
| `Color.Label` | `#F5F5F7` | 主文字 |
| `Color.LabelSecondary` | `#99EBEBF5`（60%） | 次文字 |
| `Color.LabelTertiary` | `#4DEBEBF5`（30%） | 三级文字、禁用 |
| `Color.GlassTintTop` / `Color.GlassTintBottom` | `#853C3C42` / `#99262C2C` | 玻璃底色（中性深灰，约 52%→60%） |
| `Color.GlassWhite` | `#0DFFFFFF`（5%） | 玻璃叠白 |
| `Color.GlassSolid` | `#EB26262A` | 无法模糊时的玻璃替代色（实时桌面上的浮窗） |
| `Color.DarkGlassSolid` | `#F21C1C20` | 浮在亮色内容上的菜单 |
| `Color.Window` | `#1E1E20` | 设置窗口回退底色 |
| `Color.Group` | `#0FFFFFFF`（6%） | 设置分组底 |
| `Color.Separator` | `#1AFFFFFF` | 分隔线 |
| `Color.Hover` / `Color.Pressed` | `#12FFFFFF` / `#1FFFFFFF` | 悬停 / 按下 |
| `Color.Selected` | `#29FFFFFF` → `#1FFFFFFF` | 选中块（竖向渐变） |
| `Color.EdgeHighlight` | `#42FFFFFF`（26%） | 左上亮边 |
| `Color.EdgeDark` | `#80000000` | 外圈暗色发丝线 |

**兼容别名**：旧 key（`Brush.TextPrimary`、`Brush.Surface`、`Brush.ChromeGradient`、`Brush.GlassRim`、`Brush.AuroraCard`、`Brush.PrimaryAction`、`Brush.Ruby*`、`Brush.Overlay*`、`Brush.Neutral*`、`Color.Neutral*` 等，C# 中目前约 100 处引用）在第 1 阶段**全部保留，但改成指向新值**：
- 所有渐变类（`ChromeGradient`、`AuroraCard`、`BrandGradient`、`WindowBackdrop`）→ 改成玻璃底色或 `Color.Window` 的纯色或近似纯色。
- `PrimaryAction` → 蓝色竖向渐变；`Ruby*` → 录制红。
- `AccentText` → `#6CB4FF`；`AccentSubtleDark` → `#6B0A84FF`（42% 蓝，用于「激活中」的按钮）。
- `Neutral50…900` → 映射到中性灰阶 `#1C1C1E`、`#242426`、`#2C2C2E`、`#3A3A3C`、`#48484A`、`#636366`、`#8E8E93`、`#AEAEB2`、`#D1D1D6`、`#E5E5EA`、`#F5F5F7`。

这样第 1 阶段只改一个文件，整个 App 的色调就会切换过来。别名在第 10 阶段逐步清理。

### 2.2 圆角（只用这 6 档）

| Token | 值 | 用于 |
| --- | --- | --- |
| `Radius.Key` | 5 | 键帽、小徽标 |
| `Radius.Control` | 6 | 输入框、下拉、小按钮、菜单项、提示气泡 |
| `Radius.Button` | 8 | 工具按钮、主按钮、录制按钮、分段控件外框 |
| `Radius.Menu` | 10 | 菜单、弹出层 |
| `Radius.Bar` | 12 | 工具栏分组、样式面板（11）、尺寸标签以外的 HUD、窗口 |
| `Radius.Card` | 14 | 文字卡片、翻译卡片、设置分组（11） |

> [!IMPORTANT]
> 不使用胶囊形（全圆角）。只有开关滑轨、颜色圆点、倒计时圆环这类本来就是圆形的元素才用圆形。

### 2.3 字体

- 正文 `Segoe UI Variable Text, Microsoft YaHei UI`；标题 `Segoe UI Variable Display, Microsoft YaHei UI`。
- 字号只用 **11 / 12 / 13 / 15 / 20** 五档。正文 13，次要信息 12，徽标 11，卡片和区块标题 15，页面标题 20。
- 字重：正文 Normal，按钮和标签 SemiBold（600），标题 SemiBold 或 Bold。
- 计时器、坐标等数字使用等宽数字（`Typography.NumeralAlignment="Tabular"`）。

### 2.4 「克制的玻璃」材质

五步配方，对应原型顶部的开关：

| 步骤 | 原型 CSS | WPF 实现 |
| --- | --- | --- |
| ① 模糊 | `blur(26px)` | 浮层内：预先模糊截图（见 4.1）；其他窗口：不模糊，用 `GlassSolid` |
| ② 增色 | `saturate(170%)` | 和模糊一起预处理 |
| ③ 叠白 | 白 5%，叠在中性深灰 52%→60% 上 | 一层 `GlassTint` 竖向渐变，再叠一层 `GlassWhite` |
| ④ 亮边 | `inset 1px 1px rgba(255,255,255,.26)` | 内层 1px Border，渐变描边从左上 `#42FFFFFF` 到右下 `#0AFFFFFF` |
| ⑤ 折射 | 默认关闭 | **不实现** |

阴影（原型：`0 0 0 .5px 黑50%`、`0 2px 6px 黑16%`、`0 18px 44px -10px 黑50%`）在 WPF 中用两层实现：
- 外圈 1px `#80000000` 描边（模拟 0.5px 暗色发丝线）。
- 一个 `DropShadowEffect`：`BlurRadius=28, ShadowDepth=8, Direction=270, Opacity=0.4, Color=Black`。

> [!WARNING]
> `DropShadowEffect` 只能加在**单独的背景层**上，不能加在含文字的容器上，否则文字会变糊，而且性能差。结构见 3.1。

### 2.5 动效

| 场景 | 时长 | WPF 缓动 |
| --- | --- | --- |
| 工具选中块滑动 | 450ms | `BackEase { Amplitude = 0.3, EasingMode = EaseOut }`（TranslateX + Width） |
| 面板 / 卡片 / 菜单出现 | 380ms | 不透明度 0→1；缩放 0.96→1（菜单从 0.94 开始）、Y −6→0；`BackEase { Amplitude = 0.35 }` |
| 颜色点选中 | 450ms | 缩放 0.6→1，`BackEase { Amplitude = 0.5 }` |
| 开关 | 350ms | 滑块 TranslateX，`BackEase { Amplitude = 0.3 }`；轨道颜色 200ms 线性 |
| 分段控件滑块 | 400ms | 同选中块 |
| 悬停 / 按下 | 150ms 线性 / 按下缩放到 0.9 | `ScaleTransform`，松开回弹 `BackEase` |
| 提示气泡 | 延迟 450ms 出现 | `ToolTipService.InitialShowDelay=450` |
| 成功提示框 | 出现 450ms，停留 1.3s，消失 250ms | 对勾路径描边动画（`StrokeDashOffset` 1→0，400ms） |
| 缩略图滑入 / 滑出 | 600ms / 300ms | `BackEase { Amplitude = 0.3 }` |
| 倒计时数字 | 每拍 700ms | 缩放 1.5→1 + 不透明度 0→1；圆环 `StrokeDashOffset` 线性走满 |

统一写在一个静态类 `Motion` 里（例如 `Motion.SlideTo(element, x, width)`、`Motion.PopIn(element)`），不要到处手写 Storyboard。

---

## 3. 组件库（新建 `Themes/Controls.xaml` 和 `Controls/` 目录）

| 组件 | 类型 | 说明 |
| --- | --- | --- |
| `GlassPanel` | 自定义 `ContentControl` | 见 3.1，所有浮层、卡片、菜单、HUD 都用它 |
| `HudIconButtonStyle` | Button 样式 | 34×34，圆角 8，图标 18px，描边 1.7；悬停 `Color.Hover`；按下缩放 0.88；禁用时不透明度 0.3 |
| `HudToolButtonStyle` | RadioButton 样式 | 外观同上；选中时由所在分组的滑动选中块表示；选中的封闭图形（矩形、椭圆、序号、选择）图标轻微填充（填充不透明度 0.22） |
| `HudActiveButton` 状态 | 附加属性 `IsActive` | 激活态（提取文字 / 翻译卡片打开时）：42% 蓝色底 + 内部 1px 高光 |
| `PrimaryButtonStyle` | Button 样式 | 高 34，圆角 8，蓝色竖向渐变，左上 1px `#61FFFFFF` 高光，`0 2 10 #6B0A84FF` 蓝色投影；文字 13 SemiBold |
| `RecordButtonStyle` | Button 样式 | 同上，换成录制红 |
| `SegmentedControl` | 自定义控件 | `ItemsSource` + `SelectedIndex`；外框圆角 8，底色 `#38000000` 加内凹阴影；滑块圆角 6，`#42FFFFFF→#26FFFFFF` 渐变加左上高光，用 `Motion.SlideTo` 动画 |
| `ToggleSwitchStyle` | ToggleButton 样式 | 40×24 圆形轨道；关闭 `#47000000`，打开时蓝色渐变；滑块 20×20 白色渐变；按下时滑块拉宽到 25 |
| `KeycapStyle` | Border 样式 | 高 22，圆角 5，`#4D4D52→#3A3A3C` 渐变，底部 1px 黑色投影（键帽厚度）；字号 11.5 SemiBold |
| `GlassToolTipStyle` | 全局 ToolTip 样式 | `DarkGlassSolid` 底，圆角 6，内边距 8×4，字号 11.5，1px `#1FFFFFFF` 描边 |
| `GlassMenuStyle` | ContextMenu / Popup 样式 | `GlassPanel` 加圆角 10，菜单项高 30、圆角 6，悬停为蓝色渐变底 |
| `SettingsGroup` / `SettingsRow` | 样式或 UserControl | 分组：6% 白底，圆角 11，左上 1px 12% 高光；行：最小高度 44，左右内边距 14，行间分隔线左右各缩进 14（行首有图标时左侧缩进 48） |
| `IconTile` | 小控件 | 设置里的彩色方形图标：圆角为尺寸的 24%，竖向渐变 + 左上高光，白色图标占 60%。配色见 7.2 |

### 3.1 `GlassPanel` 结构

```xml
<Grid>
  <!-- 0 阴影层：只有它带 DropShadowEffect -->
  <Border CornerRadius="{r}" Background="#01000000" Effect="{StaticResource Shadow.Glass}" />
  <!-- 1 外圈暗线 -->
  <Border CornerRadius="{r}" BorderBrush="#80000000" BorderThickness="1" ClipToBounds="True">
    <Grid>
      <!-- 2 背景（浮层中为预模糊截图的 ImageBrush；其他窗口为透明） -->
      <Border x:Name="Backdrop" CornerRadius="{r-1}" />
      <!-- 3 压暗层：面板在选区外时 Opacity=1（与浮层压暗一致：#6B000000） -->
      <Border x:Name="Dim" CornerRadius="{r-1}" Background="#6B000000" />
      <!-- 4 玻璃底色 + 叠白（没有背景时改用 GlassSolid） -->
      <Border CornerRadius="{r-1}" Background="{GlassTint}" />
      <Border CornerRadius="{r-1}" Background="{GlassWhite}" />
      <!-- 5 亮边 -->
      <Border CornerRadius="{r-1}" BorderThickness="1" BorderBrush="{EdgeHighlightGradient}" />
      <!-- 6 内容 -->
      <ContentPresenter Margin="{Padding}" />
    </Grid>
  </Border>
</Grid>
```

依赖属性：`CornerRadius`、`Padding`、`Variant`（`Glass` / `Solid` / `DarkSolid`）、`BackdropSource`（`ImageSource`，可空）。

---

## 4. 截图浮层（`CaptureOverlayWindow`）

### 4.1 背景模糊（实现玻璃质感的关键，做法很简单）

浮层背后就是我们自己截的 `_frame`，所以不需要系统模糊，只要**预先算一张模糊图**：

1. 新建 `GlassBackdropProvider`：浮层打开时在后台线程（`Task.Run`）处理 `_frame.Pixels`（BGRA）。
   - 按 1/4 尺寸做盒式下采样。
   - 做 3 次水平加竖直的盒式模糊，半径 `round(26 × dpiScale / 4 / 2)`，合起来约等于 26px 的高斯模糊。
   - 增色：`gray = 0.2126R + 0.7152G + 0.0722B`，`c' = gray + (c − gray) × 1.7`，结果截断到 0–255。
   - 输出一个冻结的 `BitmapSource`。4K 截图应在 30ms 内完成。
2. 在 `PositionFloatingPanels()` 的末尾，对每个可见的 `GlassPanel`：
   - `Backdrop.Background = new ImageBrush(blurred) { ViewboxUnits = Absolute, Viewbox = 面板在浮层里的 DIP 矩形 × (frame.Width / ActualWidth) / 4, Stretch = Fill }`。
   - `Dim.Opacity` = 面板中心在选区外时为 1，否则为 0。
   - 模糊图还没算好时，面板改用 `GlassSolid`；算好后不需要重新布局，直接换上背景即可。
3. 标注不会出现在玻璃背景里，这是可以接受的。

### 4.2 框选前

- **删除顶部模式栏**（`BuildModeBar`、`_modeButtons`、`UpdateModeButtons`，以及 `PositionFloatingPanels` 里相关的放大镜避让区）。`CaptureEntryMode` 只保留给录屏模式使用；Screenshot / Ocr / Translation / ScrollCapture 这几个入口删除，`OnSelectionCommitted` 里对应的分支也一并删掉。
- 压暗从 42% 改为 14%（只在框选前，框选后仍为 42%）。
- 放大镜（`CaptureSurface.Rendering.cs`）重新配色：外圈 4px 白色 85% 环，加 1px 黑色外线；中心像素用 1px 白框加 1px 黑框；下方信息条用 `GlassPanel`（圆角 7），内容为「物理坐标 x, y │ 色块 + #HEX」。
- 底部居中显示一个 `GlassPanel` 提示条（圆角 12）：`[拖动] 截取区域  [单击] 截取窗口  [Esc] 取消`，方括号部分用键帽样式。选区确定后隐藏。
- 十字线改为 `#8CFFFFFF`，1px。

### 4.3 选区

- 边框为 1px `#F2FFFFFF`。
- 8 个手柄是直径 9 的白色圆点，带 0.5px 黑色描边和 `0 1 3 #73000000` 投影。
- 尺寸标签放在选区左上角上方 26px：`GlassPanel`（圆角 6，内边距 8×3），字号 11 SemiBold，等宽数字，内容如「600 × 200」。

### 4.4 工具栏（21 个控件全部保留，分 4 组）

工具栏是一个横向 `StackPanel`，组间距 8，每组是一个 `GlassPanel`（圆角 12，内边距 4）：

| 分组 | 顺序（Lucide 图标 · 提示文字） |
| --- | --- |
| **绘制**（9 个，`RadioButton` 组，带滑动选中块） | 选择 `MousePointer2` · 选择 ／ 矩形 `Square` · 矩形 ／ 椭圆 `Circle` · 椭圆 ／ 直线 `Minus`（旋转 45°）· 直线 ／ 箭头 `MoveUpRight` · 箭头 ／ 画笔 `PencilLine` · 画笔 ／ 文字 `Type` · 文字 ／ 序号 `CircleNumber` 或自绘的圆圈数字 · 序号 ／ 马赛克 `ToolbarIcons.Mosaic*` · 马赛克 |
| **智能**（6 个控件） | 贴图 `Pin` · 贴到桌面 ／ 翻译 `Languages` · 翻译 ／ 语种 `ChevronDown`（宽 16，左侧负边距 −5，和翻译按钮贴在一起）· 翻译成… ／ 提取文字 `ScanText` · 提取文字 ／ 长截图 `ScissorsLineDashed`（旋转 90°）· 长截图 ／ 录制 `Video` · 录制此区域 |
| **历史与输出**（4 个） | 撤销 `Undo2` · 撤销　Ctrl+Z ／ 重做 `Redo2` · 重做　Ctrl+Y ／ 存储 `Download` · 存储　Ctrl+S ／ 取消 `X` · 取消　Esc |
| **完成**（1 个） | `PrimaryButton`：「完成」加一个 11px、60% 不透明的「↵」 · 拷贝到剪贴板 |

行为细节：
- **滑动选中块**：绘制组内放一个绝对定位的 `Border`（圆角 8，`Color.Selected` 渐变，左上 1px `#57FFFFFF` 高光，1px `#40000000` 外线），切换工具时用 `Motion.SlideTo` 移到目标按钮。首次显示时不播放动画。
- **撤销 / 重做**：绑定 `CaptureSurface.CanUndo` / `CanRedo`，不可用时 `IsEnabled=false`（不透明度 0.3）。需要在 `AnnotationContentChanged` 时刷新状态。
- **取消按钮不再是红色**，和其他按钮一样。
- 提取文字 / 翻译的卡片打开时，对应按钮显示 `IsActive`（蓝色）。
- 语种菜单使用 `GlassMenuStyle` 的 `DarkSolid` 变体（它常常浮在白色网页上）。标题「翻译成」，当前语种右侧显示蓝色对勾，选中后立即翻译。菜单向上弹出。
- 提示文字全部缩短：去掉「· 按住 Ctrl 选择已有标注」，但 Ctrl 临时选择的**行为保留**。
- 工具栏出现时整体播放 `PopIn`，样式面板延迟 60ms 再出现。
- 屏幕过窄时按组换行，组内不拆开（沿用现有「从不收起」的原则）。

### 4.5 样式面板（功能不减，只换样式）

使用 `GlassPanel`（圆角 11，内边距 4×6）。定位沿用现有逻辑：水平居中对准当前工具，优先放在工具栏上方。切换工具时重新播放 `PopIn`。

| 工具 | 内容 |
| --- | --- |
| 选择 | 不显示面板 |
| 矩形 / 椭圆 / 直线 / 箭头 / 画笔 | 3 档粗细（每档用当前颜色画一条 16px 宽、1.5/3/5px 高的圆头线作预览）│ 颜色 |
| 文字 | 保留现有的字号档位和文字样式选项，字号档位用 11/14/18px 的「A」表示 │ 颜色 |
| 序号 | 当前颜色的序号预览圆（显示下一个数字）＋「下一个」小字 │ 颜色 |
| 马赛克 | `SegmentedControl`[像素 │ 模糊]（用两张 20×14 的小预览图）│ `SegmentedControl`[涂抹 │ 矩形] │ 3 档大小 │ 强度滑杆（保留，改成细轨道样式） |

颜色：保留现有 7 色（红、黄、绿、蓝、黑、灰、白），改成直径 20 的圆点，带左上高光和 0.5px 暗边。选中项外面加一圈间隔 4px、宽 2px 的白环，并播放 `pop` 动画。

### 4.6 文字卡片与翻译卡片（保持两个入口、两张卡片）

两张卡片都换成 `GlassPanel`（圆角 14，内边距 10×12），并统一结构：

```
┌ 头部：标题或分段控件 ……………………………… [×] ┐   × 是直径 24 的圆形按钮，10% 白底
│ 正文：13–14px，行高 1.6                          │
└ 底部（上方 1px 分隔线）：次要信息 ………… [操作] ┘
```

- **文字卡片**：头部为「提取文字」。正文可编辑（保留现有的编辑模式）。底部左侧显示「英语 · 本机识别」（语言 + 识别来源），右侧是「编辑」和「拷贝」文字按钮（蓝色 `#6CB4FF`，悬停时 18% 蓝色底）。缺语言包时的提示与操作保留。
- **翻译卡片**：头部用 `SegmentedControl`[原文 │ 译文]，切到原文显示 OCR 文本。保留语种下拉，放在底部左侧，显示为「英语 → 简体中文 ⌄」。右侧依次是「重新翻译」（图标按钮）和「拷贝」。来源服务名不再用「✦」徽标，改为底部小字，例如「AI 翻译 · 1.3 秒」。
- **识别时的反馈（新增）**：OCR 完成后，用 `OcrDocument.Lines[i].Bounds` 在选区里给每一行画高亮：圆角 5，9% 蓝色底，1px 38% 蓝色描边，外加 12px 18% 蓝色外发光。各行间隔 90–110ms 依次 `PopIn`。同时从上到下扫过一道蓝色渐变光带（70px 高，750ms）。卡片关闭时高亮一起消失。
- **翻译中的反馈（新增）**：卡片边缘显示一圈 2px 的彩色渐变描边，颜色为 `#FF6AC1 → #FFB86B → #FFE36B → #5BD2FF → #A77BFF`，通过旋转 `LinearGradientBrush` 的 `RelativeTransform` 实现流动（2.2s 一圈），外加柔和的紫蓝色外发光。正文在新旧内容之间淡出淡入（可选再加 0→7px 的 `BlurEffect`）。翻译完成后彩色描边淡出。
- 卡片出现：从左侧 12px、缩放 0.92 处弹入（`PopIn` 的横向版本）。

### 4.7 完成后的反馈

- **完成（拷贝）**：浮层关闭后，在当前显示器工作区的右下角（距边 18px）滑入 `CaptureThumbnailWindow`：
  - `GlassPanel` Solid 变体，圆角 12，宽 188。
  - 内含截图缩略图（宽 174，圆角 7）和一行「✓ 已拷贝到剪贴板」。
  - 停留 2.6 秒后滑出；鼠标悬停时暂停计时；单击后关闭。
  - 窗口不抢焦点：使用 `WS_EX_NOACTIVATE`、`ShowActivated=false`。
- **存储 / 贴图 / 拷贝文字**：改造 `HintToastWindow`，在屏幕中央显示 156×156 的提示框（`GlassPanel` Solid，圆角 28）：
  - 上方 50px 图标，下方 13px SemiBold 文字，例如「已存储到「图片」」「已贴到桌面」「已拷贝」。
  - 对勾使用描边动画。
  - 停留 1.3 秒。
  - 原有的文字提示用法（例如快捷键冲突）保留为现在的条状样式，只换配色。

---

## 5. 录屏

### 5.1 录制前设置条（替换 `BuildRecordingPanel`）

选区下方居中放一行 3 个 `GlassPanel`（圆角 12，间距 8）：

| 分组 | 内容 |
| --- | --- |
| 格式 | `SegmentedControl`：[`Video` 视频 │ GIF 图标 GIF]，默认值取设置中的 `Recording.Format` |
| 内容 | 3 个开关按钮：系统声音（`Volume2` / `VolumeX`）、麦克风（`Mic` / `MicOff`）、鼠标（自绘的点击光标或 `MousePointer2`）。打开时 14% 白底加 22% 高光、图标为白色；关闭时图标为 50% 白色 |
| 录制 | `RecordButtonStyle`：白色圆点加「录制」 |

- **选择 GIF 时，系统声音和麦克风两个按钮禁用**（不透明度 0.35），提示文字为「GIF 不含声音」。只影响本次录制，不改设置中的默认值。
- **删除**「摄像头 · 即将推出」占位，以及原来的 MP4 / GIF 标签行。
- 回车键仍然等同于点击「录制」。

### 5.2 倒计时（`RecordingCountdownWindow`）

- 选区中心显示直径 128 的圆形 `GlassPanel` Solid。
- 内部一圈 3px 圆环：底环为 15% 白，进度环为白色，每拍用 700ms 走满一圈。
- 中间数字 56px，字体为 Display SemiBold，每拍从 1.5 倍缩小并淡入。
- 保留现有的倒计时时长与取消逻辑。

### 5.3 录制中（`RecordingControlWindow` + `RecordingBorderWindow`）

- 控制条：一个 `GlassPanel` Solid（圆角 12），放在选区上方居中，空间不够时放到选区下方。内容依次为：
  - 呼吸红点：直径 9，径向渐变加外发光，1.6s 循环。
  - 计时：13px Bold，等宽数字。
  - 格式徽标：「MP4」或「GIF」，圆角 5，14% 白底。
  - 电平条：16 根，宽 2.5，间距 2，白色。复用现有的 `levelProvider`；GIF 时不显示。
  - 暂停按钮。
  - 停止按钮：70% 红底，白色方块图标。
  - 保留现有的状态文字（保存中、恢复中等）和取消保存功能。
- 边框窗：1.5px `#FF453A` 实线，外加 1.8s 循环的柔和红色外发光（外发光可选，性能不好时去掉）。

### 5.4 录制结束

- 保持现有流程：打开 `RecordingPreviewWindow`。可选在右下角显示一张缩略图提示（复用 `CaptureThumbnailWindow`），带播放图标、格式和时长。
- 预览窗按 token 换色，按钮改用新样式：播放、裁剪、拷贝、存储为主操作，倍速和音量收进一个 `⋯` 菜单（功能保留）。

### 5.5 预览窗中的 GIF

- 当录制格式为 GIF 时：「存储」导出 `.gif`（应用当前的裁剪范围），「拷贝」把 GIF 文件以 `FileDropList` 放进剪贴板。
- 当格式为视频时，行为和现在一样。

### 5.6 数据模型（`Screen.Core/Models.cs`）

```csharp
public enum RecordingFormat { Mp4, Gif }

public sealed record RecordingProfile
{
    // ……现有字段保持不变……
    public RecordingFormat Format { get; init; } = RecordingFormat.Mp4;
    public int GifFrameRate { get; init; } = 15;       // 10–30
    public int GifMaxWidth { get; init; } = 1280;      // 0 = 不缩放
}
```

- 新字段都有默认值，不需要提升 `SchemaVersion`。需要在 `SettingsPersistenceTests` 里补测试：旧配置能正常读取；新字段能保存并读回。
- GIF 录制时，`RecordingController` 本次会话强制 `RecordSystemAudio = false`、`RecordMicrophone = false`。

### 5.7 GIF 编码（新增能力）

1. **重新编译 FFmpeg**（`tools/ffmpeg/build-ffmpeg.sh`），在现有配置上增加：
   ```
   --enable-encoder=gif --enable-muxer=gif --enable-filter=fps,split,palettegen,paletteuse
   ```
   这些都是 FFmpeg 内置组件，不引入外部库。同时更新 `tools/installer/third-party-notices/FFMPEG-BUILD-AND-SOURCE.txt` 和 SHA256 记录。新的 `ffmpeg.exe` 通过 Git LFS 提交。
2. 录制流程不变：仍然先录成无声 MP4。结束时（或在预览窗导出时）转换为 GIF：
   ```
   ffmpeg -y [-ss 开始 -to 结束] -i in.mp4
     -vf "fps={GifFrameRate},scale='min(iw,{GifMaxWidth})':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle"
     -loop 0 out.gif
   ```
3. 新增一个可单测的方法 `RecordingPreviewWindow.BuildGifStartInfo(ffmpeg, source, target, range, fps, maxWidth)`，写法参照现有的 `BuildTrimStartInfo`；导出时显示进度并支持取消，和现有导出一致。
4. 如果 FFmpeg 不支持 GIF（进程返回错误且输出里包含 `Unknown encoder 'gif'` 或找不到相关滤镜），提示「当前 FFmpeg 不支持 GIF，请更新 Screen」，并保留 MP4。
5. 测试：补 `BuildGifStartInfo` 的参数测试；在 `RecordingPipelineIntegrationTests` 中补一个 2 秒小视频转 GIF 的集成测试（FFmpeg 不支持时跳过）。

---

## 6. 设置窗口

### 6.1 窗口

- Windows 11 22H2（build 22621）及以上：
  - `WindowChrome`（`GlassFrameThickness = -1`）。
  - `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE = 20, 1)`。
  - `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE = 38, 2 /* Mica */)`。
  - 窗口 `Background = Transparent`，右侧内容区加一层 16% 黑。
- 更早的系统回退为纯色 `#1E1E20`，侧栏为 `#242426`。
- 窗口尺寸改为 920×620，最小 820×560。

### 6.2 结构：5 页合并为 3 页

左侧栏宽 216，从上到下依次为：
- 资料卡：App 图标 40px，「Screen」13 Bold，下方「版本 1.0.16」11px 次要文字。
- 3 个导航项：高 32，圆角 7，左侧是 24px 的彩色方形图标。选中时显示 18%→10% 白色渐变底和左上高光。

右侧：顶部 48px 标题栏（页面名 13 SemiBold）；内容区宽 576、居中、可滚动。每页第一块是「页头分组」：44px 彩色图标 + 15 Bold 标题 + 12px 一句话说明，右侧可放状态标签。

### 6.3 设置项映射（每一项都要保留）

**通用**（图标：灰色 `Settings2`；说明：启动方式、快捷键和存储位置）

| 位置 | 设置项 | 对应字段 | 控件 |
| --- | --- | --- | --- |
| 分组 1 | 开机时启动 | `StartWithWindows` | 开关 |
| 分组 1 | 存储位置（**新增 UI**，字段已存在） | `SaveDirectory` | 弹出按钮「📁 图片 › Screenshots」，点击打开文件夹选择器 |
| 快捷键 | 截图（蓝色图标） | `HotKey` | 键帽，点击后进入「按下新的快捷键…」等待态（蓝色呼吸描边），保留现有的录制与冲突校验逻辑 |
| 快捷键 | 录屏（红色图标） | `RecordHotKey` | 同上（**从录屏页移到这里**） |
| 页脚 | 「点按快捷键，然后直接按下新的组合。」 | — | — |
| 高级 | 恢复穿透的贴图 | `ClickThroughRecoveryHotKey` | 键帽 |
| 高级 | 快捷键优先交给 Screen | `PreferScreenHotKeys` | 开关（保留原有的切换失败回滚逻辑） |

**录屏**（图标：红色 `Video`；说明：录下屏幕的一块区域，保存成视频或 GIF）

| 位置 | 设置项 | 对应字段 | 控件 |
| --- | --- | --- | --- |
| 分组 1 | 默认格式（紫色图标） | `Recording.Format` | 分段[视频 │ GIF] |
| 分组 1 | 画质（靛蓝图标） | `FrameRate` + `Quality` | 分段[标准 │ 高清]。标准 = 30 帧 + `Standard`，高清 = 60 帧 + `High`。**如果当前值不属于这两种组合，显示第三段「自定义」（选中且只读），不覆盖用户的值** |
| 页脚 | 根据格式和画质显示一句说明，例如「30 帧 · 8 Mbps，适合教程和日常分享。」；GIF 时显示「GIF 适合短小的动图演示，不含声音。」 | — | — |
| 录制内容 | 系统声音（粉色图标） | `RecordSystemAudio` | 开关 |
| 录制内容 | 麦克风（橙色图标） | `RecordMicrophone` | 开关 |
| 录制内容 | 录制鼠标光标 | `ShowCursor` | 开关 |
| 录制内容 | 鼠标点击效果（青色图标） | `ShowClickEffects` | 开关 |
| 高级 | 帧率 | `FrameRate` | 弹出菜单 15 / 30 / 60 |
| 高级 | 码率 | `Quality` | 弹出菜单 4 / 8 / 12 Mbps |
| 高级 | GIF 帧率 | `GifFrameRate` | 弹出菜单 10 / 15 / 20 / 30 |
| 高级 | GIF 最大宽度 | `GifMaxWidth` | 弹出菜单 原始 / 1920 / 1280 / 960 |
| 高级 | 临时文件夹 | `TempDirectory` | 弹出按钮加「选择…」；保留空间和权限检查，以及原来那条恢复素材提示（改成页脚小字） |

**文字与翻译**（图标：靛蓝 `Languages`；说明：截图里的文字，一键提取，随手翻译；页头右侧显示连接状态：「未测试」或绿色「已连接」）

| 位置 | 设置项 | 对应字段 | 控件 |
| --- | --- | --- | --- |
| 分组 1 | 翻译服务 | `Translation.Provider` | 分段[AI 翻译 │ 腾讯 │ 免费] → OpenAiCompatible / Tencent / MyMemory |
| 分组 1（选 AI 时） | 接口地址 / 模型 / API Key | `BaseAddress` / `Model` / 凭据 | 右对齐的无边框输入框，获得焦点时左对齐并显示蓝色焦点环；API Key 草稿与当前地址不一致时，行尾显示「绑定到此地址」按钮（保留现有的绑定语义） |
| 分组 1（选腾讯时） | SecretId / SecretKey / 地域 | 凭据 / `TencentRegion` | 输入框 / 输入框 / 弹出菜单 |
| 分组 1（选免费时） | 一行说明「MyMemory 免费服务，无需配置，每天有额度限制。」 | — | — |
| 分组 1 | 连接 | — | 「测试连接」按钮 → 转圈加「正在连接…」→ 成功显示绿色「✓ 0.8 秒」，失败显示红色错误摘要；页头状态同步更新 |
| 页脚 | 「🔒 密钥只保存在这台电脑上。」 | — | — |
| 分组 2 | 翻译成 | `TargetLanguage` | 弹出菜单 |
| 分组 2 | 显示方式 | `TranslationDisplayMode` | 分段[卡片 │ 覆盖原文] |
| 高级 | 文字识别 | `Ocr.Mode` | 弹出菜单：智能（本机优先）/ 云端优先 / 仅本机 |
| 高级 | 截图翻译时增强识别 | `Ocr.UseCloudForTranslation` | 开关 |
| 高级 | 原文语言 | `SourceLanguage` | 弹出菜单 |
| 高级 | 腾讯云密钥（用于增强识别和回退） | 腾讯凭据 + 地域 | 服务不是腾讯时也要能配置：显示一行「已配置 / 未配置」和「设置…」，点开后展开两个输入框和地域 |
| 高级 | 同时请求第二个模型 | `EnableRacing` | 开关；打开后在下方展开 地址 / 模型 / API Key 三行 |
| 高级 | AI 超时后改用腾讯 | `EnableFastFallbackRace` + `FastFallbackDelaySeconds` | 开关加秒数（2–30）输入 |
| 高级 | 失败时改用免费服务 | `EnableMyMemoryFallback` | 开关 |
| 高级 | MyMemory 联系邮箱 | `MyMemoryContactEmail` | 输入框，占位文字「可选」 |
| 高级页脚 | 「AI 翻译失败时，如已配置腾讯密钥会先尝试腾讯；直接选择免费服务时原文始终发送给 MyMemory。」 | — | — |

- 「高级」是一个可折叠分组：标题行带右箭头，展开时箭头旋转 90°，内容高度有过渡动画（WPF 中用 `ScaleTransform.ScaleY` 加 `Opacity` 近似即可）。
- 文案原则：只保留标题，删掉逐行说明；必要的说明放在分组下方的页脚（11.5px、50% 白）。删除所有 WASAPI、settings.json、凭据管理器之类的实现术语。
- 保留：按字段保存、无效分组不影响其他设置、关闭窗口时处理未保存内容（这些都在 ViewModel 中，不要改）。

### 6.4 图标配色（`IconTile`）

| 用途 | 渐变（160°） | 图标 |
| --- | --- | --- |
| 通用 | `#B0B0B6 → #6E6E73` | `Settings2` |
| 录屏 | `#FF7A70 → #E0352B` | `Video` |
| 文字与翻译 | `#9D8BFF → #5B48E0` | `Languages` |
| 截图快捷键 | `#5AAEFF → #0A6FE0` | `ScanText` |
| 默认格式 | `#D98BFF → #9B3FE0` | GIF |
| 画质 | `#9D8BFF → #5B48E0` | HD |
| 系统声音 | `#FF7FB0 → #E0356F` | `Volume2` |
| 麦克风 | `#FFC05A → #F07D00` | `Mic` |
| 点击效果 / 光标 | `#6FE8DC → #14A99A` | `MousePointerClick` |

---

## 7. 其他窗口（第 1 阶段换色后做轻量调整）

- **贴图窗口菜单**（`PinWindowManager`）：改用 `GlassMenuStyle`，菜单项圆角 6，悬停为蓝色；「浅色 / 深色 / 蓝色描边」选项不变。
- **托盘菜单**（`GraphiteToolStripRenderer`）：背景 `#F21C1C20`，圆角 8，悬停为 `#0A84FF`，分隔线 `#1AFFFFFF`，文字 `#F5F5F7`。
- **长截图编辑器、滚动截图控制条、文字输入框**：工具栏和按钮改用新的 Hud 样式；窗口底色改为 `#1E1E20`。
- **原位翻译状态条**：改为 `GlassPanel`（圆角 12）。

---

## 8. 文案对照（节选，其余按同样原则处理）

| 现在 | 改后 |
| --- | --- |
| 选择和调整标注 · 绘制时可按住 Ctrl 选择 | 选择 |
| 矩形 · 按住 Ctrl 选择已有标注 | 矩形 |
| 另存为 PNG · Ctrl+S | 存储　Ctrl+S |
| 完成并复制 · Enter | 拷贝到剪贴板 |
| 取消截图 · Esc | 取消　Esc |
| 按当前语种翻译 / 选择翻译语种 | 翻译 / 翻译成… |
| 采集扬声器正在播放的声音（WASAPI 环回） | （删除，只留标题「系统声音」） |
| 腾讯云访问密钥，不会写入 settings.json | （删除，页脚统一写「密钥只保存在这台电脑上。」） |
| 两个模型同时翻译，自动采用最先返回的结果 | 同时请求第二个模型 |
| ✦ AI 自然翻译 | AI 翻译 · 1.3 秒（底部小字） |
| 已开启：录制系统声音 / 已关闭：不录制系统声音 | 录制系统声音 / 不录系统声音 |

---

## 9. 实施阶段

| 阶段 | 内容 | 完成标准 |
| --- | --- | --- |
| **P0 准备** | 新建分支 `ui/v4-glass`；跑一遍 `./verify.ps1` 记录基线；截取现有各界面的截图存到 `docs/design/before/` | 基线全绿 |
| **P1 Token 重置** | 按 2.1–2.3 重写 `Tokens.xaml`（新 token + 旧 key 别名）；全局 ToolTip、ScrollBar、ContextMenu 换色；README 第 5 行的主题描述更新为「中性深色玻璃主题」 | 构建与测试通过；整个 App 变成中性灰加蓝色，布局不变 |
| **P2 组件库** | `Controls.xaml`、`GlassPanel`、`SegmentedControl`、`ToggleSwitch`、`Keycap`、`IconTile`、`Motion`、`GlassBackdropProvider`（第 3 节、4.1） | 给 `SegmentedControl`、`GlassBackdropProvider`（模糊与增色结果的尺寸和数值）加单元测试 |
| **P3 截图浮层** | 4.2–4.5：删除模式栏、提示条、放大镜、选区、4 组工具栏、样式面板 | 21 个控件全部可用；快捷键行为不变；撤销/重做禁用态正确；100% / 150% / 200% DPI 下布局正确 |
| **P4 卡片与反馈** | 4.6–4.7：两张卡片、文字行高亮、翻译光环、提示框、缩略图窗 | OCR 和翻译的现有功能（编辑、语种、重新翻译、拷贝、原位模式）全部可用 |
| **P5 录屏** | 5.1–5.4：设置条、倒计时、控制条、边框、预览窗换样式 | 单屏与跨屏录制、暂停、音频提示、保存失败恢复流程不变 |
| **P6 GIF** | 5.5–5.7：模型字段、FFmpeg 重编、转换与导出、测试 | 可以录出 GIF 并导出 / 拷贝；FFmpeg 不支持时有明确提示 |
| **P7 设置窗口** | 第 6 节 | 6.3 表里的每一项都能修改并持久化；旧配置文件正常读取；「自定义」画质不被覆盖 |
| **P8 其他窗口** | 第 7 节 | 贴图菜单、托盘菜单、长截图编辑器视觉统一 |
| **P9 清理** | 删除不再使用的旧 token（保留测试依赖的 7 个）和样式，删除 `BuildModeBar` 等死代码；更新 README 的功能描述和截图 | `./verify.ps1` 全绿，`dotnet format` 无改动 |

---

## 10. 验收清单

**视觉**（与 `docs/design/ui-preview-v4.html` 对照）
- [ ] 除完成和录制按钮外，没有其他实色按钮；没有渐变底的大面板。
- [ ] 圆角只用 5 / 6 / 8 / 10 / 11 / 12 / 14 这几档，没有胶囊形。
- [ ] 浮层上的工具栏能透出背后截图的模糊颜色，边缘有左上亮边和外圈暗线。
- [ ] 截图浮层 21 个控件分 4 组，切换工具时选中块平滑滑动。
- [ ] 文字较多的面板（卡片、菜单）放在白色背景上依然清晰可读。

**功能**
- [ ] 截图：所有工具、Ctrl 临时选择、撤销 / 重做、存储、贴图、长截图、录制此区域、OCR、翻译（卡片和原位两种模式）都可用。
- [ ] 录屏：视频和 GIF 都能录制；选 GIF 时声音开关禁用；倒计时、暂停、停止、保存失败恢复都正常。
- [ ] 设置：6.3 表中每一项都能修改、保存，重启后生效。
- [ ] 快捷键：修改、冲突提示、优先模式切换失败回滚都正常。

**性能与兼容**
- [ ] 4K 屏幕上，浮层从按下快捷键到出现的耗时比 P0 基线多不到 30ms（模糊在后台线程计算）。
- [ ] 100% / 125% / 150% / 200% DPI、多显示器、旋转屏下布局正确。
- [ ] Windows 10 21H2：设置窗口回退为纯色，其他功能正常。
- [ ] 截图浮层打开和关闭各 20 次后，内存没有持续增长（模糊图在关闭时释放）。

---

## 11. 风险与对策

| 风险 | 对策 |
| --- | --- |
| `DropShadowEffect` 拖慢性能或让文字变糊 | 只加在单独的背景层上（见 3.1）；录屏时的控制条可以去掉阴影 |
| 在 WPF 里画 0.5px 发丝线不稳定 | 统一用 1px 加半透明颜色代替 |
| 无法重新编译 FFmpeg（缺 MSYS2 环境） | 备选方案：用 WPF `GifBitmapEncoder` 加手动写入 NETSCAPE2.0 循环块和帧延时；画质较差，只作为临时方案，并在 PR 中说明 |
| 测试代码依赖旧的 brush key | 保留这 7 个 key 作为别名（见 0.7） |
| Mica 在部分 Windows 11 机器上不生效 | 检测 `DwmSetWindowAttribute` 的返回值，失败时回退纯色 |
| 删除模式栏后，「框选后直接 OCR / 翻译」的入口消失 | 有意为之：先框选，再在工具栏里选择；属于预期变化，需要在 README 中说明 |

---

## 12. 交付物

1. 按阶段拆分的提交（或 PR），每个提交说明对应本文的哪一节。
2. `docs/design/after/`：改版后各界面的截图，对应 P0 的「before」截图。
3. README 更新：主题描述、GIF 能力、去掉模式栏后的使用方式。
4. 实机验收时需要人工确认的项目清单（显卡、HDR、音频设备、Windows 10）。
