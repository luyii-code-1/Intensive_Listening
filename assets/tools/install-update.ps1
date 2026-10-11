param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$updatePlan = Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json
$resultPath = Join-Path $updatePlan.dataDirectory 'updates/result.json'
try {
    $parent = Get-Process -Id $updatePlan.processId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().ToString('o') -eq ([DateTime]$updatePlan.startedAt).ToUniversalTime().ToString('o')) {
        if (-not $parent.WaitForExit(120000)) { throw '应用未完成关闭。' }
    }
    $package = Get-Item -LiteralPath $updatePlan.installer
    $packageLock = [IO.File]::Open($package.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if ($package.Length -ne $updatePlan.size -or (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash -ne $updatePlan.sha256) { throw '安装包校验失败。' }
    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/MDNUPDATE=1', ('/DIR="' + $updatePlan.appDirectory + '"'), ('/MDNDATA="' + $updatePlan.dataDirectory + '"'))
    $installerProcess = Start-Process -FilePath $package.FullName -ArgumentList $args -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    if ($installerProcess.ExitCode -notin @(0, 3010)) { throw ('安装失败，退出码 ' + $installerProcess.ExitCode) }
    @{ id=$updatePlan.id; version=$updatePlan.version; success=$true; automaticOobe=$updatePlan.automaticOobe } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    @{ id=$updatePlan.id; success=$false; message=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
}
if ($packageLock) { $packageLock.Dispose() }
if (Test-Path -LiteralPath $updatePlan.executable) { Start-Process -FilePath $updatePlan.executable -WorkingDirectory $updatePlan.appDirectory -WindowStyle Normal }
