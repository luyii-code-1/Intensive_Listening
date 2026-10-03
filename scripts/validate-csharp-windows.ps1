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
$env:IL2_LAUNCHER_VERIFY_REPORT=Join-Path (Resolve-Path $ReportDirectory) 'standalone.json'
try {
    $standalone=Start-Process -FilePath (Join-Path $ReportDirectory 'standalone-verification.exe') -Wait -PassThru -RedirectStandardError (Join-Path $ReportDirectory 'standalone-stderr.txt') -RedirectStandardOutput (Join-Path $ReportDirectory 'standalone-stdout.txt')
    if (Test-Path -LiteralPath $env:IL2_LAUNCHER_VERIFY_REPORT) {
        $standaloneResult=Get-Content -LiteralPath $env:IL2_LAUNCHER_VERIFY_REPORT -Raw | ConvertFrom-Json
        $standaloneResult | ConvertTo-Json -Depth 8
    }
    if (Test-Path -LiteralPath ($env:IL2_LAUNCHER_VERIFY_REPORT+'.launcher-error.txt')) {
        Get-Content -LiteralPath ($env:IL2_LAUNCHER_VERIFY_REPORT+'.launcher-error.txt')
    }
    if ($standalone.ExitCode -ne 0 -or -not $standaloneResult.success) { throw "Standalone launcher verification failed (exit $($standalone.ExitCode))" }
}
finally { Remove-Item Env:IL2_LAUNCHER_VERIFY; Remove-Item Env:IL2_LAUNCHER_VERIFY_REPORT }
Write-Output 'Windows native runtime and independent launcher verified.'
