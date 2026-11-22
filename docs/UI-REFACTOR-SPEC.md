# Screen UI/UX 全面重构工程实施规范（WPF / .NET 10）

> 本文档是针对 `d:\Project\Screen` 仓库的**完整自包含重构交付规范**，用于直接指导开发者 / Codex 将现有深蓝 “Liquid Sapphire” 界面彻底重构为定版方案：**「浅色磨砂 × 液态玻璃 + Apple G2 连续曲率（Squircle 0.60）+ 纯净 24×24 矢量图标 + 物理弹簧微动效」**。
>
> 如执行环境可访问本地预览原型，可对照交互原型进行视觉对齐：  
> `C:\Users\Administrator\.gemini\antigravity\brain\6fa6257f-98dd-4d39-bd21-aecd5a62fa4b\screen-ui-preview.html`  
> 若无法跨目录访问上述路径，**本文档内含全部颜色值、曲率几何公式、20 枚矢量图标精确路径、弹簧物理参数、布局架构与验收细则**，可直接作为唯一开发基准实施。

---

## 0. 工程约束与红线（必读）

1. **解决方案与项目特性**：
   - 根解决方案：`Screen.slnx`（包含 `src\Screen.App`、`src\Screen.Core`、`tests\Screen.App.Tests`、`tests\Screen.Core.Tests`）。
   - `Screen.App.csproj` 配置：`net10.0-windows`、`UseWPF=true`、`UseWindowsForms=true`、**`ImplicitUsings=disable`**（所有新建 `.cs` 文件必须显式声明 `using`）、`AllowUnsafeBlocks=true`。
2. **仅重构 UI / 交互展示层，严禁破坏既有业务功能与数据持久化**：
   - 必须完整保留：DXGI/GDI 截图捕获、窗口吸附、标注绘制与撤销重做、马赛克合成、本地 PaddleOCR、AI/腾讯/百度 OCR 与翻译（含原位覆盖翻译）、滚动长截图、MP4 录屏及音频采集、热键注册、置顶贴图缩放与鼠标穿透（含恢复穿透热键）、`SettingsViewModel` 全部配置项（`StartWithWindows`、`HotKey`、`ClickThroughRecoveryHotKey`、`PreferScreenHotKeys`、`RecordingFrameRateIndex`、`RecordingQualityIndex`、`RecordingShowCursor`、OCR 与翻译接口配置、API 凭证加密存储等）。
3. **验证门槛**：
   - 每次分阶段改造完成并提交前，必须在仓库根目录运行：
     ```powershell
     .\verify.ps1
     ```
   - `verify.ps1` 包含 4 项强制门禁：`dotnet restore`、`dotnet build -c Release`、`dotnet test -c Release`、`dotnet format Screen.slnx whitespace --verify-no-changes`。**四项必须全数通过**，不得删减或弱化现有测试断言。

---

## 1. 统一设计令牌（Design System Tokens）

### 1.1 唯一浅色磨砂 × 液态玻璃色板

彻底替换 `src\Screen.App\Themes\Tokens.xaml` 中原有深蓝配色（不再做深/浅双主题切换），所有 WPF 浮窗、卡片、控件统一引用如下色板：

| Token / 用途 | Color / Gradient（WPF ARGB `#AARRGGBB`） | 视觉说明 |
| --- | --- | --- |
| 悬浮玻璃主底（模式条、工具栏、准备栏、轻提示、灵动岛） | 线性垂直渐变 `(0,0)→(0,1)`：`#D9FFFFFF` → `#C4F6F8FC` | 通透浅白磨砂，兼具深/浅壁纸对比度 |
| 浮起大卡片底（OCR 卡片、翻译卡片、裁切条） | 线性垂直渐变 `(0,0)→(0,1)`：`#EBFFFFFF` → `#D9F8FAFD` | 较高不透明度，确保长文本阅读清晰 |
| 内嵌次级槽位底（分段轨道槽、滑块轨道槽、编辑底纹） | `#0E0F172A`（`rgba(15,23,42,0.055)`） | 无硬边框，微暗沉槽区分层级 |
| 液态弹簧滑块本体 | 线性垂直渐变 `(0,0)→(0,1)`：`#FAFFFFFF` → `#E8F7F9FC` | 浮于槽内，带轻柔外投影 |
| 玻璃镜面折光双描边（Rim） | 内高光描边：`#F5FFFFFF (0%) → #73FFFFFF (45%) → #2EFFFFFF (100%)`；外细线描边：`#210F172A` | 上檐微亮、侧底渐隐，贴合连续曲率轮廓 |
| 主文本 / 次级文本 / 弱提示文本 | `#FF1D1D1F` / `#9E1D1D1F` / `#6B1D1D1F` | 苹果深炭灰字色体系 |
| 主强调色（系统蓝） | `#FF007AFF`；主按钮垂直渐变 `#FF1888FF → #FF007AFF`；柔和激活底 `#1C007AFF` | 用于主完成按钮、激活态图标/标签、开关开启底 |
| 危险 / 录制红 | `#FFFF3B30`；停止录制浅底 `#24FF3B30`；边框 `#57FF3B30` | 取消、录屏指示点、开始/结束录制 |
| 成功绿 / 暂停琥珀黄 | `#FF28CD41` / `#FFFF9F0A` | 完成勾选、就绪圆点、录屏暂停指示 |
| 选区外部暗化遮罩 | `#570F172A`（`rgba(15,23,42,0.34)`） | 轻透不过分压暗桌面 |
| 悬停底 / 极细分割线 | `#0E0F172A` / `#1A0F172A` | 所有分隔线均使用透明微灰细线 |
| 设置窗三层材质（无硬框） | 外底板 `#FFF4F6F9 → #FFEEF1F5`；右侧浮板 `#FFFAFBFD`；分组卡片 `#FFFFFFFF` | 极简纯白/冷灰层级，依靠柔和多层投影区分 |

### 1.2 全局 Apple G2 连续曲率（Squircle）阶梯表

**核心准则**：全局连续曲率平滑系数固定为 **`smoothing = 0.60`**；所有嵌套元素严格遵守同心几何规则 **`内圆角 = 外圆角 - 内边距`**：

| 层级 | 应用对象 | 曲率半径 (`r`) | 内边距 / 同心子元素曲率 |
| --- | --- | --- | --- |
| **L1 大容器** | 设置窗口外底框、OCR/翻译浮动卡片、录后裁切预览卡片 | **`13`** | 设置内嵌浮起白板 **`11`**；设置分组白卡 **`9.5`**；OCR 正文编辑框 **`8`** |
| **L2 悬浮条** | 顶部模式条、主工具栏、二级样式条、录屏准备条、录屏灵动岛、原位翻译状态条、托盘右键菜单 | **`10.5`** | 内边距 **`4`** → 内部弹簧滑块、完成主按钮、开始录制按钮、菜单高亮条 **`6.5`** |
| **L2.5 贴图** | 桌面贴图（`PinWindow`）裁切表面 & 悬停光学描边 | **`10`** | 描边路径与裁切路径共用同一 `SquircleGeometry` |
| **L3 内部控件** | 侧边栏导航项与纯白悬浮滑块 | **`8.5`** | 导航项高度固定 `40` |
| **L3.5 次级轨道** | 文本字号/字重、马赛克像素/模糊、`MP4 / GIF` 分段轨道槽 | **`6.5`** | 内边距 **`2`** → 内部弹簧滑块 **`4.5`**（粗细滑块用 **`6`**） |
| **L4 轻量反馈** | 底部完成轻提示（`HintToastWindow`） | **`8`** | 内含 SVG/Path 对勾绘制动画 |
| **L5 迷你标签** | 选区左上角尺寸铭牌、放大镜底部色值坐标条 | **`7.5`** | — |

### 1.3 排版与等宽数字规范
- 主字体：`Segoe UI Variable Text, Microsoft YaHei UI`。
- 凡涉及尺寸（`620 × 344`）、坐标（`px, py`）、HEX 色值、录屏计时器（`00:12`）、裁切时间范围、倒计时与缩放百分比（`100%`），**必须设置 `Typography.NumeralAlignment="Tabular"`** 并搭配 `"Cascadia Mono", Consolas`，避免数字跳动。

---

## 2. 阶段一：核心 UI 基础组件（`src\Screen.App\UI\`）

建议在 `src\Screen.App\UI\` 下新增以下 3 个基础引擎，并更新资源词典：

### 2.1 `SquircleGeometry.cs` & `SquircleGlassBorder.cs`
严禁在主要浮窗和卡片上直接用普通的 WPF `Border.CornerRadius` 替代（WPF 默认圆角为普通四分之一圆弧，直线接圆弧会产生明显折肩感）。必须实现精确的 Figma/Apple G2 连续曲率几何：

```csharp
// 精确对应原型 calcCornerParams / buildSquirclePath，默认 smoothing = 0.60
internal static class SquircleGeometry
{
    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    public static StreamGeometry Create(double width, double height, double radius, double smoothing = 0.60)
    {
        var geometry = new StreamGeometry();
        if (width <= 0 || height <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        double maxExtent = Math.Min(width, height) / 2.0;
        double r = Math.Min(radius, maxExtent);
        using (var ctx = geometry.Open())
        {
            if (r <= 0.5)
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(width, 0), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(width, height), true, false);
                ctx.LineTo(new Point(0, height), true, false);
                return geometry;
            }

            double s = smoothing;
            double p = (1.0 + s) * r;
            if (p > maxExtent)
            {
                s = Math.Max(0.0, maxExtent / r - 1.0);
                p = maxExtent;
            }

            double arcMeasure = 90.0 * (1.0 - s);
            double arcSectionLength = Math.Sin(ToRad(arcMeasure / 2.0)) * r * Math.Sqrt(2.0);
            double angleAlpha = (90.0 - arcMeasure) / 2.0;
            double p3ToP4 = r * Math.Tan(ToRad(angleAlpha / 2.0));
            double angleBeta = 45.0 * s;
            double c = p3ToP4 * Math.Cos(ToRad(angleBeta));
            double d = c * Math.Tan(ToRad(angleBeta));
            double b = (p - arcSectionLength - c - d) / 3.0;
            double a = 2.0 * b;
            var arcSize = new Size(r, r);

            // 顺时针构建四个 G2 连续曲率拐角：顶边 → 右上角 → 右边 → 右下角 → 底边 → 左下角 → 左边 → 左上角
            ctx.BeginFigure(new Point(p, 0), isFilled: true, isClosed: true);

            // 右上角
            ctx.LineTo(new Point(width - p, 0), true, false);
            ctx.BezierTo(
                new Point(width - (p - a), 0),
                new Point(width - (p - a - b), 0),
                new Point(width - (p - a - b - c), d),
                true, true);
            ctx.ArcTo(new Point(width - d, p - a - b - c), arcSize, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(
                new Point(width, p - a - b),
                new Point(width, p - a),
                new Point(width, p),
                true, true);

            // 右下角
            ctx.LineTo(new Point(width, height - p), true, false);
            ctx.BezierTo(
                new Point(width, height - (p - a)),
                new Point(width, height - (p - a - b)),
                new Point(width - d, height - (p - a - b - c)),
                true, true);
            ctx.ArcTo(new Point(width - (p - a - b - c), height - d), arcSize, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(
                new Point(width - (p - a - b), height),
                new Point(width - (p - a), height),
                new Point(width - p, height),
                true, true);

            // 左下角
            ctx.LineTo(new Point(p, height), true, false);
            ctx.BezierTo(
                new Point(p - a, height),
                new Point(p - a - b, height),
                new Point(p - a - b - c, height - d),
                true, true);
            ctx.ArcTo(new Point(d, height - (p - a - b - c)), arcSize, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(
                new Point(0, height - (p - a - b)),
                new Point(0, height - (p - a)),
                new Point(0, height - p),
                true, true);

            // 左上角
            ctx.LineTo(new Point(0, p), true, false);
            ctx.BezierTo(
                new Point(0, p - a),
                new Point(0, p - a - b),
                new Point(d, p - a - b - c),
                true, true);
            ctx.ArcTo(new Point(p - a - b - c, d), arcSize, 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(
                new Point(p - a - b, 0),
                new Point(p - a, 0),
                new Point(p, 0),
                true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
```

**`SquircleGlassBorder` 避坑架构（必须遵守）**：
- 在 WPF 中如果直接对带 `DropShadowEffect` 的节点设置 `Clip`，**投影会被连同裁掉**。
- 正确结构为三层嵌套 `Grid`（对外暴露 `CornerRadiusRadius` / `Smoothing` / `GlassBackground` / `ShowRim` / `ShadowLevel` / `Child` / `Padding` 等属性）：
  1. **投影层（底层，不设 `Clip`）**：一个内部 `Path`（与内层同路径 `Data`，`Fill="#01FFFFFF"` 或 `#FFFFFFFF`（对不透明层）或外层透明阴影投射层）+ `DropShadowEffect`（或在无边框设置卡片采用叠放 2~3 层极柔和半透明 `Path`/`DropShadowEffect` 模拟多层弥散影）。
  2. **裁切与内容层（中层，设 `Clip = squircle`）**：设置磨砂渐变背景 `GlassBackground`，承载 `Padding` 和用户 `Child`。
  3. **光学折光描边层（顶层，`IsHitTestVisible = false`）**：两个叠加 `Path`（均绑定同一冻结 `Data`），内层高光描边 `StrokeThickness = 2.0~2.5` 使用垂直渐隐高光画刷，外层细边 `StrokeThickness = 1.0~1.2` 使用 `#210F172A`（`ShowRim = false` 时隐藏此层，用于设置卡片及纯实心小徽章）。

### 2.2 `AppleVectorIcons.cs`（20 枚纯净 24×24 矢量图标全量路径）

淘汰原有混杂透明色块的 `ToolbarIcons` 与线条粗细不一的混用调用，统一基于 `24×24` 画布、`Viewbox`（`Stretch="Uniform"`）、`PenLineCap.Round`、`PenLineJoin.Round`、`StrokeThickness = 1.75`（仅 `check` 使用 `2.1`），画刷通过绑定继承父按钮 `Foreground`。

以下为**全部 20 枚图标的精确矢量定义（1:1 取自定版原型）**：

| 标识 | 矢量定义（24×24 坐标系） |
| --- | --- |
| `rect` | `<rect x="3.75" y="5.25" width="16.5" height="13.5" rx="2.5" />`（仅 Stroke） |
| `ellipse` | `<circle cx="12" cy="12" r="8.25" />`（仅 Stroke） |
| `arrow` | `M4.5 19.5 L19.5 4.5 M8.25 4.5 H19.5 V15.75` |
| `pen` | `m16.862 4.487 1.687-1.688a1.875 1.875 0 1 1 2.652 2.652L6.832 19.82a4.5 4.5 0 0 1-1.897 1.13l-2.685.8.8-2.685a4.5 4.5 0 0 1 1.13-1.897L16.863 4.487Zm0 0L19.5 7.125` |
| `text` | `M5.5 6.75V5.25A.75.75 0 0 1 6.25 4.5h11.5a.75.75 0 0 1 .75.75v1.5M12 4.5v15m-3.25 0h6.5` |
| `mosaic` | **无脏灰层！** 外框 `<rect x="3.75" y="3.75" width="16.5" height="16.5" rx="3" />`（Stroke）；内部 5 枚实心矩形（纯 Fill，无 Stroke）：`(7,7,3,3,0.5)`、`(14,7,3,3,0.5)`、`(10.5,10.5,3,3,0.5)`、`(7,14,3,3,0.5)`、`(14,14,3,3,0.5)` |
| `pin` | 围绕 `(12,12)` 旋转 `35°`：`M8.5 4.5h7M9.75 4.5v4.25L7.5 12v1.5h9V12l-2.25-3.25V4.5M12 13.5V20` |
| `ocr` | `M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2 M8 9.25h8M8 12.25h5.5M8 15.25h6.5` |
| `translate` | `m10.5 21 5.25-11.25L21 21m-9-3h7.5M3 5.621a48.474 48.474 0 0 1 6-.371m0 0c1.12 0 2.233.038 3.334.114M9 5.25V3m3.334 2.364C11.176 10.658 7.69 15.08 3 17.502m9.334-12.138c.896.061 1.785.147 2.666.257m-4.589 8.495a18.023 18.023 0 0 1-3.827-5.802` |
| `scroll` | `<rect x="5.25" y="3.5" width="13.5" height="17" rx="2.5" />`（Stroke） + `M9.5 12.5 L12 15 L14.5 12.5 M12 8.5 V15` |
| `video` | `m15.75 10.5 4.72-4.72a.75.75 0 0 1 1.28.53v11.38a.75.75 0 0 1-1.28.53l-4.72-4.72M4.5 18.75h9a2.25 2.25 0 0 0 2.25-2.25v-9a2.25 2.25 0 0 0-2.25-2.25h-9A2.25 2.25 0 0 0 2.25 7.5v9a2.25 2.25 0 0 0 2.25 2.25Z` |
| `undo` | `M9 15 3 9m0 0 6-6M3 9h12a6 6 0 0 1 0 12h-3` |
| `save` | `M3 16.5v2.25A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V16.5M16.5 12 12 16.5m0 0L7.5 12m4.5 4.5V3` |
| `close` | `M6 18 18 6M6 6l12 12` |
| `check` | `m4.5 12.75 6 6 9-13.5`（`StrokeThickness = 2.1`） |
| `speaker` | `M19.114 5.636a9 9 0 0 1 0 12.728M16.463 8.288a5.25 5.25 0 0 1 0 7.424M6.75 8.25l4.72-4.72a.75.75 0 0 1 1.28.53v15.88a.75.75 0 0 1-1.28.53l-4.72-4.72H4.51c-.88 0-1.704-.507-1.938-1.354A9.009 9.009 0 0 1 2.25 12c0-.83.112-1.633.322-2.396C2.806 8.756 3.63 8.25 4.51 8.25H6.75Z` |
| `mic` | `M12 18.75a6 6 0 0 0 6-6v-1.5m-6 7.5a6 6 0 0 1-6-6v-1.5m6 7.5v3.75m-3.75 0h7.5M12 15.75a3 3 0 0 1-3-3V4.5a3 3 0 1 1 6 0v8.25a3 3 0 0 1-3 3Z` |
| `cursor` | `M15.042 21.672 13.684 16.6m0 0-2.51 2.225.569-9.47 5.227 7.917-3.286-.672ZM12 2.25V4.5m5.834.166-1.591 1.591M20.25 10.5H18M7.757 14.743l-1.59 1.59M6 10.5H3.75m4.007-4.243-1.59-1.59` |
| `copy` | `M16.5 8.25V6a2.25 2.25 0 0 0-2.25-2.25H6A2.25 2.25 0 0 0 3.75 6v8.25A2.25 2.25 0 0 0 6 16.5h2.25m8.25-8.25H18a2.25 2.25 0 0 1 2.25 2.25V18A2.25 2.25 0 0 1 18 20.25h-7.5A2.25 2.25 0 0 1 8.25 18v-1.5m8.25-8.25h-6a2.25 2.25 0 0 0-2.25 2.25v6` |
| `settings` | `M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z` + `<circle cx="12" cy="12" r="3" />` |

### 2.3 `LiquidSpringTracker.cs`（双轴物理弹簧滑块引擎）

基于 `CompositionTarget.Rendering`（帧间隔钳制 `dt = Clamp(elapsed, 0.004, 0.032)`）在独立 `Canvas` 或 `Grid` 底层驱动滑块的 `TranslateTransform(x, y)`、`ScaleTransform(sx, sy)`（`RenderTransformOrigin = (0.5, 0.5)`）与 `Width` / `Height`：

1. **几何追踪规则（关键）**：
   - 必须通过 `target.TransformToAncestor(track).Transform(new Point(0, 0))` 与 `target.ActualWidth` / `target.ActualHeight` 获取精确 `(X, Y, W, H)`；
   - **禁止让滑块写死 `VerticalAlignment=Stretch` 忽略 `Y/Height`**，否则带 `Padding` 的轨道（如模式条 `p=4`、分段槽 `p=2`）会出现上下贴边不居中。
   - **ColorRing 模式**：目标点水平中心对齐，固定尺寸 `24×24`，垂直居于颜色点正中。
   - **瞬时落位规则**：当轨道或目标刚从 `Collapsed`/`Hidden` 切换为 `Visible`，或尚未初始化首帧时，必须 `MoveTo(target, instant: true)`，严禁滑块从 `(0, 0)` 飞越屏幕。
2. **双轴物理参数**：
   - **`Axis.X`（横向液态玻璃滑块：顶部模式条、主工具栏绘图区、粗细、字号/字重、马赛克类型、MP4/GIF）**：
     - 弹簧方程：`stiffness = 420.0`, `damping = 27.0` 驱动 `x` 与 `w`，`y`/`h` 以 `0.35` 比例快速跟随；
     - 速度耦合拉伸：`stretch = Math.Min(0.24, Math.Abs(vx) * 0.00028)`，`sx = 1 + stretch`，`sy = 1 - stretch * 0.34`；
     - 收敛阈值：`|targetX - x| < 0.15 && |vx| < 0.8 && |targetW - w| < 0.15` 时锁定精确目标坐标、清零形变并摘除 `Rendering` 订阅。
   - **`Axis.Y`（纵向高阻尼轻柔滑块：设置侧边栏导航、托盘悬停跟随）**：
     - 弹簧方程：`stiffness = 380.0`, `damping = 36.0` 驱动 `y` 与 `h`，`x`/`w` 以 `0.35` 比例跟随；
     - **克制形变上限**：`stretch = Math.Min(0.035, Math.Abs(vy) * 0.00005)`，`sy = 1 + stretch`，`sx = 1 - stretch * 0.25`（几乎无晃动，温润平移）。

### 2.4 全局资源与通用控件重构
- 修改 `src\Screen.App\Themes\Tokens.xaml`、`src\Screen.App\Themes\GraphiteControls.xaml` 与 `src\Screen.App\App.xaml`：
  - 将颜色与画刷替换为 **1.1 浅色磨砂 × 液态玻璃规范**。
  - 更新细滑块（`Slider`）样式：4px `#0E0F172A` 圆角底槽 + `#007AFF` 填充轨 + 14px 纯白圆形带轻影拇指。
  - 更新开关（`ToggleButton` / Apple Switch）样式：40×22px，开启底色 `#007AFF`、关闭底色 `#1F0F172A`（`rgba(15,23,42,0.12)`），18×18px 纯白圆钮平滑移动（`200ms`，Curve `(0.22, 1.04, 0.36, 1)`，无大过冲；按下宽度仅极轻微拉伸至 `19.5px`）。

---

## 3. 阶段二：截图主流程重构

**目标文件**：`CaptureOverlayWindow.Main.cs`、`CaptureOverlayWindow.UI.cs`、`CaptureOverlayWindow.Layout.cs`、`CaptureOverlayWindow.Actions.cs`、`CaptureSurface.Rendering.cs`、`CaptureSurface.Input.cs`、`TextPromptWindow.cs`、`HintToastWindow.cs`

### 3.1 选区 Chrome 与全屏暗化遮罩（`CaptureSurface.Rendering.cs`）
- 选区外暗化遮罩由极黑透明改为通透的 **`#570F172A`**。
- 选区边线：取消粗蓝框，改为 **`1px` 白色内线（`#EBFFFFFF`）+ `1px` 深半透明外线（`#570F172A`）**（在深/白背景均清晰不抢眼）。
- 8 个尺寸控制点：`6×6px` 纯白实心小圆点 + `0.5px` `#590F172A` 细轮廓 + 柔和微小投影。
- 左上角尺寸铭牌：`SquircleGlassBorder(r = 7.5)` + 等宽数字 `W × H`（如 `620 × 344`）。

### 3.2 框选前：顶部模式条 + 圆形光学放大镜
1. **顶部模式条（`BuildModeBar`）**：
   - 在选区确认前顶部居中常驻（框选提交后隐藏），外框 `SquircleGlassBorder(r = 10.5, Padding = 4)`，内含 `截图 / 滚动截图 / 提取文字 / 翻译`，由 `LiquidSpringTracker(Axis.X)` 驱动 `r = 6.5` 白色滑块。
   - 出现时播放顶降入场动画（`TranslateY: -12 → 0`, `Scale: 0.94 → 1`, `Opacity: 0 → 1`, `300ms`）。
2. **圆形光学放大镜（重写 `CaptureSurface.Rendering.cs` 中的 `DrawMagnifier`）**：
   - 废弃原方角卡片、粗蓝大十字条与中间横线，改为**两段分离悬停结构**：
     - **上方 116×116px 圆形光学镜片**：`EllipseGeometry` 裁切放大像素 + 像素网格（缩放倍率 `>= 6×` 时绘制 `1px #21FFFFFF` 细网线）+ 居中单个像素白/黑双线取色方框 + 镜环外框（`2.5px` `#EBFFFFFF` + `1px` `#240F172A` 外细线 + 外阴影 + 内顶部微高光与内底部微暗影）；滚轮切换倍率（`4× / 8× / 12×`）时顶部浮出半透明黑胶囊显示 `8×`，900ms 后自动淡出，圆镜轻弹确认。
     - **下方独立 `7.5px` 连续曲率磨砂色值条**（距圆镜下方 `8px`）：左 `10×10px` 采样色圆点 + 等宽 `#HEX` + `·` + 坐标 `x, y`；按 `Ctrl+C` 复制时变为绿字 `✓ 已复制 #XXXXXX` 轻弹确认。
   - **惯性阻尼跟随**：放大镜视觉坐标使用 `lerp = 0.30` 平滑追随真实鼠标光标（与模式条避让区域 `MagnifierAvoidanceArea` 联动）。

### 3.3 框选后：主工具栏、二级跟随样式条与编排弹出特效
1. **主工具栏（`BuildToolbar`）**：
   - 外框 `SquircleGlassBorder(r = 10.5, Padding = 4)`，所有图标按钮为 `34×34px`（悬停底圆角 `6.5px`），`AppleVectorIcons` 驱动：
     - **绘图区（接入 `LiquidSpringTracker(Axis.X)` + `r = 6.5` 透镜）**：`rect`、`ellipse`、`arrow`、`pen`、`text`、`mosaic`
     - *1px × 16px 发丝竖线*
     - **功能区**：`pin`（贴图）、`ocr`（提取文字）、`translate`（翻译，保留右键快捷切换语言，**合并移除冗余的独立小下拉箭头按钮**）、`scroll`（长截图）、`video`（录屏）
     - *1px × 16px 发丝竖线*
     - **操作区**：`undo`、`save`、`close`（`#FF3B30`）、**完成主按钮**（`#1888FF → #007AFF` 梯度，`Height = 34`, `Squircle r = 6.5`，无 Rim 描边，白色 `check` 图标 +「完成」）
2. **二级样式跟随条（`BuildStylePanel`）**：
   - 默认定位在主工具栏上方 `8px`（顶部不足时下移至工具栏下方 `8px`），外框 `SquircleGlassBorder(r = 10.5, Padding = 4)`。
   - **水平跟随**：在 `PositionFloatingPanels` 中，以平滑平移动画（`260ms CubicEase Out`）对齐到当前激活绘图工具按钮中心，同时钳制在主工具栏左右边界内。
   - **动态分组**（切换分组时新显现组播放 `200ms` `Opacity 0→1, TranslateY 5→0` 柔和淡入）：
     - 绘图类（`rect` / `ellipse` / `arrow` / `pen`）：粗细 3 档圆点（接入 `r = 6` 弹簧滑块）+ 7 色色点圆盘（`#FF3B30`, `#FFCC00`, `#34C759`, `#007AFF`, `#1C1C1E`, `#8E8E93`, `#FFFFFF`，接入 `24×24` 圆形弹簧环）
     - 文字类：字号分段（小/中/大，`r = 6.5` 槽 + `r = 4.5` 弹簧滑块）+ 字重分段（常规/粗体）+ 7 色色盘
     - 马赛克类：类型分段（像素/模糊，`r = 6.5` 槽 + `r = 4.5` 弹簧滑块）+ 细滑块调节强度（隐藏色盘）
3. **编排弹出特效（仅选区刚由无变有弹出时触发）**：
   - 主工具栏整体：`Opacity: 0 → 1`、`TranslateY: -10 → 1.5 → 0`、`Scale: 0.93 → 1.012 → 1`（`320ms`）
   - 栏内各按钮按序号 `i` 依次延迟 `14ms * i` 微浮展开（`Opacity: 0 → 1, TranslateY: 5 → 0, Scale: 0.84 → 1`, `240ms`）
   - 二级样式条延迟 `70ms` 从下方轻盈探出（`TranslateY: 9 → 0, Scale: 0.94 → 1`, `280ms`）
   - 选区 8 个控制点同步 `Scale: 0 → 1.22 → 1` 弹入（不影响原有锚点偏移）
4. **单按钮微动效**：
   - 工具/粗细点击：图标 `Scale 1 → 0.82 → 1.12 → 1`（`280ms`）；
   - 撤销点击：图标逆时针轻旋 `-30°` 回正（`320ms`）；
   - 保存/长截图点击：图标向下微沉 `2.5px` 回位（`260ms`）。

### 3.4 内联文字输入（`TextPromptWindow.cs`）与完成轻提示（`HintToastWindow.cs`）
- **`TextPromptWindow.cs`**：去掉深蓝实心弹窗卡片与粗边框，改用定位在鼠标点击处透明轻量容器（`#29000000` 微透背景 + 细虚线边框，实时渲染当前工具选中颜色、字号与字重），继续无缝配合中文 IME 及 `CaptureKeyboardRouting`。
- **`HintToastWindow.cs`**：点击「完成」后，先在选区矩形区域播放一道白色柔和光学闪光定格（`Opacity: 0 → 0.55 → 0`, `280ms`），退罩后在屏幕底部居中弹出 **`8px` 连续曲率磨砂轻提示**（绿色 `#28CD41` 小圆底 + 白色对勾路径描边动画 `StrokeDashOffset 24 → 0` + `已复制到剪贴板`），约 `2s` 后淡出，完整保留 `WsExTransparent | WsExNoActivate` 与 `WdaExcludeFromCapture`。

---

## 4. 阶段三：OCR 与翻译卡片、原位翻译统一重构

**目标文件**：`OcrCard.cs`、`TranslationCard.cs`、`CaptureOverlayWindow.InPlaceTranslation.cs`

1. **统一卡片骨架（`13px` 连续曲率高透白底）**：
   - 入场动效：`Opacity: 0 → 1, TranslateY: 10 → 0, Scale: 0.96 → 1`（`280ms`）。
   - **顶栏**（`Cursor = SizeAll` 可按住拖动，拖动中卡片整体微微抬升 `Scale = 1.015` 投影略增，松手回落）：
     - `TranslationCard`：源语言 + `→` + 柔和蓝底（`#1C007AFF`）下拉目标语言选择器 + 右侧轻提示「可拖动」+ 关闭图标。
     - `OcrCard`：`提取文字` + 行数徽章（如 `3 行`）+ 关闭图标。
   - **正文区**：无边框透明排布；OCR 点击「编辑」后平滑显示 `#0E0F172A` 凹槽底 + `1px #007AFF` 细边（`r = 8`）；切换目标语言或触发重译后，正文播放 `200ms` 轻淡上浮过渡。
   - **底栏**：左侧绿色状态圆点 + 简短引擎/耗时提示（悬停 Tooltip 显示详情），右侧次按钮（`重译` / `编辑`，悬停浅微灰）+ 系统蓝主按钮（`复制译文` / `复制`，点击文案切换为 `✓ 已复制` 并在 `1s` 后还原）。
2. **原位覆盖翻译悬浮状态条**：
   - `10.5px` 连续曲率磨砂状态条（顶降弹出）：状态绿点 + `原位覆盖完成 / 正在对照原文` + `查看原文 / 查看译文` + `复制译文`。
   - 切换「查看原文 / 查看译文」时，译文覆盖图层执行 `200ms` 平滑 `Opacity (0 ↔ 1)` + 微缩放 `Scale (0.98 ↔ 1.0)` 过渡。

---

## 5. 阶段四：录屏全链路重构

**目标文件**：`CaptureOverlayWindow.UI.cs`（`BuildRecordingPanel`）、`CaptureOverlayWindow.Actions.cs`、`RecordingController.cs`、`RecordingCountdownWindow.cs`、`RecordingControlWindow.cs`、`RecordingBorderWindow.cs`、`RecordingPreviewWindow.cs`、`TimelineBar.cs`

1. **单行录屏准备条 + 启用 `MP4 / GIF` 选择**：
   - 将原本多行厚重的录屏卡片改造成紧凑**单行 `10.5px` 连续曲率磨砂条**（`Padding = 4`，`dockPopIn` 弹出动效），**直接删除原有未实现的灰色「摄像头即将推出」占位按钮**。
   - **正式启用 `MP4 / GIF` 分段弹簧开关**（`r = 6.5` 槽 + `r = 4.5` 白色滑块）：
     - 点击 **`GIF`**：滑块加速滑向 `GIF`，系统声音与麦克风按钮自动柔和淡化并禁用（系统声音文字切换为「无音轨」），右侧录制红按钮文案变为「录制 GIF」，且将默认导出意图传递给录屏结束后的裁切导出流程；
     - 点击 **`MP4`**：恢复声道可用状态，「开始录制」。
   - 三个选项开关按钮（系统声音 / 麦克风 / 点击波纹）：使用平滑透明度与激活浅蓝底切换，点击时图标微弹确认。
2. **倒计时窗（`RecordingCountdownWindow.cs`）**：
   - `88×88px` 浅色磨砂圆形玻璃 + `2.5px` `#007AFF` 进度弧，每次读秒切换数字时叠加一道从圆心向外扩散的蓝色光学涟漪环（`Scale: 0.92 → 1.44, Opacity: 0.55 → 0, 750ms`）。
3. **录屏中灵动岛（`RecordingControlWindow.cs`）**：
   - 重构为浅色 `10.5px` 连续曲率磨砂条（保留 `WDA_EXCLUDEFROMCAPTURE`），包含：
     - 呼吸红点 + 柔光晕（暂停时切换为琥珀黄 `#FF9F0A` 且光晕隐藏）；
     - `MP4` / `GIF` 浅灰圆角小徽章；
     - 等宽计时器 `00:12`；
     - 5 根系统蓝垂直音频律动柱（以 `180ms` 定时器平滑变化高度，GIF 模式自动隐藏，暂停时高度平滑降为 `3px`）；
     - 暂停按钮与淡红磨砂「结束录制」按钮。
4. **录后预览与裁切条（`RecordingPreviewWindow.cs` & `TimelineBar.cs`）**：
   - DWM 关闭暗黑模式（改为浅色），主控区域与裁切时间轴统一为浅色 `#EBFFFFFF` `13px` 卡片风格，活动选区采用系统蓝高亮框与左右可拖拽手柄（拖拽时手柄略加宽提亮）。

---

## 6. 阶段五：设置窗口（按参考图重塑）、桌面贴图、托盘与长截图

**目标文件**：`SettingsWindow.xaml`、`SettingsWindow.xaml.cs`、`SettingsWindow.View.Pages.cs`、`SettingsWindow.View.Controls.cs`、`SettingsWindow.View.Events.cs`、`PinWindowManager.cs`、`AppHost.cs`、`GraphiteToolStripRenderer.cs`、`LongScreenshotEditorWindow.cs`

### 6.1 设置窗口重构核心设计（严禁显挤、严禁强回弹）

> **注意痛点规避**：侧边栏导航项**必须拥有独立的样式类**，绝对不能复用工具栏中小号分段按钮（高度30/字号12），否则文字会再次显得又小又挤！

1. **整体窗体与无框三层空间架构**：
   - 在 `SettingsWindow.xaml.cs` 中将 DWM 设为浅色模式（`dark = 0`），启用无边框透明（或 `WindowChrome` 保留拖拽与系统阴影），建议尺寸调整为 `920 × 620`（`MinWidth="820", MinHeight="540"`）。
   - **彻底去掉顶部标题横线与侧栏竖线硬边框**，改用三层**无框柔和多层阴影**空间：
     - **外底板**：`SquircleGlassBorder(r = 13, ShowRim = false)`，背景渐变 `#FFF4F6F9 → #FFEEF1F5`，四周固定内边距 **`12px`**，左右两栏间距 **`12px`**。
     - **左侧独立呼吸侧边栏（固定宽 `220px`）**：
       - 顶部品牌头：`28×28` 系统蓝连续曲率小图标（`r = 7.5`，柔和蓝底投影）+ `Screen`（`14.5px SemiBold #111827`）+ `设置`（`12px #6B7280`），下方留空 `20px`；
       - 分组导航（「基础偏好」「功能模块」`11px #8A94A6` 轻灰标题，保留与全部 `SettingsViewModel` 分页对应的导航项，建议在视觉上梳理紧凑布局并保证原有全部配置项可访问）；
       - 导航按钮规格：**高 `40px`**，水平内边距 `13px`，圆角 `8.5px`，字号 **`13.5px`**，图标 `17px`，图文间距 **`11px`**，项间纵向间隙 `4px`；常态字色 `#525C6B`，悬停浅底 `#0A0F172A`；激活文字 `#111827 SemiBold` 配系统蓝 `#007AFF` 图标。
       - 选中浮片指示器：在底层由 `LiquidSpringTracker(Axis.Y)` 驱动的**纯白无框浮片（`#FFFFFF`, `r = 8.5`）+ 多层柔和弥散投影**，跨越分组小标题顺畅滑行；
       - 底部极简状态微卡：`#A6FFFFFF` 圆角条，含绿点 `本地引擎就绪` 与版本号。
     - **右侧独立浮起主内容板**：
       - `SquircleGlassBorder(r = 11, ShowRim = false)`，背景 `#FFFAFBFD`，外包多层柔和弥散投影；
       - 顶部轻盈顶栏（可拖动窗口，无底边框，Padding `28,20,28,4`）：左置面包屑 `设置 › 当前页名`（`12px #8A94A6`），右置极简圆形最小化/关闭按钮；
       - 主滚动内容区（Padding `28,8,28,24`）：页面主标题 `20px SemiBold #111827` + 副标题 `12.5px #6B7280`，下方 `20px` 后放置分组卡片列表。
2. **右侧分组纯白设置卡片（无硬边框）**：
   - 同页按语义分多张卡片（组间距 `20px`），组上方放 `11.5px` 轻灰标题（距卡片 `8px`）。
   - 每张卡片使用 `SquircleGlassBorder(r = 9.5, ShowRim = false)`，背景纯白 `#FFFFFFFF`，外加**柔和多层弥散阴影**（`Blur 2 / 6 / 18` 轻浅阴影叠加），零硬边框。
   - 每行高 **`≥ 58px`**（`Padding = 16,14`），行首配 `32×32px` `#F2F5FA` 浅灰蓝微圆角图标托盘（`r = 7.5`，内放 `16px` 图标）；主标题 `13.5px #111827`，描述文字 `12px #8A94A6`；行间仅以左右内缩 `16px` 的 `1px` `#0C0F172A` 发丝线微隔。
   - **快捷键键帽**：纯白无框立体微影（`.soft-keycap`，微白底 + 柔和底部投影 + 顶沿 `1px` 白高光）；
   - **选项/下拉控件**：`#F3F5F8` 柔和胶囊（无边框，带小上下双向指示箭头，悬停渐变为 `#EAEFF5`）。
3. **设置界面的动效与点击反馈原则——必须“轻、柔、淡”**：
   - **禁止**对设置页侧边栏图标、下拉按钮或菜单项施加强弹跳放大（`Scale 1.12` 等）；
   - 侧边栏滑块采用高阻尼近临界曲线（`stiffness = 380, damping = 36, maxStretch = 0.035`），按钮按下仅 `Scale 0.992`；
   - 切换分页时仅右侧分组卡片执行极轻量级上浮淡入（`Opacity: 0.25 → 1, TranslateY: 3 → 0, 180ms EaseOut`，组间差 `32ms`），禁止整页外框二次上跳；
   - 下拉/选项胶囊按压仅微沉 `0.988` + 底色略加深 `#E4EAF2`，切换后仅文字部分轻柔淡入替换（`Opacity: 0.38 → 1, TranslateY: 2 → 0, 170ms EaseOut`）；
   - Apple 开关切换平滑过渡（`200ms`，无大过冲）。

### 6.2 桌面贴图（`PinWindowManager.cs`）
- `PinWindow` 内部图像容器改用 `SquircleGeometry.Create(w, h, 10, 0.60)` 作为 `Clip`，悬停时的 **`1.8px` 系统蓝 `#E6007AFF` 光学描边使用完全相同的 `SquircleGeometry` 路径**，彻底修复原版“贴图直角、悬停蓝框圆角”的割裂问题。
- 静止状态无硬描边，依靠柔和投影在纯白网页与深色背景上清晰界定边界；悬停时整体微微浮起（`TranslateY: -2, Scale: 1.015`, `220ms`）并淡入描边与关闭小叉；支持鼠标拖拽移动及滚轮缩放（中央等宽百分比气泡 `750ms` 自动淡出）。

### 6.3 托盘右键菜单与长截图编辑器
- **托盘右键菜单（`AppHost.cs` / `GraphiteToolStripRenderer.cs`）**：
  - 完整保留原有全部菜单项（截图、录屏、解除全部贴图穿透、关闭全部贴图、分割线、设置、退出）与快捷键文案。
  - **推荐实现**：改为无边框高透 WPF 弹出窗口（`AllowsTransparency = true`, `Topmost = true`, `Deactivated` 自动收起），使用 `SquircleGlassBorder(r = 10.5, Padding = 4)`，内部接入 `LiquidSpringTracker(Axis.Y)`（`stiffness = 380, damping = 36`）实现滑行悬停高亮条（`r = 6.5`），点击菜单项触发极轻淡透明度确认（`200ms`）；
  - （备选兼容方案：若必须沿用 WinForms `ContextMenuStrip`，则升级 `GraphiteToolStripRenderer` 为浅色白雾底 `#F7F8FA` + 深炭灰文字 `#1D1D1F` + 柔和圆角悬停底 `#0F0F172A`。）
- **长截图编辑器（`LongScreenshotEditorWindow.cs`）**：同步迁移至浅色 `Tokens.xaml` 资源、顶部/底部工具栏换为 `10.5px` 连续曲率磨砂玻璃与 `AppleVectorIcons` 纯净图标。

---

## 7. 推荐 5 步实施路线与自检标准

建议按以下 5 个独立可编译节点顺序实施，**每完成一步均执行 `.\verify.ps1` 验证**：

1. **Step 1 — UI 基础引擎与设计令牌**：改造 `Tokens.xaml` / `GraphiteControls.xaml` / `App.xaml`，落地 `SquircleGeometry`、`SquircleGlassBorder`、`AppleVectorIcons`、`LiquidSpringTracker`，并为 `SquircleGeometry` 与 `LiquidSpringTracker` 编写核心单元测试。
2. **Step 2 — 截图主流程重构**：改造 `CaptureOverlayWindow.*`（顶部模式条、主工具栏、二级跟随条、弹出编排动效）、`CaptureSurface.*`（浅色选区 Chrome、圆形光学放大镜及 `0.30` 阻尼跟随）、`TextPromptWindow`（轻量透明内联）与 `HintToastWindow`（底部轻提示与对勾描边）。
3. **Step 3 — OCR 与翻译**：统一重构 `OcrCard`、`TranslationCard`（`13px` 磨砂卡片、拖拽抬起）与原位覆盖状态条/渐变。
4. **Step 4 — 录屏全链路**：改造单行准备条并激活 `MP4 / GIF` 选择、取消灰色摄像头占位、更新 `RecordingCountdownWindow`（涟漪）、`RecordingControlWindow`（浅色灵动岛与 180ms 声波）、`RecordingPreviewWindow` & `TimelineBar`。
5. **Step 5 — 设置窗、贴图、托盘与长截图**：按第 6 节无框多层阴影与轻柔淡交互规范彻底重构 `SettingsWindow.*`，修复 `PinWindow` 同心连续曲率裁切与悬停描边，升级托盘菜单与 `LongScreenshotEditorWindow`。

### 最终验收核查表
- [ ] `.\verify.ps1` 全绿通过（`restore`、`Release build`、`test`、`dotnet format Screen.slnx whitespace --verify-no-changes` 无任何告警或失败）。
- [ ] 全应用视觉风格统一为浅色磨砂 × 液态玻璃，全套窗口、工具栏、滑块、卡片严格使用 `0.60` G2 连续曲率及同心内圆角。
- [ ] 截图框选前常驻顶部模式条与圆形放大镜，框选后工具栏按编排流畅弹出，完成后呈现底部 `8px` 轻提示。
- [ ] 录屏前可真实切换 `MP4 / GIF`，选 `GIF` 时音轨按钮自动禁用并联动后续默认导出格式。
- [ ] 设置界面侧栏字体清晰舒展（40px 行高 / 13.5px 字号）、无硬分割边框、多层柔和投影空间层次分明，按钮与开关交互“轻、柔、淡”无突兀弹跳。
- [ ] 桌面贴图与悬停蓝边同心贴合，在纯白网页与深色终端背景上均境界清晰。
