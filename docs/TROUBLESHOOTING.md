# 排障

## 打开诊断

先用 Ctrl+C 关闭普通接收端，再运行 `Start-LAN-Diagnostics.cmd` 或 `Brokenithm-LAN.exe --diagnostics`。普通模式不会创建日志，诊断模式每次创建一个独立 UTF-8 文件：

```text
logs/brokenithm-日期-时间-进程号.log
```

程序目录不可写时，尝试 `%LOCALAPPDATA%\Brokenithm-LAN\logs`；启动日志中的 `Log file:` 是实际位置。可用 `--log-dir` 或 `Start-LAN.ps1 -LogDirectory` 指定目录。

日志包含时间、时区、单调累计耗时、序号、线程、发现过程、地址、Socket 错误码、握手和收发计数。连接期间每五秒汇总计数，不记录原始触摸内容、RGB 数组或卡号。日志可能包含设备名称和局域网地址，分享问题时可按需脱敏。

## 没有连接成功提示

- 保持 iPad 应用前台、屏幕解锁，允许应用的本地网络权限。
- 确认设备能够互访，检查访客网络、AP 隔离、VLAN、VPN 和路由。
- 自动发现需要 UDP 5353 组播；允许程序使用受信任的专用网络。
- PC 是 TCP 客户端，不监听 TCP 24864；目标端口位于 iPad。
- 如果组播不通而 TCP 可互访，用 `--ip <iPad地址>` 回退。
- 多个地址可能属于同一台 iPad，分别为 IPv4、IPv6 或不同网卡作用域。

跨子网网络通常不自动转发 mDNS。自动发现经 IPv4 组播工作，纯 IPv6 网络使用手动 IP。

## 积极拒绝 / ConnectionRefused / 10061

服务已被发现，但公布的 TCP 端点没有接受连接。可能是监听停止、应用暂停、旧发现记录或网络设备主动拒绝。此时尚未进入 WEL、输入或灯光处理。

在 iPad 多任务界面彻底划掉应用，再重新打开并保持前台。已查阅的 SwiftUI 实现中，`restart()` 在尚无连接对象时直接返回，因此只点 `Restart Connection` 可能无法重建监听。PC 无法远程启动 iPad 的监听。

## 连接成功但游戏没有输入

有效 WEL 和收到输入帧说明 iPad 与 PC 桥接已经工作。接下来检查游戏是否实际加载兼容的 ChuniIO，且与接收端处在同一 Windows 登录会话。Tap Card 还要求启用兼容 AimeIO。

游戏现有 mux 若已加载 Brokenithm，应先沿用配置。备份配置后再按驱动说明调整路径，避免多个生效路径或同时运行 USB/LAN 接收端。

## 灯光异常或 iPad 应用退出

先关闭当前接收端，使用 `Start-LAN-NoLED.cmd` 或 `--no-led` 验证纯输入。

已查阅的 SwiftUI `Network.swift` 将一次 TCP receive 当作 LED 帧，且短数据长度检查不足。PC 已合并单次写入，但网络仍可拆分或合并数据；完整修复需要更新 iPad 应用。不要同时用端口探测工具和实际接收端连接同一 iPad。

## 更新和缺失文件

更新前先关闭旧接收端。若存在 `Brokenithm-LAN.pending.exe`，启动脚本会在旧程序退出后应用更新。

运行包需要保留 EXE、对应 `.exe.config` 和启动脚本。源码目录缺少 EXE 时，启动脚本会调用本目录的 `build.ps1`；运行包不包含编译源码，缺少 EXE 时应重新解压完整包。
