#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
output="${1:-artifacts/build/IL2-win-x64}"
mkdir -p "$output/tools" artifacts/native
output="$(cd "$output" && pwd)"
dotnet test IL2.slnx -c Release --nologo
dotnet publish src/IL.App/IL.App.csproj -c Release -r win-x64 --self-contained true -o "$output" --nologo
dotnet publish src/IL.Launcher/IL.Launcher.csproj -c Release -r win-x64 --self-contained true -o artifacts/build/launcher --nologo
cp artifacts/build/launcher/lesson_player_launcher.exe "$output/tools/lesson_player_launcher.exe"
ffmpeg="third_party/ffmpeg/ffmpeg.exe"
if [[ ! -f "$ffmpeg" ]]; then
  curl -fL --retry 2 https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip -o artifacts/native/ffmpeg.zip
  python3 - <<'PY'
import zipfile,pathlib
with zipfile.ZipFile('artifacts/native/ffmpeg.zip') as z:
    name=next(n for n in z.namelist() if n.endswith('/bin/ffmpeg.exe'))
    pathlib.Path('third_party/ffmpeg').mkdir(parents=True,exist_ok=True)
    pathlib.Path('third_party/ffmpeg/ffmpeg.exe').write_bytes(z.read(name))
PY
fi
python3 - "$ffmpeg" <<'PY'
import hashlib,sys
assert hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest()=='72a489eccd008c2ec2c0a5856c5c75bc3d8bbfa90166c4566865c246445e6aa3','FFmpeg checksum mismatch'
PY
cp "$ffmpeg" "$output/ffmpeg.exe"
if [[ ! -f artifacts/native/AlibabaCloud_RUM_Windows.zip ]]; then
  curl -fL --retry 2 https://rum-sdk.oss-cn-hangzhou.aliyuncs.com/native/AlibabaCloud_RUM_Windows.zip -o artifacts/native/AlibabaCloud_RUM_Windows.zip
fi
mkdir -p "$output/licenses"
cp LICENSE "$output/licenses/Intensive-Listening-LICENSE.txt"
cp third_party/arms/README.md "$output/licenses/ARMS.md"
cp third_party/ffmpeg/*LICENSE* "$output/licenses/" 2>/dev/null || true
cp third_party/ffmpeg/README.md "$output/licenses/FFmpeg-README.md"
cp THIRD_PARTY_NOTICES.md "$output/licenses/THIRD_PARTY_NOTICES.md"
python3 - "$output" <<'PY'
import hashlib,sys,zipfile,pathlib,uuid
archive=pathlib.Path('artifacts/native/AlibabaCloud_RUM_Windows.zip').read_bytes()
assert hashlib.sha256(archive).hexdigest()=='bfa05c0718a57eb7e94c9494499bd3c84305a3cc39b76d6a9096667553928994','ARMS archive checksum mismatch'
with zipfile.ZipFile('artifacts/native/AlibabaCloud_RUM_Windows.zip') as z:
    name=next(n for n in z.namelist() if n.endswith('bin/x86_64/Release/alibabacloud_rum.dll'))
    data=z.read(name)
    assert hashlib.sha256(data).hexdigest()=='33cec949309f8025be35ff19d7ae7f7bfc0f59a9ff0bef3e05c40460f0bf6e8d','ARMS DLL checksum mismatch'
    pathlib.Path(sys.argv[1],'alibabacloud_rum.dll').write_bytes(data)
root=pathlib.Path(sys.argv[1])
(root/'installation-id.txt').write_text(str(uuid.uuid4()))
for relative in ['IL.App.exe','libvlc/win-x64/libvlc.dll','libvlc/win-x64/libvlccore.dll','tools/lesson_player_launcher.exe','ffmpeg.exe','assets/legal/eula_zh_cn.txt','assets/legal/privacy_zh_cn.txt']:
    assert (root/relative).is_file(),f'Missing runtime dependency: {relative}'
assert (root/'libvlc/win-x64/plugins').is_dir(),'Missing VLC plugins'
manifest={str(p.relative_to(root)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob('*')) if p.is_file() and p.name!='manifest.json'}
import json
(root/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
PY
python3 - "$output" <<'PY'
import pathlib,sys,zipfile,hashlib
root=pathlib.Path(sys.argv[1]);target=root.with_suffix('.zip')
with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=5) as z:
    for p in sorted(root.rglob('*')):
        if p.is_file():z.write(p,p.relative_to(root))
target.with_suffix('.zip.sha256').write_text(hashlib.sha256(target.read_bytes()).hexdigest()+'  '+target.name+'\n')
print(target)
PY
