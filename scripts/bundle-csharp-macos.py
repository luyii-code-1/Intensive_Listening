#!/usr/bin/env python3
"""Bundle locally installed FFmpeg and official VLC with relocatable Mach-O dependencies."""
import hashlib, json, os, pathlib, plistlib, re, shutil, subprocess, sys, zipfile, uuid

app, vlc, ffmpeg = map(pathlib.Path, sys.argv[1:4])
mac = app / 'Contents/MacOS'
(mac / 'installation-id.txt').write_text(str(uuid.uuid4()))
resources = app / 'Contents/Resources'
resources.mkdir(parents=True, exist_ok=True)
licenses = resources / 'licenses'
licenses.mkdir(exist_ok=True)
if (mac / 'libvlc').exists():
    shutil.rmtree(mac / 'libvlc')
shutil.copytree(vlc / 'Contents/MacOS/lib', mac / 'libvlc/lib', symlinks=True)
shutil.copytree(vlc / 'Contents/MacOS/plugins', mac / 'libvlc/plugins', symlinks=True)
# The VLC application's UI module needs its updater framework. LibVLC uses the host UI.
(mac / 'libvlc/plugins/libmacosx_plugin.dylib').unlink(missing_ok=True)
(mac / 'libvlc/plugins/plugins.dat').unlink(missing_ok=True)
shutil.copy(vlc / 'Contents/Resources/README', licenses / 'VLC-README.txt')
for source in ['LICENSE', 'THIRD_PARTY_NOTICES.md', 'assets/legal/LGPL-2.1.txt']:
    shutil.copy(source, licenses / pathlib.Path(source).name)

# Official ARMS C SDK, pinned independently of the Windows package.
arms_archive = pathlib.Path('artifacts/native/AlibabaCloud_RUM_macOS.zip')
assert hashlib.sha256(arms_archive.read_bytes()).hexdigest() == 'b837d135b16eb1ec087cfba8796be2425765cb39e16b161d663364135e59085e', 'ARMS macOS checksum mismatch'
arms = mac / 'arms'
arms.mkdir(exist_ok=True)
with zipfile.ZipFile(arms_archive) as sdk:
    for name in ['libalibabacloud_rum.dylib', 'libcurl.dylib']:
        (arms / name).write_bytes(sdk.read('AlibabaCloud_RUM_macOS_0.4.4/lib/arm64/Release/' + name))
        (arms / name).chmod(0o755)
shutil.copy('third_party/arms/README.md', licenses / 'ARMS.md')
(licenses / 'ARMS-macOS.json').write_text(json.dumps({'version': '0.4.4', 'source': 'https://rum-sdk.oss-cn-hangzhou.aliyuncs.com/native/AlibabaCloud_RUM_macOS.zip', 'sha256': 'b837d135b16eb1ec087cfba8796be2425765cb39e16b161d663364135e59085e'}, indent=2))

# Homebrew dependencies are absolute. Copy their closure and rewrite each load command.
ffmpeg = ffmpeg.resolve()
(mac / 'ffmpeg').unlink(missing_ok=True)
shutil.copy2(ffmpeg, mac / 'ffmpeg')
(mac / 'ffmpeg').chmod(0o755)
fflib = mac / 'ffmpeg-libs'
if fflib.exists():
    shutil.rmtree(fflib)
fflib.mkdir()
queue = [(ffmpeg, mac / 'ffmpeg')]
seen = {}
formulas = {}
while queue:
    source, target = queue.pop(0)
    deps = subprocess.check_output(['otool', '-L', str(source)], text=True).splitlines()[1:]
    for line in deps:
        dep = line.strip().split(' (')[0]
        if not dep.startswith('/opt/homebrew/'):
            continue
        real = pathlib.Path(dep).resolve()
        if real == source:  # dylib's own install name
            continue
        name = real.name
        if name in seen and seen[name] != real:
            raise RuntimeError(f'Dependency name collision: {name}')
        if name not in seen:
            seen[name] = real
            copied = fflib / name
            shutil.copy2(real, copied)
            copied.chmod(0o644)
            queue.append((real, copied))
            parts = real.parts
            if 'Cellar' in parts:
                index = parts.index('Cellar')
                formula = pathlib.Path(*parts[:index+3])
                formulas[str(formula)] = formula
        relative = os.path.relpath(fflib / name, target.parent)
        subprocess.run(['install_name_tool', '-change', dep, '@loader_path/' + relative, str(target)], check=True)
    if target.suffix == '.dylib':
        subprocess.run(['install_name_tool', '-id', '@loader_path/' + target.name, str(target)], check=True)
formulas[str(ffmpeg.parent.parent)] = ffmpeg.parent.parent
for formula in formulas.values():
    destination = licenses / 'homebrew' / formula.parent.name / formula.name
    destination.mkdir(parents=True, exist_ok=True)
    for p in formula.iterdir():
        if p.is_file() and (p.name.startswith(('COPYING', 'LICENSE', 'README', 'INSTALL_RECEIPT', 'sbom'))):
            shutil.copy(p, destination / p.name)

# VLC ships its codec dependencies within the plugins; remove dependence on host rpaths.
vlclib = mac / 'libvlc/lib'
for p in (mac / 'libvlc').rglob('*.dylib'):
    if p.is_symlink():
        continue
    for line in subprocess.check_output(['otool', '-L', str(p)], text=True).splitlines()[1:]:
        dep = line.strip().split(' (')[0]
        if dep.startswith('@rpath/'):
            candidate = vlclib / pathlib.Path(dep).name
            if not candidate.exists():
                candidate = p.parent / pathlib.Path(dep).name
            if not candidate.exists():
                raise RuntimeError(f'VLC dependency missing: {dep}')
            subprocess.run(['install_name_tool', '-change', dep, '@loader_path/' + os.path.relpath(candidate, p.parent), str(p)], check=True)

native = []
minimum = (14, 0)
for p in mac.rglob('*'):
    if not p.is_file() or p.is_symlink():
        continue
    kind = subprocess.check_output(['file', '-b', str(p)], text=True)
    if 'Mach-O' not in kind:
        continue
    if 'arm64' not in kind:
        raise RuntimeError(f'Non ARM64 native file: {p}')
    commands = subprocess.check_output(['otool', '-l', str(p)], text=True)
    for version in re.findall(r'\bminos (\d+(?:\.\d+)+)', commands):
        minimum = max(minimum, tuple(map(int, version.split('.'))))
    deps = subprocess.check_output(['otool', '-L', str(p)], text=True)
    if '/opt/homebrew/' in deps:
        raise RuntimeError(f'External Homebrew dependency: {p}')
    native.append(p)
plist = {
    'CFBundleName': 'Intensive Listening', 'CFBundleDisplayName': 'Intensive Listening 2 Resonance',
    'CFBundleExecutable': 'IL.App', 'CFBundleIdentifier': 'com.luyii.intensivelistening.preview',
    'CFBundlePackageType': 'APPL', 'CFBundleInfoDictionaryVersion': '6.0',
    'CFBundleShortVersionString': '2.0.0', 'CFBundleVersion': '2.0.0',
    'CFBundleIconFile': 'AppIcon.icns', 'NSHighResolutionCapable': True,
    'NSPrincipalClass': 'NSApplication', 'LSApplicationCategoryType': 'public.app-category.education',
    'LSMinimumSystemVersion': '.'.join(map(str, minimum)),
}
with (app / 'Contents/Info.plist').open('wb') as f:
    plistlib.dump(plist, f)
# Convert the existing application icon into the macOS icon container.
iconset = resources / 'AppIcon.iconset'
iconset.mkdir()
png = resources / 'source.png'
subprocess.run(['sips', '-s', 'format', 'png', 'assets/app_icon.ico', '--out', str(png)], check=True, stdout=subprocess.DEVNULL)
for size in [16, 32, 128, 256, 512]:
    for scale in [1, 2]:
        label = f'icon_{size}x{size}' + ('@2x' if scale == 2 else '') + '.png'
        subprocess.run(['sips', '-z', str(size*scale), str(size*scale), str(png), '--out', str(iconset/label)], check=True, stdout=subprocess.DEVNULL)
subprocess.run(['iconutil', '-c', 'icns', str(iconset)], check=True)
shutil.rmtree(iconset)
png.unlink()
signing = [p for p in mac.rglob('*') if p.is_file() and not p.is_symlink() and p.name != 'IL.App']
for p in signing:
    subprocess.run(['codesign', '--force', '--sign', '-', str(p)], check=True, stdout=subprocess.DEVNULL)
notice = {'architecture': 'arm64', 'minimumMacOS': plist['LSMinimumSystemVersion'],
          'vlc': '3.0.23', 'vlcSource': 'https://get.videolan.org/vlc/3.0.23/',
          'ffmpeg': subprocess.check_output([str(mac/'ffmpeg'), '-version'], text=True).splitlines()[0],
          'homebrewDependencies': sorted(formulas), 'signing': 'ad-hoc; development build'}
(licenses / 'macos-native-dependencies.json').write_text(json.dumps(notice, indent=2))
subprocess.run(['codesign', '--force', '--sign', '-', str(app)], check=True)
subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
manifest = {str(p.relative_to(app)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(app.rglob('*')) if p.is_file()}
app.with_suffix('.manifest.json').write_text(json.dumps(manifest, indent=2))
print(json.dumps(notice, indent=2))
