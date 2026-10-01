#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Build on an Apple Silicon Mac.'; exit 1; }
ffmpeg="${ILP_FFMPEG_BUILD_PATH:-$(command -v ffmpeg)}"
archive=artifacts/native/vlc-3.0.23-arm64.dmg
mount="$PWD/artifacts/native/vlc-build-arm64"
output="${1:-artifacts/Intensive Listening 2.0.app}"
mkdir -p artifacts/native artifacts/verification
if [[ ! -f "$archive" ]]; then
  curl -fL --retry 2 https://get.videolan.org/vlc/3.0.23/macosx/vlc-3.0.23-arm64.dmg -o "$archive"
fi
python3 - "$archive" <<'PY'
import hashlib, sys
assert hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest() == 'fc6fac08d87f538517d44aca0c5e7a244b67c8c4cb589bf478363a7315fd5e0d', 'VLC checksum mismatch'
PY
hdiutil attach "$archive" -nobrowse -readonly -mountpoint "$mount"
trap 'hdiutil detach "$mount" >/dev/null' EXIT
dotnet test IL2.slnx -c Release --nologo
# Clean only the generated application output selected for this build.
if [[ -e "$output" ]]; then mv "$output" "$output.previous-$(date +%s)"; fi
mkdir -p "$output/Contents/MacOS"
dotnet publish src/IL.App/IL.App.csproj -c Release -r osx-arm64 --self-contained true -o "$output/Contents/MacOS" --nologo
if [[ ! -f artifacts/native/AlibabaCloud_RUM_macOS.zip ]]; then
  curl -fL --retry 2 https://rum-sdk.oss-cn-hangzhou.aliyuncs.com/native/AlibabaCloud_RUM_macOS.zip -o artifacts/native/AlibabaCloud_RUM_macOS.zip
fi
python3 scripts/bundle-csharp-macos.py "$output" "$mount/VLC.app" "$ffmpeg"
"$output/Contents/MacOS/IL.App" --verify-runtime "$PWD/artifacts/verification/macos-arm64.json"
zip=artifacts/IL2-osx-arm64.zip
rm -f "$zip"
ditto -c -k --sequesterRsrc --keepParent "$output" "$zip"
shasum -a 256 "$zip" > "$zip.sha256"
echo "$zip"
