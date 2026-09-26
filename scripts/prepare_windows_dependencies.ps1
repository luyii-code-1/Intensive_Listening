param(
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$ffmpegDirectory = Join-Path $projectRoot 'windows\third_party\ffmpeg'
$ffmpegExecutable = Join-Path $ffmpegDirectory 'ffmpeg.exe'
$archiveUrl = if ($env:ILP_FFMPEG_ARCHIVE_URL) {
  $env:ILP_FFMPEG_ARCHIVE_URL
} else {
  'https://github.com/GyanD/codexffmpeg/releases/download/9.0.1/ffmpeg-9.0.1-essentials_build.zip'
}
$expectedArchiveHash = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'
$expectedExecutableHash = '72a489eccd008c2ec2c0a5856c5c75bc3d8bbfa90166c4566865c246445e6aa3'

function Get-Sha256([string]$Path) {
  (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Ensure-LocalPubCacheAndDart {
  if (-not (Test-Path -LiteralPath 'E:\dev')) { return }
  $dartExe = 'E:\dev\flutter\bin\cache\dart-sdk\bin\dart.exe'
  $dartBak = 'E:\dev\flutter\bin\cache\dart.exe.bak'
  if ((Test-Path -LiteralPath $dartExe) -and -not (Test-Path -LiteralPath $dartBak)) {
    Copy-Item -LiteralPath $dartExe -Destination $dartBak -Force
  } elseif (-not (Test-Path -LiteralPath $dartExe) -and (Test-Path -LiteralPath $dartBak)) {
    Copy-Item -LiteralPath $dartBak -Destination $dartExe -Force
    Write-Output 'Restored dart.exe from local backup.'
  }

  $pubCache = if ($env:PUB_CACHE) { $env:PUB_CACHE } else { 'E:\dev\pub_cache' }
  $cacheDir = Join-Path $pubCache 'hosted\pub.flutter-io.cn'
  $pubDevDir = Join-Path $pubCache 'hosted\pub.dev'
  New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
  if (-not (Test-Path -LiteralPath $pubDevDir)) {
    cmd /c mklink /J "$pubDevDir" "$cacheDir" | Out-Null
  }

  $lockFile = Join-Path $projectRoot 'pubspec.lock'
  if (-not (Test-Path -LiteralPath $lockFile)) { return }
  $lockContent = Get-Content -LiteralPath $lockFile -Raw
  $pattern = '(?ms)^\s\s([a-zA-Z0-9_]+):\r?\n\s\s\s\sdependency:.*?\r?\n\s\s\s\sdescription:\r?\n\s\s\s\s\s\sname:\s*([a-zA-Z0-9_]+)\r?\n\s\s\s\s\s\ssha256:\s*"?([0-9a-fA-F]+)"?\r?\n\s\s\s\s\s\surl:\s*"?([^\r\n"]+)"?\r?\n\s\s\s\ssource:\s*hosted\r?\n\s\s\s\sversion:\s*"([^"]+)"'
  $matches = [regex]::Matches($lockContent, $pattern)
  New-Item -ItemType Directory -Force -Path 'E:\dev\tmp' | Out-Null
  foreach ($m in $matches) {
    $pkgName = $m.Groups[2].Value
    $ver = $m.Groups[5].Value
    $pkgDir = Join-Path $cacheDir "$pkgName-$ver"
    if (-not (Test-Path -LiteralPath (Join-Path $pkgDir 'pubspec.yaml'))) {
      Write-Output "Pre-fetching pub package $pkgName-$ver..."
      $tmpTgz = "E:\dev\tmp\$pkgName-$ver.tar.gz"
      try {
        $meta = Invoke-RestMethod -Uri "https://pub.flutter-io.cn/api/packages/$pkgName/versions/$ver" -UseBasicParsing
        Invoke-WebRequest -Uri $meta.archive_url -OutFile $tmpTgz -UseBasicParsing
      } catch {
        Invoke-WebRequest -Uri "https://pub.dev/api/archives/$pkgName-$ver.tar.gz" -OutFile $tmpTgz -UseBasicParsing
      }
      New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null
      & tar.exe -xzf $tmpTgz -C $pkgDir
      Remove-Item -LiteralPath $tmpTgz -Force -ErrorAction SilentlyContinue
    }
  }
}

Ensure-LocalPubCacheAndDart

if ((Test-Path -LiteralPath $ffmpegExecutable -PathType Leaf) -and -not $Force) {
  $installedHash = Get-Sha256 $ffmpegExecutable
  if ($installedHash -eq $expectedExecutableHash) {
    Write-Output 'Pinned FFmpeg dependency is already available.'
    exit 0
  }
  throw "Existing ffmpeg.exe has an unexpected SHA-256: $installedHash"
}

$tempBase = if (Test-Path -LiteralPath 'E:\dev') { 'E:\dev\tmp' } else { [IO.Path]::GetTempPath() }
$temporary = Join-Path $tempBase ('intensive-listening-ffmpeg-' + [guid]::NewGuid().ToString('N'))
$archive = Join-Path $temporary 'ffmpeg.zip'
$expanded = Join-Path $temporary 'expanded'
New-Item -ItemType Directory -Path $expanded -Force | Out-Null

try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Write-Output 'Downloading pinned FFmpeg 9.0.1 essentials build...'
  Invoke-WebRequest -Uri $archiveUrl -OutFile $archive -UseBasicParsing

  $archiveHash = Get-Sha256 $archive
  if ($archiveHash -ne $expectedArchiveHash) {
    throw "FFmpeg archive SHA-256 mismatch. Expected $expectedArchiveHash, received $archiveHash."
  }

  Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
  $candidate = Get-ChildItem -LiteralPath $expanded -Filter 'ffmpeg.exe' -File -Recurse |
    Select-Object -First 1
  if (-not $candidate) {
    throw 'The FFmpeg archive did not contain ffmpeg.exe.'
  }

  $executableHash = Get-Sha256 $candidate.FullName
  if ($executableHash -ne $expectedExecutableHash) {
    throw "ffmpeg.exe SHA-256 mismatch. Expected $expectedExecutableHash, received $executableHash."
  }

  New-Item -ItemType Directory -Path $ffmpegDirectory -Force | Out-Null
  Copy-Item -LiteralPath $candidate.FullName -Destination $ffmpegExecutable -Force
  Write-Output "FFmpeg dependency ready: $ffmpegExecutable"
} finally {
  Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
