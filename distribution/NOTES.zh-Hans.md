本版包含更新下载取消、Windows 安装版与开机启动，以及按需双向文件传输。

### Windows 首次改用安装版

下载 windows-x64-Setup.exe。安装时在 Keep portable settings 页面选择旧便携版的文件夹（内有 Unictrl.exe 和 Data/settings.json），导入现有配对及设置。旧目录保留；已有安装配置先备份。默认安装到 %LOCALAPPDATA%\Programs\Unictrl，也可选目录；配置独立保存在 %LOCALAPPDATA%\Unictrl。此后安装版自动更新在原路径升级。便携版仍可用，但 ZIP 更新不会自动转安装版。

安装版及便携版均可开关 Start at login，使用当前用户启动项；便携版启用后勿移动程序目录。

### 文件传输

两端都升级至 0.6.0，并在 Shared clipboard 开启 Share files。

| 入口 | 限制 | 开始传输 |
|---|---|---|
| 复制文件 / 跨端粘贴 | 整批 100 MiB | 在对端按 Ctrl+V / ⌘V |
| 拖拽 / Choose files | 每文件 1 GiB、整批 4 GiB | 对端点 Receive files |

最多 32 个普通文件，文件夹先压缩 ZIP；只复制，不删除源文件。复制时仅发送清单。粘贴触发后台下载，完成后写入真实本机文件列表；目标应用仍在前台则继续粘贴。若切换应用，可用 Copy received files；系统菜单粘贴不触发下载，可先点 Receive files。

默认限速 10 MiB/s，可选 1 / 5 / 10 / 20 / 50 / 100 MiB/s，双方较低值生效。显示进度并可取消。默认接收到 Downloads/Unictrl Received/Received-UUID，不覆盖已有文件。重试从头开始，暂不支持断点续传。

已配对设备使用一次性票据及独立加密通道；文件分块写盘并校验 SHA-256，不将大文件整体读入内存。取消、断线或校验失败清理未完成数据。新复制的清单不会被旧下载完成事件覆盖；取消后的旧连接消息不会重启传输。

### 软件更新

Mac / Windows 均显示下载进度并提供 Cancel download，取消会清理未完成下载。安装版下载经签名清单校验的安装程序，退出后原路径升级并保留配置。Mac 沿用固定个人自签名证书；未经 Apple 公证，实际系统权限连续性仍需本机升级验证。

原有双向文字、图片、四方向布局及签名更新功能保留。Windows 10/11 x64；Mac Apple 芯片 / Intel，macOS 15+。

### 验证

Mac：66 项单元测试；独立原生文字、图片、文件复制与按需接收、传输中取消、较新剪贴板保护测试。Windows：56 项单元测试；原生文字/图片与文件列表，含 1 GiB 加密流式传输、接收校验、确认接收、取消与旧票据拒绝；安装、配置迁移、同路径升级、开机启动和更新取消/恢复检查。两台实际电脑及实际 Wi-Fi 速度仍需用户复测。Windows 包仅在云端构建发布，未下载到此 Mac。

开发分支保留：feature/windows-installer（0.5.0），feature/clipboard-files（0.6.0）。
