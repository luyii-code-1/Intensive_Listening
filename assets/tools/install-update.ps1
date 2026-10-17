param([Parameter(Mandatory)][string]$Plan, [string]$Ready)
$ErrorActionPreference = 'Stop'
$updatePlan = $null
$packageLock = $null
$copiedLock = $null
$installed = $false
$root = Split-Path -Parent $Plan
$resultPath = Join-Path $root 'result.json'
$logPath = Join-Path $root 'install-update.log'
function Write-UpdateLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Value ((Get-Date -Format o) + ' ' + $Message) -Encoding UTF8
}
try {
    Write-UpdateLog 'Helper starting.'
    $updatePlan = Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-UpdateLog ('Task ' + $updatePlan.id + ': waiting for the application to exit.')
    if ($Ready) { Set-Content -LiteralPath $Ready -Value 'ready' -Encoding UTF8 }
    $parent = Get-Process -Id $updatePlan.processId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().ToString('o') -eq ([DateTime]$updatePlan.startedAt).ToUniversalTime().ToString('o')) {
        if (-not $parent.WaitForExit(120000)) { throw '应用未完成关闭。' }
    }
    Write-UpdateLog 'Application stopped; verifying the installer.'
    $package = Get-Item -LiteralPath $updatePlan.installer
    $packageLock = [IO.File]::Open($package.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash($packageLock)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($package.Length -ne $updatePlan.size -or $digest -ne $updatePlan.sha256) { throw '安装包校验失败。' }
    $installerLog = Join-Path $root ('installer-' + $updatePlan.id + '.log')
    $installerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/MDNUPDATE=1', ('/LOG="' + $installerLog + '"'), ('/DIR="' + $updatePlan.appDirectory + '"'), ('/MDNDATA="' + $updatePlan.dataDirectory + '"'))
    Write-UpdateLog 'Starting the installer; Windows may request administrator permission.'
    $updateExecutable = Join-Path $root 'Intensive Listening更新.exe'
    Copy-Item -LiteralPath $package.FullName -Destination $updateExecutable -Force
    $copiedLock = [IO.File]::Open($updateExecutable,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $copiedDigest = [BitConverter]::ToString($sha.ComputeHash($copiedLock)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($copiedLock.Length -ne $updatePlan.size -or $copiedDigest -ne $updatePlan.sha256) { throw '安装包校验失败。' }
    $installerProcess = Start-Process -FilePath $updateExecutable -ArgumentList $installerArgs -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    Write-UpdateLog ('Installer exit code: ' + $installerProcess.ExitCode)
    if ($installerProcess.ExitCode -notin @(0, 3010)) { throw ('安装失败，退出码 ' + $installerProcess.ExitCode) }
    $installed = $true
    @{ id=$updatePlan.id; version=$updatePlan.version; success=$true; automaticOobe=$updatePlan.automaticOobe } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    Write-UpdateLog ('Update failed: ' + $_.Exception.ToString())
    @{ id=$updatePlan.id; version=$updatePlan.version; success=$false; message=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} finally {
    if ($packageLock) { $packageLock.Dispose() }
    if ($copiedLock) { $copiedLock.Dispose() }
}
if ($updatePlan -and (Test-Path -LiteralPath $updatePlan.executable)) {
    if ($updatePlan.feedback) {
        try { Start-Process -FilePath $updatePlan.executable -ArgumentList @('--update-feedback-only', ('"' + $updatePlan.dataDirectory + '"')) -WorkingDirectory $updatePlan.appDirectory -WindowStyle Hidden -Wait }
        catch { Write-UpdateLog ('Feedback process failed: ' + $_.Exception.ToString()) }
    }
    $background = $installed -and $updatePlan.PSObject.Properties['background'] -and $updatePlan.background
    if (-not $background -and $updatePlan.PSObject.Properties['reopen'] -and -not $updatePlan.reopen) { exit }
    try {
        Write-UpdateLog 'Restarting the application.'
        if ($background) {
            Start-Process -FilePath $updatePlan.executable -ArgumentList '--background-after-update' -WorkingDirectory $updatePlan.appDirectory -WindowStyle Hidden
        } else {
            Start-Process -FilePath $updatePlan.executable -WorkingDirectory $updatePlan.appDirectory -WindowStyle Hidden
        }
    } catch { Write-UpdateLog ('Restart failed: ' + $_.Exception.ToString()) }
}
