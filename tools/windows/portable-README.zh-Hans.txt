Unictrl 0.6.0 — 双向文本和图片剪贴板（个人 Fork 测试版）

完整解压后运行 Unictrl.exe，保留全部 DLL 与子目录。旧版升级请先退出旧程序，把原来的 Data 文件夹复制到新软件目录，配对即可保留。

两端均需更新。Mac 和 Windows 设置中开启 Share text clipboard。首次升级默认关闭；当两端均显示 Ready 后，重新复制文字，再去另一台电脑粘贴。
Mac 配对多台 PC 时，请在 Windows PC 下拉框选定一台；只有一台时可用 Automatic。
支持中文、Emoji、多行纯文本，最大 UTF-8 1 MiB。两端另行开启 Share images too 后支持截图、单张静态图片，最大 10 MiB PNG / 2000 万像素。图片传输约限速 1 MiB/s，请等待 Ready 后再粘贴。复制 Finder/资源管理器里的图片文件属于文件复制，本版不传；请在预览、浏览器或图片编辑器里复制图片内容。动图、多图、文件、历史和开启前旧内容不传。图片保留透明度，目标应用须支持粘贴图片。
传输通过单独的加密局域网连接，不上传云端。暂停共享或断开连接会取消未完成的传输，键鼠连接独立。

仅提供复制内容的同步，粘贴快捷键仍沿用你当前的键盘布局。目标应用必须允许粘贴文本。
这是本地 Fork 的版本，不是原作者的官方发行。基于 GPL-3.0，完整许可证和修改源码随包提供。
源代码：https://github.com/ielfstone/unictrl-oss/tree/feature/auto-update

自动更新：启动及每六小时检查此 fork 的稳定发布，后台验签、下载。完成后点 Restart and update，或从托盘菜单退出时自动替换并重启。Data 文件夹与配对保留，旧程序保存在同级 .previous 目录。可在 Software updates 中关闭自动检查。仅更新到本 fork 发布且验签成功的版本。
