#!/bin/zsh
# 构建 LiveSub.app（只需要 Xcode 命令行工具）。
#   scripts/build-app.sh            构建到 build/LiveSub.app
#   scripts/build-app.sh --install  另外复制到 ~/Applications 并打开
set -euo pipefail
cd "$(dirname "$0")/.."

export LIVESUB_SDK_VERSION="$(xcrun --show-sdk-version)"
swift build -c release --product LiveSub

APP="build/LiveSub.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$(swift build -c release --show-bin-path)/LiveSub" "$APP/Contents/MacOS/LiveSub"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# 界面文字：中文、英文对照表与本地化的应用名称、权限说明。
cp -R Resources/*.lproj "$APP/Contents/Resources/"
date "+%Y-%m-%d %H:%M:%S" > "$APP/Contents/Resources/BuildInfo.txt"

# 签名：有本机证书（scripts/make-signing-cert.sh 创建）就用它，签名身份固定，重新构建后不会再询问麦克风/录音权限；
# 没有就用 ad-hoc 签名（每次构建签名都会变，系统会重新询问权限）。
IDENTITY="LiveSub Local Signing"
if security find-identity -p codesigning | grep -q "\"$IDENTITY\""; then
  codesign --force --sign "$IDENTITY" --identifier com.livesub.app "$APP"
else
  echo "⚠️  没有找到“$IDENTITY”证书，使用 ad-hoc 签名（运行 scripts/make-signing-cert.sh 可以避免每次重新授权）"
  codesign --force --sign - --identifier com.livesub.app "$APP"
fi
echo "✅ 已生成 $APP"

if [[ "${1:-}" == "--install" ]]; then
  mkdir -p ~/Applications
  rm -rf ~/Applications/LiveSub.app
  cp -R "$APP" ~/Applications/
  echo "✅ 已安装到 ~/Applications/LiveSub.app"
  open ~/Applications/LiveSub.app
fi
