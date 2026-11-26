<div align="center">
  <img src="https://raw.githubusercontent.com/OsmiumRemote/Osmium/master/assets/osmium_logo.svg" alt="Osmium Logo" width="128" />
  <h1><a href="https://github.com/OsmiumRemote/Osmium">Osmium</a></h1>
</div>

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11%20%7C%20Android%20%7C%20Web-brightgreen)
![License](https://img.shields.io/badge/License-MIT-orange)
![Stars](https://img.shields.io/github/stars/OsmiumRemote/Osmium?style=flat&color=yellow)
![Downloads](https://img.shields.io/github/downloads/OsmiumRemote/Osmium/total?style=flat&color=blue)

### Supports 4K, up to 240FPS, HDR, and P2P direct connection! An open-source, permanently free low-latency remote desktop assistance and game streaming software

**If you find this project helpful, please consider giving it a Star to support ongoing development.**

- [Web Client](https://web.osmium.fufu.skin) - [OsmiumWeb Repository](https://github.com/OsmiumRemote/OsmiumWeb)

- [简体中文](./README-CN.md)

---

### Before opening an issue, please verify that you are on the latest version and attach the appropriate labels.

## Main Features

- High-FPS Streaming: 4K+ and up to 240 FPS with H.264/HEVC/AV1 encoding & decoding, HDR, and YUV 4:4:4 color mode
- Bidirectional Audio: Host system audio streaming, client microphone passthrough, and virtual audio switching
- Gamepad & Input: Built-in controller support and custom key mapping, host/virtual/smart mouse modes, multi-touch, and optional virtual peripheral drivers
- Remote Assistance: Multi-monitor switching, Privacy Screen, bidirectional clipboard sync, and file transfer
- Unattended & Power Control: System service auto-start, Wake-on-LAN, remote shutdown/restart/sleep/lock, Quick Launch, and Ctrl+Alt+Del / Task Manager shortcuts
- Multi-Platform: Windows 10/11 host & client, Android mobile client, and zero-install Web client
- Connectivity: P2P direct connection first across LAN, IPv6, and WAN, with automatic relay fallback on restricted networks
- Account & Security: End-to-end encrypted sessions, two-factor authentication, OAuth, QR login, revocable login keys, and multi-device management

## Built With

- Windows Desktop: .NET 8, WinUI 3
- Native Engine: C++, Direct3D 11 / DXGI, FFmpeg, Opus, SDL3
- Android Mobile: Flutter, C++, MediaCodec
- Web Client: WebRTC, WebCodecs, AudioWorklet

## How to Use

- Download and install the latest release from [Releases](https://github.com/OsmiumRemote/Osmium/releases) on your host and client devices, or open the [Web Client](https://web.osmium.fufu.skin) directly
- Sign in on the **Account** page with email, OAuth, QR code, or a login key to sync your device list, or enable remote access on the host
- Select an online host from your device list or enter its Device ID to connect
- Adjust resolution, frame rate, codec, mouse mode, audio, or privacy screen from the in-session control bar

## Notes

- Windows 10 2004+ or Windows 11 x64 is required; a GPU with hardware encode/decode is recommended for 4K and high frame rates
- Installing optional virtual peripheral drivers or configuring unattended system-service auto-start requires **administrator privileges**
- **Privacy Screen**, **Quick Launch**, and **Remote Power / Wake-on-LAN** require host-side permissions enabled in Settings; Wake-on-LAN also requires BIOS and NIC support
- **Do not report security vulnerabilities through public issues** — see [SECURITY.md](./SECURITY.md); for general help, see [SUPPORT.md](./SUPPORT.md)

## Supported Languages

![English](https://img.shields.io/badge/English-en--US-informational)
![简体中文](https://img.shields.io/badge/%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-zh--CN-informational)

## Contributors

<a href="https://github.com/OsmiumRemote/Osmium/graphs/contributors"><img src="https://contrib.rocks/image?repo=OsmiumRemote/Osmium" /></a>
