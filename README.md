# Brokenithm LAN

Windows 接收端，将 [Brokenithm-SwiftUI](https://github.com/OWCramer/Brokenithm-SwiftUI) 的 iPhone/iPad 触摸输入通过局域网转发到兼容的 CHUNITHM I/O 驱动，并回传灯光数据。

默认自动发现设备、静默等待，仅在握手成功后显示连接成功信息。支持手动 IP、自动重连、关闭灯光和按需诊断日志。

## 环境要求

- Windows，安装 .NET Framework 4.7.2 或更高版本。
- 使用启动、构建或打包脚本时，需要 Windows PowerShell 5.1 或更高版本。
- iPad/iPhone 已安装兼容的 Brokenithm-SwiftUI，并与 PC 位于可互访的同一局域网。
- 游戏已能正常运行，并加载支持 Brokenithm 共享内存协议的 ChuniIO；使用刷卡功能时还需兼容的 AimeIO。

PC 接收端不依赖 iTunes、Apple USB 驱动、Bonjour 安装包或旧版 USB 接收程序。源码构建没有 NuGet 或 .NET SDK 依赖。

## 快速开始

1. 将完整运行包解压到任意可访问的目录，保持包内文件在一起。
2. 打开 iPad 应用，允许“本地网络”权限，保持应用前台和屏幕解锁；需要 AIR 时打开应用的 Air Input。
3. 双击 `Start-LAN.cmd`。程序默认使用约 3 秒的发现窗口，没有找到设备时会静默重试。
4. 出现 `已连接 iPad：设备名称（IP）` 后启动游戏，并在游戏测试菜单核对输入。
5. 使用 Ctrl+C 停止接收端。断线后会释放输入并重新连接，重新握手成功时再次显示提示。

如 Windows 防火墙询问网络权限，在受信任的局域网允许“专用网络”。PC 可以使用网线，iPad 使用同一路由器的 Wi-Fi。

直接使用 Git 源码目录时，`Start-LAN.cmd` 会在首次运行缺少 EXE 时调用本目录的 `build.ps1`。源码和运行包均不需要父目录中的文件。

| 入口 | 行为 |
| --- | --- |
| `Start-LAN.cmd` | 自动发现，仅显示连接成功提示，不生成日志 |
| `Start-LAN-NoLED.cmd` | 相同行为，额外关闭灯光回传 |
| `Start-LAN-Diagnostics.cmd` | 自动发现，显示并保存详细诊断 |

同一 Windows 登录会话内只运行一个 USB 或 LAN 接收端。游戏与接收端需要处于同一登录会话，并使用兼容的运行权限。参数错误或程序无法启动时仍会显示必要错误。

## 游戏侧接入

接收端独立运行，通过 `Local\BROKENITHM_SHARED_BUFFER` 与游戏侧驱动交换数据。运行包不包含游戏、游戏数据或第三方 I/O DLL；这些驱动应放在游戏要求的位置，而不是接收端目录。

如果现有游戏配置已经通过 mux 加载兼容的 Brokenithm 驱动，沿用该配置即可。单 DLL 的常见配置形式如下，实际文件名以驱动说明为准：

```ini
[chuniio]
path=brokenithm.dll
```

使用提供 x86/x64 两个 DLL 的驱动及支持它们的加载器时，可能采用以下形式：

```ini
[chuniio]
path32=brokenithm_x86.dll
path64=brokenithm_x64.dll
```

按游戏与驱动的说明选择一种配置方式，不添加重复段落或同时保留冲突的生效路径。Tap Card 还需要游戏侧启用相应 AimeIO。本项目不会自动修改游戏配置或卡号文件。

## 手动连接与选项

IP 填写 iPad 的地址；自动发现模式使用设备公布的端口。

```powershell
.\Brokenithm-LAN.exe --ip 192.168.1.50
.\Brokenithm-LAN.exe --device "Living Room iPad"
.\Brokenithm-LAN.exe --no-led
.\Brokenithm-LAN.exe --diagnostics
.\Brokenithm-LAN.exe --help
```

启动脚本也接受参数，例如：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Start-LAN.ps1 -IP 192.168.1.50 -NoLed
```

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `--ip` | 自动发现 | 直接连接指定 IPv4/IPv6 地址，跳过发现 |
| `--device` | 不筛选 | 按设备名或完整服务实例名筛选，与 `--ip` 互斥 |
| `--port` | 24864 | 手动 IP 模式的 TCP 端口 |
| `--no-led` | 关闭 | 传入此开关后停止灯光回传，继续处理输入 |
| `--diagnostics` | 关闭 | 输出并保存详细诊断 |
| `--log-dir` | 未指定 | 指定日志目录，同时启用诊断 |
| `--discover-timeout-ms` | 3000 | 每轮发现窗口，500–60000 毫秒 |
| `--connect-timeout-ms` | 3000 | TCP 建连超时，100–60000 毫秒 |
| `--handshake-timeout-ms` | 5000 | WEL 握手总超时，100–60000 毫秒 |
| `--retry-ms` | 1000 | 重连间隔，100–60000 毫秒 |

## 构建、验证与打包

在项目根目录运行：

```powershell
# 编译到项目根目录
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 编译并执行测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Tests

# 编译并生成独立运行包
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Package
```

打包输出为 `dist\Brokenithm-LAN-portable.zip`。ZIP 包含 EXE、运行配置、启动脚本和说明文档，不包含源码、测试产物或本地日志，可单独复制到其他目录运行。

`build.ps1` 使用 .NET Framework 自带的 C# 编译器。也可用安装了 .NET Framework 4.7.2 targeting pack 的 Visual Studio/MSBuild 打开 `Brokenithm-LAN.csproj`。

测试使用回环 TCP、独立临时共享内存和模拟 mDNS，不连接真实 iPad 或启动游戏。覆盖分帧、输入、LED、重连、异常恢复、DNS-SD、安静模式和异步日志。已通过 11 组测试；实机日志确认过 iPad 与接收端握手及触摸输入，游戏内行为仍需对应环境验证。

## 项目结构

```text
src/                         接收端源码和 App.config
tests/                       自包含协议与集成测试
docs/PROTOCOL.md             网络协议与共享内存
docs/TROUBLESHOOTING.md      网络、日志和 iPad 限制
Brokenithm-LAN.csproj        .NET Framework 项目
build.ps1                   构建、测试与运行包生成
Start-LAN*.cmd               启动入口
Start-LAN.ps1                启动参数与源码首次构建
```

Git 仅跟踪源码、测试、脚本与文档；生成的 EXE/config、`bin/`、`dist/`、临时文件和个人日志由 `.gitignore` 排除。运行包可作为单独的发布附件分发。

排障见 [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)，实现细节见 [PROTOCOL.md](docs/PROTOCOL.md)。

## 相关项目

- [OWCramer/Brokenithm-SwiftUI](https://github.com/OWCramer/Brokenithm-SwiftUI)：兼容的 iOS/iPadOS 应用。
- [esterTion/Brokenithm-iOS](https://github.com/esterTion/Brokenithm-iOS)：原始 iOS/USB 桥接实现与协议参考。
- [Brokenithm-Evolved](https://gitea.tendokyu.moe/Dniel97/Brokenithm-Evolved)：兼容共享内存布局的 I/O 驱动参考。
