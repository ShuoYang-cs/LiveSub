#!/bin/zsh
# 打包发布到 GitHub Releases 的 macOS 版：Apple 芯片和 Intel 通用的 LiveSub.app，做成 dmg 和 zip。
#   scripts/package-release.sh            生成 build/LiveSub-<版本>-macOS.dmg 和 .zip（版本取自 Resources/Info.plist）
#   scripts/package-release.sh --dmg-only 不重新构建，用现成的 build/LiveSub.app 只做 dmg
# 没有苹果开发者证书，用本机证书“LiveSub Local Signing”签名（scripts/make-signing-cert.sh）：
# 用户第一次打开时要在“系统设置 › 隐私与安全性”里点“仍要打开”。以后的版本必须用同一张证书签名，
# 系统才会认作同一个 App，更新后不重新询问麦克风和录音权限。所以发布版只在有这张证书的电脑上打包。
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="LiveSub Local Signing"
if ! security find-identity -p codesigning | grep -q "\"$NAME\""; then
  echo "❌ 没有找到“$NAME”证书。发布版必须用它签名，否则用户每次更新都要重新授权。" >&2
  exit 1
fi

VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" Resources/Info.plist)
APP="build/LiveSub.app"

if [[ "${1:-}" != "--dmg-only" ]]; then
  scripts/build-app.sh --universal
  ZIP="build/LiveSub-$VERSION-macOS.zip"
  rm -f "$ZIP"
  # ditto 保留签名和扩展属性；zip 命令会弄坏 App 的签名。
  ditto -c -k --keepParent "$APP" "$ZIP"
  echo "✅ 已生成 $ZIP"
fi
lipo -archs "$APP/Contents/MacOS/LiveSub"
codesign --verify --strict "$APP"

# dmg：打开后是 App 和“应用程序”文件夹的快捷方式，把 App 拖过去就装好了。
DMG="build/LiveSub-$VERSION-macOS.dmg"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
ditto "$APP" "$STAGE/LiveSub.app"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "LiveSub $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
codesign --force --sign "$NAME" "$DMG"
echo "✅ 已生成 $DMG"
