<div align="center">
  <img src="https://raw.githubusercontent.com/OsmiumRemote/Osmium/master/assets/osmium_logo.svg" alt="Osmium Logo" width="128" />
  <h1><a href="https://github.com/OsmiumRemote/Osmium">Osmium</a></h1>
</div>

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20%7C%20Android%20%7C%20Web-brightgreen)
![License](https://img.shields.io/badge/License-MIT-orange)
![Stars](https://img.shields.io/github/stars/OsmiumRemote/Osmium?style=flat&color=yellow)
![Downloads](https://img.shields.io/github/downloads/OsmiumRemote/Osmium/total?style=flat&color=blue)

### 支持4K+和最高240FPS及HDR与P2P直连！一款开源、永久免费的低延迟远程桌面协助与游戏串流软件

**如果这个项目对你有帮助，欢迎点一个Star支持持续开发**

- [Web网页端](https://web.osmium.fufu.skin) - [OsmiumWeb开源仓库](https://github.com/OsmiumRemote/OsmiumWeb)

- [English](./README.md)

---

### 提出问题前，请先确认已更新至最新版本，并为issue添加对应标签!

## 主要功能

- 高帧率串流：支持4K+与最高240FPS H.264/HEVC/AV1编解码，支持HDR与YUV 4:4:4色彩模式
- 双向音频：支持被控端系统音频串流、主控端麦克风透传与虚拟音频切换
- 手柄与外设：内置手柄支持与自定义键位映射，支持主机/虚拟/智能鼠标模式、多点触控与可选虚拟外设驱动
- 远程协助：支持多显示器切换、隐私屏、双向剪贴板同步与文件传输
- 无人值守与电源：支持系统服务开机自启、网络唤醒、远程关机/重启/睡眠/锁屏、应用快速启动，以及Ctrl+Alt+Del与任务管理器快捷指令
- 多端协同：支持Windows 10/11主控与被控、Android移动端与免安装Web网页端
- 网络连接：优先P2P直连，支持局域网、IPv6及跨网直连，受限网络下自动切换中继
- 账号与安全：支持端到端加密传输，提供两步验证、第三方登录、扫码授权、登录密钥与多设备管理

## 基于的技术栈

- Windows桌面端：.NET 8、WinUI 3
- 原生引擎：C++、Direct3D 11/DXGI、FFmpeg、Opus、SDL3
- Android移动端：Flutter、C++、MediaCodec
- Web网页端：WebRTC、WebCodecs、AudioWorklet

## 怎么用

- 前往[Releases](https://github.com/OsmiumRemote/Osmium/releases)下载最新版本并安装到被控与主控设备，也可直接使用[Web网页端](https://web.osmium.fufu.skin)
- 在账号页面通过邮箱、第三方账号、扫码或登录密钥登录以同步设备列表，或在被控端开启允许远程控制
- 在设备列表选择在线主机，或直接输入目标设备ID发起连接
- 连接后可在会话顶部控制栏随时调整分辨率、帧率、编码格式、鼠标模式、音频与隐私屏

## 注意事项

- 桌面端需要Windows 10 2004及以上或Windows 11 x64系统，建议使用支持硬件编解码的显卡以获得更好的4K高帧率体验
- 安装可选虚拟外设驱动或配置系统服务开机自启需要**管理员权限**
- **隐私屏**、**快速启动**与**远程电源/网络唤醒**需在被控端设置中开启对应授权，网络唤醒同时需主板BIOS与网卡支持
- **如发现安全漏洞请不要提交公开Issue**，请参阅[SECURITY.md](./SECURITY.md)及时告知我们；如有使用问题请参阅[SUPPORT.md](./SUPPORT.md)

## 支持的语言

![English](https://img.shields.io/badge/English-en--US-informational)
![简体中文](https://img.shields.io/badge/%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-zh--CN-informational)

## 贡献者

<a href="https://github.com/OsmiumRemote/Osmium/graphs/contributors"><img src="https://contrib.rocks/image?repo=OsmiumRemote/Osmium" /></a>
