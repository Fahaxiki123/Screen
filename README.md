# Screen

个人使用的 Windows 截图、录屏、OCR 与翻译工具，使用 .NET 10 / WPF。

当前源码版本为 **1.0.9**。程序、托盘与安装器使用统一的蓝色截图图标，界面采用夜蓝与月白青渐变主题，提供横向快捷栏、翻译卡片和录屏预览。

## 已实现的功能

- 区域/窗口截图、选区调整、取色和放大镜。
- 矩形、椭圆、线条、箭头、画笔、文字、序号、像素/模糊马赛克，标注新增与修改支持撤销重做。
- 向下自动/手动滚动截图、结果编辑和裁剪。
- 桌面贴图、贴图穿透与恢复。
- 随程序分发的 PaddleOCR 模型、腾讯云 OCR、本地/云端识别策略。
- 腾讯机器翻译、OpenAI 兼容 AI 自然翻译、MyMemory，支持失败回退和模型竞速。
- OCR/翻译等待时在选区内上下往返扫光，翻译可在设置的“翻译接口 → 翻译显示”切换浮动卡片与原位覆盖。
- 原位译文按识别位置覆盖，可对照原文、复制译文，并将当前显示画面复制、保存 PNG 或贴图。
- 单显示器内区域/全屏录屏、暂停恢复、系统声/麦克风、点击效果、MP4、录后预览与时长裁切。
- 热键、启动项与保存目录设置。

原位翻译通过局部底色覆盖文字，复杂图片背景可能出现覆盖痕迹，过长译文会缩小字号。录屏区域须位于单个显示器内，要求 SDR。当前翻译提供方以上述列表为准，本地 OCR 模型随项目提供。

## 环境与获取源码

- Windows 11 24H2（26100）及更新版本的 x64 系统，项目最低平台声明为 `10.0.26100.0`。
- .NET 10 SDK。
- Git 和 Git LFS，FFmpeg 二进制通过 Git LFS 保存。
- 构建安装包时另需 PowerShell 7 和 NSIS 3.12。

发布脚本要求源码目录名为 `开发工程`。在用于存放项目的父目录中执行以下命令。

```powershell
git lfs install
git clone https://github.com/Fahaxiki123/Screen.git 开发工程
Set-Location 开发工程
git lfs pull
```

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

脚本也会从 `PATH`、常见 NSIS 安装目录及 `artifacts/tooling/nsis-3.12/Bin/makensis.exe` 自动发现编译器。打包使用上一层已有的 `正式版` 目录，应先完成发布与功能检查。构建成功后，`../安装包` 中保留当前版本的 `Screen-1.0.9-win-x64-Setup.exe`。

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

## 配置和失败恢复

- 设置文件位于 `%LOCALAPPDATA%/Screen/settings.json`。
- 密钥保存在 Windows 凭据管理器，与 settings.json 分开存储。
- 日志位于 `%LOCALAPPDATA%/Screen/logs`。
- 录屏保存失败时，提示会给出恢复素材目录。请保留该目录，直至完成素材恢复。
- 裁切复制会在原录屏目录生成独立 MP4，关闭预览后仍可使用已复制文件。
- AI 失败且已配置腾讯密钥时，会尝试腾讯。公共 MyMemory 回退开关控制之后是否向 MyMemory 发送原文。直接选择 MyMemory 时始终使用该服务。
- OCR 联网行为以文字识别页的策略和说明为准。

## 验证边界

自动测试覆盖离线 HTTP、临时文件、模拟音频回调、离屏 WPF 控件和 FFmpeg 编解码。不同显卡、声卡、DPI、HDR 及长时间录制仍需实机验收。NAudio 3 旧采集接口存在弃用提示，迁移新接口需要设备录制与回放验证。

项目为个人使用，项目代码保留所有权利。第三方信息见 `src/Screen.App/THIRD-PARTY-NOTICES.txt`，完整随附声明位于 `tools/installer/third-party-notices`。
