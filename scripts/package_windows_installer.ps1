param(
  [Parameter(Mandatory = $true)]
  [string]$ReleaseDirectory,

  [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pubspec = Get-Content -LiteralPath (Join-Path $projectRoot 'pubspec.yaml') -Raw
$versionMatch = [regex]::Match($pubspec, '(?m)^version:\s*([0-9]+\.[0-9]+\.[0-9]+)(?:\+[0-9]+)?\s*$')
if (-not $versionMatch.Success) {
  throw 'pubspec.yaml must declare a three-part application version.'
}
$appVersion = $versionMatch.Groups[1].Value
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
if (-not $OutputDirectory) {
  $OutputDirectory = Join-Path (Split-Path -Parent $projectRoot) 'dist\installer'
}

& (Join-Path $PSScriptRoot 'verify_windows_bundle.ps1') -ReleaseDirectory $release

$compilerCandidates = @(
  $env:ILP_ISCC,
  'E:\dev\Inno\ISCC.exe',
  'E:\dev\Inno Setup 6\ISCC.exe',
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$compiler = $compilerCandidates |
  Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
  Select-Object -First 1
if (-not $compiler) {
  $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($command) { $compiler = $command.Source }
}
if (-not $compiler) {
  throw 'Inno Setup 6 compiler (ISCC.exe) is required on the Windows build host.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$script = Join-Path $PSScriptRoot 'windows_installer.iss'
& $compiler "/DAppVersion=$appVersion" "/DReleaseDirectory=$release" "/O$output" $script
if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }

$installer = Join-Path $output "Intensive Listening-Setup-$appVersion.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
  throw "Windows installer was not produced: $installer"
}

Write-Output "Windows installer ready: $installer"
Write-Output 'Install directory: selected during setup (default: %ProgramFiles%\Intensive Listening)'
