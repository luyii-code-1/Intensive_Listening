param(
  [Parameter(Mandatory = $true)]
  [string]$ReleaseDirectory
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

Write-Output "Windows runtime bundle verified ($($requiredFiles.Count) required files)."
