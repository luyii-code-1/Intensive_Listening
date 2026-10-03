#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Build on an Apple Silicon Mac.'; exit 1; }
ffmpeg="${ILP_FFMPEG_BUILD_PATH:-$(command -v ffmpeg)}"
archive=artifacts/native/vlc-3.0.23-arm64.dmg
mount="$PWD/artifacts/native/vlc-build-arm64"
output="${1:-artifacts/build/macos-arm64/Intensive Listening 2.0.app}"
package_format="${2:-dmg}"
case "$package_format" in dmg|zip) ;; *) echo 'Package format must be dmg or zip.'; exit 1 ;; esac
mkdir -p artifacts/native artifacts/verification artifacts/build artifacts/installer
if [[ ! -f "$archive" ]]; then
  curl -fL --retry 2 https://get.videolan.org/vlc/3.0.23/macosx/vlc-3.0.23-arm64.dmg -o "$archive"
fi
python3 - "$archive" <<'PY'
import hashlib, sys
assert hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest() == 'fc6fac08d87f538517d44aca0c5e7a244b67c8c4cb589bf478363a7315fd5e0d', 'VLC checksum mismatch'
PY
hdiutil attach "$archive" -nobrowse -readonly -mountpoint "$mount"
stage=""
trap 'hdiutil detach "$mount" >/dev/null; if [[ -n "$stage" ]]; then rm -rf "$stage"; fi' EXIT
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
if [[ "$package_format" == dmg ]]; then
  stage="$(mktemp -d "$PWD/artifacts/installer/macos-stage.XXXXXX")"
  ditto "$output" "$stage/$(basename "$output")"
  ln -s /Applications "$stage/Applications"
  package=artifacts/installer/Intensive-Listening-2.0.0-osx-arm64.dmg
  hdiutil create -volname 'Intensive Listening 2 Resonance' -srcfolder "$stage" -format UDZO -fs HFS+ -ov "$package"
else
  package=artifacts/build/IL2-osx-arm64.zip
  rm -f "$package"
  ditto -c -k --sequesterRsrc --keepParent "$output" "$package"
fi
echo "$package"
