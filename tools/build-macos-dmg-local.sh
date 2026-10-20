#!/bin/sh
# Local, persistently signed DMG build with bidirectional text clipboard support.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
out="$root/artifacts"
mkdir -p "$out"
cd "$root/macos"
# The installed macOS 27 CLT lacks SwiftUIMacros; the retained SDK builds the project source.
sdk=${UNICTRL_SDK:-/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk}
for arch in arm64 x86_64; do
    swift build -c release --build-system native --sdk "$sdk" --triple "$arch-apple-macosx15.0" --scratch-path ".build-sdk26-$arch"
done
arm_bin=$(swift build -c release --build-system native --sdk "$sdk" --triple arm64-apple-macosx15.0 --scratch-path .build-sdk26-arm64 --show-bin-path)/Unictrl
intel_bin=$(swift build -c release --build-system native --sdk "$sdk" --triple x86_64-apple-macosx15.0 --scratch-path .build-sdk26-x86_64 --show-bin-path)/Unictrl
stage=$(mktemp -d "$out/dmg-stage.XXXXXX")
app="$stage/Unictrl.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
lipo -create "$arm_bin" "$intel_bin" -output "$app/Contents/MacOS/Unictrl"
cp "$root/macos/Info.plist" "$app/Contents/Info.plist"
icons="$root/assets/icons/mac"
cp "$icons/Unictrl.icns" "$app/Contents/Resources/"
cp "$icons"/menubar/*.png "$app/Contents/Resources/"
if xcrun --find actool >/dev/null 2>&1; then
    car=$(mktemp -d "$out/icon-stage.XXXXXX")
    xcrun actool "$icons/Unictrl.icon" --compile "$car" --platform macosx --minimum-deployment-target 15.0 --app-icon Unictrl --output-partial-info-plist "$car/partial.plist" --errors --warnings
    cp "$car/Assets.car" "$app/Contents/Resources/"
else
    # CFBundleIconName requires Assets.car; use the supplied ICNS without full Xcode.
    /usr/libexec/PlistBuddy -c 'Delete :CFBundleIconName' "$app/Contents/Info.plist"
fi
# Keep one local certificate across builds so the designated requirement stays stable.
signing=$(python3 "$root/tools/macos/setup-local-signing.py")
identity=$(printf '%s' "$signing" | python3 -c 'import json,sys; print(json.load(sys.stdin)["identity"])')
keychain=$(printf '%s' "$signing" | python3 -c 'import json,sys; print(json.load(sys.stdin)["keychain"])')
codesign --force --options runtime --sign "$identity" --keychain "$keychain" "$app"
codesign --verify --strict --verbose=2 "$app"
plutil -lint "$app/Contents/Info.plist"
lipo -info "$app/Contents/MacOS/Unictrl"
ln -s /Applications "$stage/Applications"
cp "$root/LICENSE" "$stage/LICENSE.txt"
cat > "$stage/安装说明.txt" <<'TEXT'
Unictrl 0.6.0 — macOS 通用版（Apple 芯片和 Intel）

安装：把 Unictrl.app 拖到 Applications 文件夹，然后从“应用程序”打开 Unictrl。
系统要求：macOS 15 或以上。

兼容 Windows 图片剪贴板版 0.4.0 及以上。0.6.0 修复 Mac 截图同时带文件标记时无法同步的问题。
在两端设置中开启 Share text clipboard，均显示 Ready 后重新复制文字，再在另一台粘贴。
图片需在两端另行开启 Share images too，支持单张静态图/截图（PNG 10 MiB、2000 万像素），约限速 1 MiB/s。
纯文本最多 UTF-8 1 MiB；等待 Ready 后粘贴，只传新复制内容。保留原有配对。
文件需两端均为 0.6.0 并开启 Share files。复制/粘贴整批限 100 MiB；在对端按 Ctrl+V / ⌘V 后下载，目标应用保持前台则完成后继续粘贴。
拖拽或选择文件每个限 1 GiB、整批 4 GiB，最多 32 个，对端点 Receive files 后才传数据。文件夹请先压缩。
限速默认 10 MiB/s，可调 1–100 MiB/s，双方较低值生效，可看进度和取消。接收至 Downloads/Unictrl Received，不覆盖旧文件。
Windows 在上、Mac 在下：在设备旁的“位置”菜单选“Windows 在 Mac 上方”。
支持拖动任意屏幕并吸附到上下左右四个方向。

自动更新：启动及每六小时后台检查、验签和下载，可显示进度和取消下载。完成后点 Restart and update，或从软件菜单退出时自动安装并重启。旧程序保留 .previous 备份，配对与设置保留。

首次使用：
1. 在“系统设置 > 隐私与安全性 > 辅助功能”中允许 Unictrl。
2. 在“系统设置 > 隐私与安全性 > 输入监控”中允许 Unictrl，按系统提示退出并重新打开应用。
3. 如果 macOS 询问本地网络访问，请允许。
4. 点菜单栏的 Unictrl 图标，选 Pair a Windows PC…。
5. Windows 端也需要安装 Unictrl 并输入配对码。

这是基于上游 v0.1.0 的本地图片剪贴板版 0.6.0，使用固定的个人自签名证书，未经过 Apple 公证。
如果从网络下载后被 macOS 阻止，请在系统设置的“隐私与安全性”中按系统提供的“仍要打开”流程处理。
从旧临时签名版本切换时仍需重新授权一次；后续保留同一证书、bundle ID 与安装路径，以验证权限连续性。
未安装完整 Xcode 时使用项目自带的 ICNS 图标。
应用上游默认启用“登录时启动”，可以在 Unictrl 设置中关闭。

源码：https://github.com/ielfstone/unictrl-oss/tree/feature/clipboard-files
上游版本：v0.1.0；本地版本：0.6.0
布局基线：db181b7e0ec03ed271dac5ed6df90b92c4d8b745
许可证：GPL-3.0；完整许可证见 LICENSE.txt。
TEXT
hdiutil create -volname 'Unictrl 0.6.0' -srcfolder "$stage" -format UDZO -ov "$out/Unictrl-0.6.0-macOS-universal.dmg"
hdiutil verify "$out/Unictrl-0.6.0-macOS-universal.dmg"
shasum -a 256 "$out/Unictrl-0.6.0-macOS-universal.dmg" > "$out/Unictrl-0.6.0-macOS-universal.dmg.sha256"
printf 'DMG: %s\nApp: %s\n' "$out/Unictrl-0.6.0-macOS-universal.dmg" "$app"
