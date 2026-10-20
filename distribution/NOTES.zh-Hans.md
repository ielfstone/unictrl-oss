个人 fork 测试版，包含屏幕上下左右排列修复、双向文本和图片剪贴板、签名验证的自动更新。

- 文本：双方开启 Share text clipboard，最多 UTF-8 1 MiB。
- 图片：双方另开 Share images too，支持截图/单张静态图，PNG 10 MiB、2000 万像素，约限速 1 MiB/s。等待 Ready 再粘贴；不包含文件传输。
- 自动更新：启动及每六小时检查本 fork 稳定发布，验签后后台下载；点 Restart and update 或从软件菜单退出时安装并重启，保留配对与设置。旧程序留作 .previous 备份。
- Mac：通用 DMG（macOS 15+，Apple 芯片/Intel），固定个人自签名，未经过 Apple 公证。从旧 ad-hoc 版本切换仍需授权一次；0.3.0 与 0.4.0 已验证同一签名身份，实际权限保留请用覆盖升级确认。
- Windows：完整解压便携 ZIP 后运行 Unictrl.exe。首次手动升级先退出旧程序，把旧 Data 文件夹复制到新目录；后续自动更新会保留 Data。

验证：Mac 58 项单元/更新测试，以及两个独立原生文本/图片往返测试；Windows 51 项测试、原生剪贴板往返和更新安装/恢复自检。

Mac 私钥仅保留在本机，未上传仓库或 CI。完整源码包含在 Windows 包的 Source.zip，也可下载本标签源码。
