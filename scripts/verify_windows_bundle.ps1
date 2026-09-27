param(
  [Parameter(Mandatory = $true)]
  [string]$ReleaseDirectory,

  [switch]$RequireArms
)

$requiredFiles = @(
  'Intensive Listening.exe',
  'flutter_windows.dll',
  'media_kit_libs_windows_audio_plugin.dll',
  'libmpv-2.dll',
  'ffmpeg.exe',
  'msvcp140.dll',
  'vcruntime140.dll',
  'vcruntime140_1.dll',
  'data\app.so',
  'data\icudtl.dat',
  'data\tools\lesson_player_launcher.exe',
  'data\flutter_assets\assets\fonts\SourceHanSansCN-Regular.otf',
  'data\flutter_assets\assets\fonts\SourceHanSansCN-Medium.otf',
  'data\flutter_assets\assets\fonts\SourceHanSansCN-Bold.otf',
  'licenses\ffmpeg\LICENSE',
  'licenses\intensive-listening\LICENSE',
  'licenses\source-han-sans\LICENSE.txt',
  'licenses\THIRD_PARTY_NOTICES.md'
)
if ($RequireArms) { $requiredFiles += 'alibabacloud_rum.dll' }

$missingFiles = @(
  foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory $relativePath) -PathType Leaf)) {
      $relativePath
    }
  }
)

if ($missingFiles.Count -gt 0) {
  Write-Error "Windows release is incomplete. Missing: $($missingFiles -join ', ')"
  exit 1
}

if ($RequireArms) {
  $arms = Join-Path $ReleaseDirectory 'alibabacloud_rum.dll'
  $expectedHash = '33CEC949309F8025BE35FF19D7AE7F7BFC0F59A9FF0BEF3E05C40460F0BF6E8D'
  if ((Get-FileHash -LiteralPath $arms -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Windows ARMS SDK DLL checksum mismatch.'
  }
}

Write-Output "Windows runtime bundle verified ($($requiredFiles.Count) required files)."
