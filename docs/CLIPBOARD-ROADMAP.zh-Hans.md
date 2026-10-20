# Fork 剪贴板版本路线图

独立于向上游提交的屏幕布局 PR。文本基线分支 `feature/clipboard-text`，图片分支 `feature/clipboard-images`，基于包含上下排列修复的提交 db181b7。

| 版本 | 范围 | 状态 |
| --- | --- | --- |
| 0.2.0 | Mac ↔ Windows 纯文本复制/粘贴 | 已交付用户试用；保留独立分支与 0.2.0 安装包 |
| 0.3.0 | 单张图片、截图、透明 PNG，独立图片开关、进度、大小/像素限制、限速 | feature/clipboard-images 实现，独立测试与构建 |
| 文件版本 | 文件/文件夹的复制粘贴、落盘目录、同名冲突、进度、取消与临时文件清理 | 仅规划；另建 feature/clipboard-files，不在文本版执行文件移动/删除 |

## 0.2.0 使用与边界

两端更新后，在设置中开启 Share text clipboard。首次升级默认关闭。Mac 配对多台 Windows 时，选择一台目标 PC。双方显示 Ready 后，只同步之后新复制的文字；开启、唤醒、重新连接不自动发送旧剪贴板。

支持 UTF-8 文本，最多 1 MiB，包含中文、Emoji、换行；文本剪切产生的内容可同步。富文本只取可用的纯文本表示。图片、文件和剪贴板历史不传。粘贴快捷键由现有键盘布局决定，不在传输过程中模拟粘贴。

状态栏提示等待、超限、忙或失败。传输未完成时先等待，过早粘贴可能仍得到对端原有剪贴板。文本版不拦截 Ctrl+V/Cmd+V，以免改变键盘输入行为。网络质量差或大量文本时，这项限制尤其需要实际双机确认。

## 文本协议

控制连接新增 0x30 能力声明 `[version=1, enabled]` 和 0x31 一次性 16 字节连接票据。只有用户选中的已配对设备、双方开启后才能连接；旧版忽略未知消息，不会启用剪贴板。

Windows 向相同 Mac 端口主动建立辅助连接，握手标记 UNICTRC1，携带 clientSalt(32)、设备 UUID(16)、票据(16)。Mac 检查当前控制会话、目标与 10 秒有效期，返回 CLIPOK01 和 serverSalt(32)。双方用原配对密钥、独立随机数和 `unictrl clipboard v1` HKDF 上下文派生新的方向密钥。双方必须交换加密 ready 证明后才能同步；票据只用于一次连接，5 秒内未证明则关闭。键鼠流量不共享该 socket 或加密计数器。

0x33 帧内为 JSON：order / begin / chunk / end / ack。ack 在原生剪贴板写入成功后返回，发送方显示 Ready，传输期间显示 Syncing text。begin 包含 UUID、UTF-8 字节长度和 SHA-256；chunk 的 offset 为字节偏移，data 为最多 8192 字节的 Base64。保留现有 16384 字节密文帧上限。接收方验证总长度、严格 UTF-8、摘要和分块顺序后才写剪贴板。文本接收缓存最多 1 MiB，10 秒没有分块进展即废弃；发送队列有界，新的复制替换旧的待发数据。

Mac 对复制事件分配单调 revision，不依赖两端时钟同步。Windows 本地待确认复制阻止较旧 Mac 数据覆盖；新本地复制或不支持的本地内容会使旧写入失效。原生适配器记录自己写入后的 changeCount/sequence number，不把该写入再发布为本地复制。日志不写入剪贴板正文。已识别的敏感/禁止监视标记会跳过，但不能保证识别所有密码来源。

## 验证与发布

Swift 与 C# 共用 tests/clipboard/text-v1.json；覆盖 Unicode、分块、上限、乱序/损坏、双向及同时复制、过期内容、防回传和不同加密域。Mac 测试使用专用命名剪贴板与真实辅助 TCP 握手。Windows --clipboard-test 在 CI 的临时桌面上测试原生监听、写入及认证连接的双向往返；该诊断会覆盖系统剪贴板，仅供 CI/开发测试使用。

文本版交付 Mac 通用 DMG 和 Windows x64 便携 ZIP；Windows 继续提供 GitHub 下载链接，不下载到当前 Mac。真实 Mac + Windows 应用间的复制粘贴由用户试用确认，再推进图片版本。

## 0.3.0 图片同步

基于 0.2.0 提交 a2cd44f，保留文本兼容。两端先开启 Share text clipboard，再单独开启 Share images too；默认不启用图片。0x32 控制消息为 `[version=1, enabled]`，只有双方声明开启后才允许 image/png。0.2.0 忽略这个新类型，继续使用原文本协议。

支持截图及应用内复制的单张静态位图。Finder/资源管理器“复制图片文件”仍属于文件传输，不触发读取磁盘文件。图片与文字同时存在时优先图片，关闭图片不会把图片的 URL 文本回退误传。多图、动图、文件列表不在本版范围。

图片统一转成 PNG，不发送原始图片元数据。上限为编码后 10 MiB、2000 万像素、每边 16384 像素；预检尺寸后才解码。begin 增加 mime、width、height，分块仍为 8192 字节，校验完整长度、SHA-256、PNG 尺寸、实际解码后再写系统剪贴板。复用独立的认证传输；图片约限速 1 MiB/s，显示准备、发送/接收百分比及 Ready。十秒没有分块进展取消缓存；两端暂停/重连都丢弃待发旧内容。

Mac 接受 PNG/TIFF，后台解码、编码，在主线程核对 changeCount 后写 PNG + TIFF。Windows 接受注册的 PNG、常见 24/32 位 CF_DIB/CF_DIBV5，使用独立 STA 监听与转换；写 PNG、带 alpha 的 CF_DIBV5 和白底兼容 CF_DIB。透明度能否显示取决于粘贴目标应用选择的格式；旧位图应用可能使用白底。格式依据 [Microsoft 剪贴板格式](https://learn.microsoft.com/zh-cn/windows/win32/dataxchg/standard-clipboard-formats) 与 [Apple NSPasteboard](https://developer.apple.com/documentation/appkit/nspasteboard)。

两端新复制的文字/图片都会使旧传输和待执行写入失效；图片原生写入成功才发 ack。传输完后等待 Ready 再粘贴。测试包括共用 PNG/协议样例、半透明/透明像素、超限、损坏、关闭能力、文字取代图片、文本旧版模式及两个平台的原生往返。

## 文件阶段（仅规划）

独立 feature/clipboard-files。先支持显式文件复制，后考虑文件夹；不执行远端剪切源文件删除。使用受控接收目录与临时文件分块落盘，校验长度/摘要后原子完成，禁止绝对路径和父目录跳转。明确文件数、单文件/总容量、磁盘空间、同名冲突策略，提供进度、限速、取消、失败重试与过期清理。文件系统写入与原生文件剪贴板格式另行验证，不复用图片的整块内存缓存。

## 0.4.0 自动更新与权限

独立 feature/auto-update，基于已交付的图片版 afecac6。启动及每六小时自动检查个人 fork 的 GitHub Releases，设置中可关闭或手动检查。发现新版后后台下载，显示“Restart and update”；也会在下一次通过软件菜单退出时安装并重新打开。配对和共享设置保留，不在键鼠会话过程中强制退出。

更新源固定为本 fork 的 latest/download/update-v1.json；清单使用本机 RSA 私钥签署，Mac/Windows 内置公钥验签。只接受 HTTPS、本 fork 对应版本的 Release 地址、严格三段版本和递增升级。下载包必须符合签名清单中的长度与 SHA-256。Mac 额外检查完整包签名、所有架构和原有 designated requirement；Windows 对压缩路径、软链接、条目数、解压大小和程序版本进行检查。

Mac 使用持久的个人自签名证书（非 Apple Developer ID、未公证），bundle ID 为 com.enlivatech.unictrl。证书和私钥位于本机 ~/Library/Application Support/UnictrlBuild，专用钥匙串仅供本地构建；私钥不上传仓库或 CI。不要删除/重建它，否则身份会改变。不能把旧 ad-hoc 授权直接迁移；切换固定签名需手动授权一次，未来同一身份和安装路径下仍需用真实覆盖升级确认 macOS 权限连续性，系统可能另行要求安装授权。

两端将候选程序放在现有程序同一磁盘；退出后保留 .previous 备份、替换并重启。Windows 便携版复制 Data（保留备份原件），不会重新创建配对。安装移动/启动失败会恢复旧目录；不能保证处理启动数分钟后才出现的应用错误，此时保留的 .previous 可供恢复。

发布过程：先通过 Windows 构建与原生自检，记录便携 ZIP 的 SHA-256/长度；Mac 本地构建固定签名 ZIP，使用 tools/releases/sign-update-feed.py 签署清单。发布任务在 Windows runner 下载 Windows 构建产物、验证两包摘要，再向自己的 GitHub Release 发布更新清单及两端包；Windows 包不经过当前 Mac。没有通过测试的包不进入稳定更新源。

## 0.4.1 截图兼容性修复

Mac 截图工具可能同时向剪贴板提供 PNG/TIFF 像素和临时文件 URL。此前文件过滤先于图片读取，导致这些截图被静默跳过，Windows 仍保留上一次文本。现在优先同步剪贴板已有的像素；不打开文件 URL，不读取或传输文件。纯文件复制仍不支持，并显示明确状态。若 PNG 数据未能提供，尝试同一剪贴板的 TIFF 表示。

回归测试在独立命名剪贴板重现修复前漏发，验证修复后 PNG/TIFF 加文件标记、连续文字到图片、多分片图片发送；不触碰用户实际剪贴板。与 0.4.0 Windows 兼容。

## 0.5.0 安装与更新体验

- Mac/Windows 显示更新下载进度并可取消，取消会清理未完成下载。
- Windows 提供 Inno Setup 当前用户安装版，默认固定在 `%LOCALAPPDATA%\Programs\Unictrl`，可自选目录，后续升级沿用；配置独立在 `%LOCALAPPDATA%\Unictrl`。首次安装可选旧便携版所在目录，导入 `Data/settings.json` 并备份现有配置。
- 安装版更新下载验签清单中的安装程序，退出后在原路径升级。旧便携版仍使用 ZIP 原路径替换并保留 Data；首次转安装版需要运行 Setup。
- 开机启动改用 HKCU Run，安装版及便携版均可开关，不要求管理员权限。便携版启用后勿移动程序目录。

## 0.6.0 按需文件传输

双方另行启用 Share files。复制文件只发送清单，在对端 Ctrl+V / ⌘V 时才下载，完成后写入本机真实文件列表，并在目标应用仍前台时继续粘贴。系统菜单粘贴暂不触发下载，可先点 Receive files。若焦点改变，则不自动向新应用粘贴；可使用 Copy received files。

- 剪贴板：整批 100 MiB；拖拽/选择文件：每个 1 GiB、整批 4 GiB、最多 32 个。文件夹先压缩为 ZIP。只复制，不删除源文件。
- 剪贴板区域有拖拽/选文件入口，对端点击 Receive files 后才传数据。
- 限速可选 1/5/10/20/50/100 MiB/s，双方较低的值生效，默认 10 MiB/s。
- 文件通道与键鼠、文字图片分离；只允许已配对并选定的设备，使用一次性票据、新随机盐和独立 AES-GCM 密钥上下文。
- 16,300 字节分块读取和写盘，避免整文件入内存。逐文件 SHA-256 校验后整批发布至 Downloads/Unictrl Received/Received-UUID，不覆盖已有文件。
- 拒绝路径穿越、重复名称、Windows 保留名称、符号链接和超限；接收前检查空间。进度可见，可取消，断线或校验失败清理未完成目录；当前重试从头开始。
- 传输只走两台电脑的局域网连接；发布服务仅用于软件更新。
