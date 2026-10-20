#!/bin/sh
# Local, ad-hoc-signed DMG build with bidirectional text clipboard support.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
out="$root/artifacts"
mkdir -p "$out"
cd "$root/macos"
# The installed macOS 27 CLT lacks SwiftUIMacros; the retained SDK builds the unmodified source.
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
codesign --force --options runtime --sign - "$app"
codesign --verify --strict --verbose=2 "$app"
plutil -lint "$app/Contents/Info.plist"
lipo -info "$app/Contents/MacOS/Unictrl"
ln -s /Applications "$stage/Applications"
cp "$root/LICENSE" "$stage/LICENSE.txt"
cat > "$stage/安装说明.txt" <<'TEXT'
Unictrl 0.3.0 — macOS 通用版（Apple 芯片和 Intel）

安装：把 Unictrl.app 拖到 Applications 文件夹，然后从“应用程序”打开 Unictrl。
系统要求：macOS 15 或以上。

Windows 端也需更新到图片剪贴板版 0.3.0。
在两端设置中开启 Share text clipboard，均显示 Ready 后重新复制文字，再在另一台粘贴。
图片需在两端另行开启 Share images too，支持单张静态图/截图（PNG 10 MiB、2000 万像素），约限速 1 MiB/s。
纯文本最多 UTF-8 1 MiB；等待 Ready 后粘贴，只传新复制内容，不传文件或旧剪贴板。保留原有配对，不需要重新配对。
Windows 在上、Mac 在下：在设备旁的“位置”菜单选“Windows 在 Mac 上方”。
支持拖动任意屏幕并吸附到上下左右四个方向。

首次使用：
1. 在“系统设置 > 隐私与安全性 > 辅助功能”中允许 Unictrl。
2. 在“系统设置 > 隐私与安全性 > 输入监控”中允许 Unictrl，按系统提示退出并重新打开应用。
3. 如果 macOS 询问本地网络访问，请允许。
4. 点菜单栏的 Unictrl 图标，选 Pair a Windows PC…。
5. Windows 端也需要安装 Unictrl 并输入配对码。

这是基于上游 v0.1.0 的本地图片剪贴板版 0.3.0，使用 ad-hoc 本地代码签名，未经过 Apple 公证。
如果从网络下载后被 macOS 阻止，请在系统设置的“隐私与安全性”中按系统提供的“仍要打开”流程处理。
重新编译后签名可能变化，届时可能需要重新授予辅助功能或输入监控权限。
未安装完整 Xcode 时使用项目自带的 ICNS 图标。
应用上游默认启用“登录时启动”，可以在 Unictrl 设置中关闭。

源码：https://github.com/ielfstone/unictrl-oss/tree/feature/clipboard-images
上游版本：v0.1.0；本地版本：0.3.0
布局基线：db181b7e0ec03ed271dac5ed6df90b92c4d8b745
许可证：GPL-3.0；完整许可证见 LICENSE.txt。
TEXT
hdiutil create -volname 'Unictrl 0.3.0' -srcfolder "$stage" -format UDZO -ov "$out/Unictrl-0.3.0-macOS-universal.dmg"
hdiutil verify "$out/Unictrl-0.3.0-macOS-universal.dmg"
shasum -a 256 "$out/Unictrl-0.3.0-macOS-universal.dmg" > "$out/Unictrl-0.3.0-macOS-universal.dmg.sha256"
printf 'DMG: %s\nApp: %s\n' "$out/Unictrl-0.3.0-macOS-universal.dmg" "$app"
