param([Parameter(Mandatory=$true)][string]$RuntimeDirectory,[string]$OutputDirectory = (Join-Path $PSScriptRoot '../../dist'),[string]$InnoCompiler)
$ErrorActionPreference='Stop'
if (-not $InnoCompiler) { $InnoCompiler=(Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $InnoCompiler) { $InnoCompiler='E:\dev\Inno\ISCC.exe' }
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Locate existing Inno Setup and pass -InnoCompiler.' }
$repo=Split-Path -Parent $PSScriptRoot
$identity = Get-Content -LiteralPath (Join-Path $RuntimeDirectory 'build-info.json') -Raw | ConvertFrom-Json
if ($identity.baseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Runtime build identity is invalid.' }
& $InnoCompiler "/DAppNumericVersion=$($identity.baseVersion)" "/DAppDisplayVersion=$($identity.displayVersion)" "/DArtifactVersion=$($identity.artifactVersion)" "/DReleaseDirectory=$RuntimeDirectory" "/DOutputDirectory=$OutputDirectory" (Join-Path $PSScriptRoot 'csharp-windows-installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
