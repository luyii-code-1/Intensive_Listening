param([Parameter(Mandatory=$true)][string]$RuntimeDirectory,[string]$ReportDirectory)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if (-not $ReportDirectory) { $ReportDirectory=Join-Path $RuntimeDirectory '..\verification' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$report=Join-Path (Resolve-Path $ReportDirectory) 'runtime.json'
$env:ILP_STANDALONE_DATA=Join-Path (Resolve-Path $ReportDirectory) 'data'
$process=Start-Process -FilePath (Join-Path $RuntimeDirectory 'IL.App.exe') -ArgumentList @('--verify-runtime',"`"$report`"") -WorkingDirectory $RuntimeDirectory -PassThru -Wait -RedirectStandardError (Join-Path $ReportDirectory 'stderr.txt') -RedirectStandardOutput (Join-Path $ReportDirectory 'stdout.txt')
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$result | ConvertTo-Json -Depth 8
if ($process.ExitCode -ne 0 -or -not $result.success) { throw 'Native runtime verification failed' }
$env:IL2_LAUNCHER_VERIFY='1'
$standalone=Start-Process -FilePath (Join-Path $ReportDirectory 'standalone-verification.exe') -Wait -PassThru
Remove-Item Env:IL2_LAUNCHER_VERIFY
if ($standalone.ExitCode -ne 0) { throw 'Standalone launcher verification failed' }
Write-Output 'Windows native runtime and independent launcher verified.'
