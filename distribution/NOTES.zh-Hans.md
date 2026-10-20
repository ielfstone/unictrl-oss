修复 Mac → Windows 截图粘贴仍出现上次文字的问题。

部分 Mac 截图工具会同时提供 PNG/TIFF 图片和临时文件 URL。旧版过早按文件过滤，漏发实际图片。0.4.1 优先同步已有图片像素；不打开文件 URL、不读取或传输文件。纯文件复制仍不支持。

- 兼容 Windows 0.4.0，可仅升级 Mac；本页也提供版本一致的 Windows 0.4.1 便携包。
- 两端开启 Share text clipboard 和 Share images too；更新后重新截图，等待 Ready 再粘贴。
- 在 0.4.0 设置中点 Check for updates，下载完成后 Restart and update。亦可使用 DMG 覆盖应用。
- 沿用同一个人自签名证书和 bundle ID，配对与设置保留。实际系统权限连续性仍需本机升级验证。
- 保留 0.4.0 的双向文本/图片、四方向布局和已签名自动更新功能。

验证：先在独立命名剪贴板复现旧版漏发，再确认修复后 PNG/TIFF 加文件标记、文字后复制图片、多分片加密传输正常。Mac 58 项单元测试和独立原生文字/图片测试通过；Windows 51 项测试及原生剪贴板、更新成功/回滚、数据保留检查通过。尚需用户在两台实际电脑复测。

Mac 支持 Apple 芯片和 Intel，macOS 15+；个人自签名，未经 Apple 公证。Windows 包为 x64 便携版，解压运行，Data 位于 Unictrl.exe 同级。Windows 包在云端构建发布，未下载到此 Mac。

源码分支：fix/mac-image-clipboard
