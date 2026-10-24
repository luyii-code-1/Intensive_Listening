param([Parameter(Mandatory=$true)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$report = Join-Path ([IO.Path]::GetTempPath()) ("il-xaml-" + [guid]::NewGuid().ToString('N') + '.json')
try {
    $process = Start-Process -FilePath (Join-Path $runtime 'IL.App.exe') -ArgumentList @('--verify-xaml', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Published XAML verification failed.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($result.success -ne $true) { throw "Published XAML verification failed: $($result.error)" }
    Write-Output "Published XAML verified: $($result.informationalVersion)"
}
finally {
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
}
