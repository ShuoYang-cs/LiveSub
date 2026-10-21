#!/bin/zsh
# 在登录钥匙串里创建本机自签名的代码签名证书 “LiveSub Local Signing”（只需运行一次）。
# scripts/build-app.sh 发现它就用它签名：签名身份固定，每次重新构建后系统不会再重新询问麦克风/系统录音权限。
# 证书只在本机使用，不需要设为“始终信任”。
set -euo pipefail
NAME="LiveSub Local Signing"

if security find-identity -p codesigning | grep -q "\"$NAME\""; then
  echo "✅ 已存在：$NAME"
  exit 0
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
EOF

openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$TMP/cert.cnf" \
  -keyout "$TMP/key.pem" -out "$TMP/cert.pem" 2>/dev/null
# 只用于这次导入的临时口令。
PASS=$(openssl rand -hex 16)
openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" -name "$NAME" \
  -out "$TMP/cert.p12" -passout "pass:$PASS"
# -T 允许 codesign 直接使用私钥，签名时不弹窗。
security import "$TMP/cert.p12" -k "$HOME/Library/Keychains/login.keychain-db" -P "$PASS" -T /usr/bin/codesign
echo "✅ 已创建：$NAME"
