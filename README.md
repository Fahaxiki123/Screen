# Flick

> 原项目 `Screen` 全面焕新为 **Flick**，全新浅色磨砂 × 液态玻璃界面与高定品牌图标正式发布。

个人使用的 Windows 截图、贴图、录屏、文字识别与翻译工具，使用 .NET 10 / WPF。

程序、托盘、设置窗与安装器使用统一的 Flick 晴空蓝虚线选区图标；全应用采用浅色磨砂 × 液态玻璃主题、Apple G2 连续曲率卡片与物理弹簧微动效，提供顶部模式条、智能跟随工具栏、翻译卡片、原位覆盖翻译和录屏灵动岛。

## 下载

**[获取最新稳定版](https://github.com/Fahaxiki123/Screen/releases/latest)** · [全部版本](https://github.com/Fahaxiki123/Screen/releases)

截至 **2026-11-28** 最新发布版本：**2.0.4**。以下程序包、校验文件和源码均对应 `v2.0.4`；后续升级使用上方最新稳定版入口。

**[下载 2.0.4 Windows x64 安装包（.exe）](https://github.com/Fahaxiki123/Screen/releases/download/v2.0.4/Flick-2.0.4-win-x64-Setup.exe)** · [版本说明](https://github.com/Fahaxiki123/Screen/releases/tag/v2.0.4) · [SHA-256 校验文件](https://github.com/Fahaxiki123/Screen/releases/download/v2.0.4/SHA256SUMS.txt) · [同版本源码](https://github.com/Fahaxiki123/Screen/tree/v2.0.4)

离线安装包支持 Windows 10 21H2（19044）及更新版本的 Windows 10 / Windows 11 x64，已完整自包含 .NET Desktop Runtime、Visual C++ 运行库、本地 PaddleOCR 模型与 FFmpeg，无需管理员权限。原有 `%LOCALAPPDATA%\Screen` 设置与 Windows 凭据管理器中的 API 密钥完全保留兼容。更新前请完成截图或录屏并从托盘退出旧版程序。

### 2.0.4 主要更新

- **开机并行自启，不再被其他开机项排队阻塞**：开机自启升级为当前用户任务计划程序登录任务（`Flick-AutoStart-<SID>`，`Normal` 优先级、无 72 小时自动终止限制），自动迁移并清理旧 `HKCU\Run` 启动项，同时尊重任务管理器/任务计划程序的手动禁用设置；卸载时同步清理登录任务。
- **开机进桌面即可秒按快捷键截图**：移除截图遮罩层对未签名图标程序集的隐式依赖，开机就绪后立即预热微软签名 GDI 截屏通道与真实遮罩窗口；在 Windows 11 智能应用控制评估模式下刚进桌面按快捷键也能直接弹出截图框，后台由独立短生命周期子进程无阻塞分阶段预热 DXGI 后自动切回主通道。
- **显著加快首次翻译与文字识别**：后台独立子进程预热 OCR 托管程序集（不常驻加载 400 MB 模型，保持低空闲内存），避免加载器锁阻塞主界面与快捷键；点击顶部模式栏「提取文字」或「翻译」时按需提前初始化本地 PP-OCRv6 引擎。
- **全局快捷键容错与锁屏恢复**：启动注册快捷键失败时自动静默重试，锁屏解锁或切换控制台时自动释放陈旧 DXGI 会话、刷新低级键盘钩子并恢复快捷键，并补充启动、预热与分阶段截图耗时诊断日志。

上一版更新见 [Flick 2.0.3 发布说明](https://github.com/Fahaxiki123/Screen/releases/tag/v2.0.3)。

## 已实现的功能

- 区域/窗口截图、选区调整、取色和放大镜。
- 矩形、椭圆、线条、箭头、画笔、文字、序号、像素/模糊马赛克，标注新增与修改支持撤销重做。
- 向下自动/手动滚动截图、结果编辑和裁剪。
- 桌面贴图、贴图穿透与恢复。
- 随程序分发的 PaddleOCR 模型、腾讯云 OCR、本地/云端识别策略；OCR 卡片支持手工编辑纠错后直接翻译。
- 翻译服务有两种：默认的免费在线翻译（无需配置），以及可自填地址、模型和 API Key 的 AI 自然翻译。AI 支持第二模型竞速，也可在失败时仅将未完成的部分交给免费在线翻译。
- OCR/翻译等待时在选区内上下往返扫光，翻译可在设置的「识别与翻译」切换浮动卡片与原位覆盖，并在卡片与原位悬浮条间一键互切。
- 原位译文按识别位置覆盖，可对照原文、复制译文、切回卡片查看，并将当前显示画面复制、保存 PNG 或贴图。
- 单显示器或跨显示器区域录屏、暂停恢复、系统声/麦克风、点击效果、MP4 / GIF、录后预览与时长裁切。
- 热键、启动项与保存目录设置。

原位翻译通过局部底色覆盖文字，复杂图片背景可能出现覆盖痕迹，过长译文会缩小字号。单显示器录屏走 DXGI，要求 SDR。跨显示器选区按桌面物理像素采集，显示器布局中的空白区域填黑。当前翻译提供方以上述列表为准，本地 OCR 模型随项目提供。

跨显示器录屏请从录屏快捷键或托盘「录屏」开始，拖出覆盖多个屏幕的选区。

## 环境与获取源码

- x64 Windows 10 21H2（19044）及更新版本的 Windows 10 / Windows 11。项目使用较新的 Windows SDK，最低运行平台声明为 `10.0.19044.0`。
- .NET 10 SDK。
- Git 和 Git LFS，FFmpeg 二进制通过 Git LFS 保存。
- 构建安装包时另需 PowerShell 7 和 NSIS 3.12。

复现上方 2.0.4 版本时，使用同版本标签检出。在用于存放项目的父目录中执行以下命令；持续开发可另行使用 `main` 分支。

```powershell
git lfs install
git clone --branch v2.0.4 --single-branch https://github.com/Fahaxiki123/Screen.git 开发工程
Set-Location 开发工程
git lfs pull
```

`开发工程` 是发布脚本的默认目录约定。已有不同名称的源码目录时，可使用 `./rebuild-and-restart.ps1 -ProductRoot '<源码目录外的产品目录>'` 显式指定发布位置。

`src/Screen.App/ffmpeg/bin/ffmpeg.exe` 应为完整的可执行文件，构建和录屏集成测试均依赖它。OCR 模型位于 `src/Screen.App/Models/Ocr`，安装器所需的 Visual C++ 运行库位于 `tools/installer/runtime/win-x64`。这些依赖及随附的第三方声明应与源码一同保留。

## 构建与验证

在 `开发工程` 目录中执行以下命令。首次还原依赖需要访问 NuGet。

```powershell
dotnet restore Screen.slnx
dotnet build Screen.slnx -c Release --no-restore
dotnet test Screen.slnx -c Release --no-build --no-restore
dotnet format Screen.slnx whitespace --verify-no-changes --no-restore
```

也可运行 `./verify.ps1` 执行上述检查。发布脚本的独立回归检查可通过 `./tools/Test-PublishFlow.ps1` 运行。

`src/Screen.Core` 保存算法和服务，`src/Screen.App` 集成 Windows/WPF。`tests/Screen.Core.Tests` 与 `tests/Screen.App.Tests` 使用离线数据、临时文件及模拟服务，云服务请求由测试替身接管。

## 开发运行

```powershell
dotnet run --project src/Screen.App/Screen.App.csproj -c Release
```

开发运行会使用当前 Windows 用户的 Screen 配置。程序在托盘中常驻，切换开发版与正式版前，应先完成截图或录屏并从托盘退出当前实例。

## 发布与运行

发布脚本沿用以下相对目录布局，`正式版` 和 `安装包` 都位于源码目录的上一层。脚本会按需创建输出目录。

```text
项目父目录/
├─ 开发工程/    # 本仓库
├─ 正式版/      # 自包含发布结果
└─ 安装包/      # 分发安装文件
```

先完成截图或录屏并从托盘退出 Screen，再从 `开发工程` 执行发布命令。

```powershell
./rebuild-and-restart.ps1
```

脚本在 `artifacts/publish` 下的临时目录构建，通过完整性检查后整体替换 `../正式版` 并启动程序。使用 `./rebuild-and-restart.ps1 -NoRestart` 可仅发布。成功后清理临时候选与回滚目录，失败时保留或恢复旧版。若程序仍在运行，脚本会停止更新并提示退出。用户设置和凭据使用独立的用户级存储。

正式版采用 **Windows x64 自包含发布**，自带 .NET Desktop Runtime、OCR 模型、原生库与 FFmpeg。日常使用可启动 `../正式版/Screen.exe`，分发时必须保留完整发布目录，运行环境随重新发布更新。

### 构建安装包

另行准备 NSIS 3.12，在 PowerShell 7 中执行以下命令，并将编译器路径替换为实际位置。

```powershell
./tools/build-installer.ps1 -CompilerPath 'C:\Tools\NSIS\makensis.exe'
```

脚本也会从 `PATH`、常见 NSIS 安装目录及 `artifacts/tooling/nsis-3.12/Bin/makensis.exe` 自动发现编译器。打包使用上一层已有的 `正式版` 目录，应先完成发布与功能检查。构建成功后，`../安装包` 中保留当前版本的 `Flick-<版本>-win-x64-Setup.exe`。

安装器按当前用户安装，可直接使用标准用户权限，包含 .NET、OCR 模型、FFmpeg 和 Visual C++ 运行库。用户作品、个人配置与密钥由各用户自行管理，默认保存目录为使用者自己的图片目录，云服务配置由使用者自行填写。

图标可通过 `powershell.exe -NoProfile -STA -File tools/generate-app-icon.ps1` 重建。构建与发布产生的 `bin`、`obj` 及 `artifacts/publish` 属于本地输出，可通过对应流程重新生成。

## 默认快捷键

| 操作 | 默认值 |
| --- | --- |
| 截图 | Ctrl+Shift+A |
| 录屏 | Ctrl+Shift+R |
| 恢复穿透贴图 | F1 |
| 完成截图 | Enter / Ctrl+C |
| 保存截图 | Ctrl+S |
| 撤销 / 重做标注 | Ctrl+Z / Ctrl+Y |
| 取消截图 | Esc |

OCR/翻译文本获得焦点时，复制、换行和撤销交给文本框处理。

截图、录屏和恢复贴图均可直接设置 F1–F24。快捷键页可启用「快捷键优先交给 Flick」。此选项会优先处理已设置的组合，系统保留的操作和其他应用的键盘钩子仍可能先收到按键。

## 配置和失败恢复

- 设置文件位于 `%LOCALAPPDATA%/Screen/settings.json`。
- 密钥保存在 Windows 凭据管理器，与 settings.json 分开存储。
- 日志位于 `%LOCALAPPDATA%/Screen/logs`。
- 录屏保存失败时，提示会给出恢复素材目录。请保留该目录，直至完成素材恢复。
- 裁切复制会在原录屏目录生成独立 MP4，关闭预览后仍可使用已复制文件。
- 新安装默认使用免费在线翻译。升级后，原先选用腾讯机器翻译或 MyMemory 的设置会自动切回免费在线翻译；原先选用 AI 的用户保留所选服务、模型和密钥。
- AI 失败时，只有打开「AI 失败时使用免费在线翻译」才会将原文交给免费在线翻译；免费服务失败会直接报错，不会再转发给其他翻译商。
- OCR 联网行为以「识别与翻译 → 高级选项」中的策略和说明为准；选区添加任意标注后自动按标注后渲染像素识别，避免被遮挡文字从底层窗口泄漏。

## 验证边界

自动测试覆盖离线 HTTP、临时文件、模拟音频回调、离屏 WPF 控件和 FFmpeg 编解码。不同显卡、声卡、DPI、HDR 及长时间录制仍需实机验收。NAudio 3 旧采集接口存在弃用提示，迁移新接口需要设备录制与回放验证。

项目为个人使用，项目代码保留所有权利。第三方信息见 `src/Screen.App/THIRD-PARTY-NOTICES.txt`，完整随附声明位于 `tools/installer/third-party-notices`。
