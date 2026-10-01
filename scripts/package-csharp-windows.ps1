param([Parameter(Mandatory=$true)][string]$RuntimeDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$InnoCompiler)
$ErrorActionPreference='Stop'
if (-not $InnoCompiler) { $InnoCompiler=(Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $InnoCompiler) { $InnoCompiler='E:\dev\Inno\ISCC.exe' }
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Locate existing Inno Setup and pass -InnoCompiler.' }
$repo=Split-Path -Parent $PSScriptRoot
& $InnoCompiler "/DReleaseDirectory=$RuntimeDirectory" "/DOutputDirectory=$OutputDirectory" (Join-Path $PSScriptRoot 'csharp-windows-installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
