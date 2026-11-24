# Galaxy Buds Widget for macOS

macOS **液态玻璃桌面小组件 + 坐姿检测 + 本地网页设置向导**（基于开源项目 [timschneeb/GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient) 深度精简与改造）。

<p align="center">
  <img src="screenshots/widget.jpg" alt="Galaxy Buds Liquid Glass Desktop Widget" width="420" />
</p>

## 核心特性

### 1. macOS 原生液态玻璃桌面小组件 (`libBudsWidget.dylib`)
- **真·液态玻璃**：基于 macOS 26+ `NSGlassEffectViewStyleClear` + `1px` 顶层高光棱边折射 + 点击激活的随动焦散光晕。
- **桌面底层常驻**：窗口层级与 macOS 桌面小组件一致，不遮挡正常应用窗口；长按顶部区域 `0.25s` 解锁拖动并记住位置，防误触。
- **4 档降噪平滑切换**：支持 **关闭 / 自适应 / 降噪 / 环境音**，带玻璃胶囊滑动动画与实时双向同步。
- **轻量电量显示**：紧凑显示左耳（L）、右耳（R）、充电盒电量；连接时带居中放大耳机图标飞入左上角的过渡动画。
- **坐姿检测快捷控制**：右上角集成坐姿检测开关与基准校准按钮。

### 2. 低头坐姿检测 (`PostureMonitorService` + `libPostureOverlay.dylib`)
- 读取耳机内置 IMU 四元数传感器，低头超过阈值（默认 `9°`）后触发全屏平滑失焦模糊提醒，坐直后自动恢复清晰。
- 支持自动重连传感器，避免刚连上耳机时启动延迟。

### 3. 极简菜单栏 + 本地网页设置向导 (`WebSettingsServer`)
- 无冗余桌面主窗口，菜单栏仅保留：**设置向导...** / **重启** / **退出**。
- 网页设置向导支持：
  - 三星全系型号选择（含 **Galaxy Buds4 / Buds4 Pro**、Buds3 系列等）与实物渲染图预览
  - 已配对蓝牙设备一键连接 / 断开
  - 4 档降噪模式、**无缝切换（Seamless Connection）**、**双设备互联解锁（Multipoint）**
  - **均衡器（EQ）**：支持三星内置预设（低音 / 柔和 / 动感 / 清晰 / 高音）及 **9 段自定义 EQ（-10 ~ +10 dB）**
  - 坐姿检测触发角度与模糊强度调校

## 构建与安装（macOS Apple Silicon）

```bash
# 1. 编译原生液态玻璃与坐姿模糊动态库
./GalaxyBudsClient.Platform.OSX/Native/build-buds-widget.sh
./GalaxyBudsClient.Platform.OSX/Native/build-posture-overlay.sh

# 2. 发布 osx-arm64 应用包
dotnet publish GalaxyBudsClient/GalaxyBudsClient.csproj -c Release -r osx-arm64 \
  -p:UseMacOSSDK=true \
  -p:AllowMissingPrunePackageData=true \
  -p:ValidateXcodeVersion=false
```

## 致谢与协议

- 上游项目：[timschneeb/GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient)
- 坐姿检测灵感参考：[tldev/dorso](https://github.com/tldev/dorso)
- 开源协议：[GPL-3.0 License](LICENSE)
