Unictrl 0.2.0 — 双向纯文本剪贴板（个人 Fork 测试版）

完整解压后运行 Unictrl.exe，保留全部 DLL 与子目录。旧版升级请先退出旧程序，把原来的 Data 文件夹复制到新软件目录，配对即可保留。

两端均需更新。Mac 和 Windows 设置中开启 Share text clipboard。首次升级默认关闭；当两端均显示 Ready · text only 后，重新复制文字，再去另一台电脑粘贴。
Mac 配对多台 PC 时，请在 Windows PC 下拉框选定一台；只有一台时可用 Automatic。
支持中文、Emoji、多行纯文本，最大 UTF-8 1 MiB。不传图片、文件、历史或原有剪贴板；超限显示状态。图片计划在 0.3.0，文件在后续版本规划。
传输通过单独的加密局域网连接，不上传云端。暂停共享或断开连接会取消未完成的文本传输，键鼠连接独立。

仅提供复制内容的同步，粘贴快捷键仍沿用你当前的键盘布局。目标应用必须允许粘贴文本。
这是本地 Fork 的版本，不是原作者的官方发行。基于 GPL-3.0，完整许可证和修改源码随包提供。
源代码：https://github.com/ielfstone/unictrl-oss/tree/feature/clipboard-text
