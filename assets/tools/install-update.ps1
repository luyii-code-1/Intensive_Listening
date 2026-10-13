param([Parameter(Mandatory)][string]$Plan, [string]$Ready)
$ErrorActionPreference = 'Stop'
$updatePlan = $null
$packageLock = $null
$notification = $null
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
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $notification = New-Object System.Windows.Forms.NotifyIcon
        $notification.Icon = [System.Drawing.SystemIcons]::Information
        $notification.Text = 'Intensive Listening'
        $notification.Visible = $true
        $notification.ShowBalloonTip(10000, '您的组织正在更新您的应用', ($updatePlan.organization + ' 正在安装 Intensive Listening ' + $updatePlan.version + '，完成后自动重新启动。'), [System.Windows.Forms.ToolTipIcon]::Info)
        [System.Windows.Forms.Application]::DoEvents()
    } catch { Write-UpdateLog ('System notification unavailable: ' + $_.Exception.Message) }
    $installerLog = Join-Path $root ('installer-' + $updatePlan.id + '.log')
    $installerArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/MDNUPDATE=1', ('/LOG="' + $installerLog + '"'), ('/DIR="' + $updatePlan.appDirectory + '"'), ('/MDNDATA="' + $updatePlan.dataDirectory + '"'))
    Write-UpdateLog 'Starting the installer; Windows may request administrator permission.'
    $installerProcess = Start-Process -FilePath $package.FullName -ArgumentList $installerArgs -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    Write-UpdateLog ('Installer exit code: ' + $installerProcess.ExitCode)
    if ($installerProcess.ExitCode -notin @(0, 3010)) { throw ('安装失败，退出码 ' + $installerProcess.ExitCode) }
    @{ id=$updatePlan.id; version=$updatePlan.version; success=$true; automaticOobe=$updatePlan.automaticOobe } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    Write-UpdateLog ('Update failed: ' + $_.Exception.ToString())
    @{ id=$updatePlan.id; success=$false; message=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
} finally {
    if ($packageLock) { $packageLock.Dispose() }
    if ($notification) { $notification.Dispose() }
}
if ($updatePlan -and (Test-Path -LiteralPath $updatePlan.executable)) {
    try {
        Write-UpdateLog 'Restarting the application.'
        Start-Process -FilePath $updatePlan.executable -WorkingDirectory $updatePlan.appDirectory -WindowStyle Normal
    } catch { Write-UpdateLog ('Restart failed: ' + $_.Exception.ToString()) }
}
