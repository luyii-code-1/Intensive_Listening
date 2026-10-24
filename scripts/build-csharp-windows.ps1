param([switch]$Publish, [ValidateSet("insider", "beta", "release")][string]$Channel = "insider", [string]$ReleaseTag)
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'E:\dev\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
Set-Location (Join-Path $PSScriptRoot '..')
$versionArguments = @('scripts/build_version.py', '--channel', $Channel)
if ($ReleaseTag) { $versionArguments += @('--tag', $ReleaseTag) }
& python @versionArguments
if ($LASTEXITCODE -ne 0) { throw 'Build version preparation failed' }
& "$env:DOTNET_ROOT\dotnet.exe" build IL2.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& "$env:DOTNET_ROOT\dotnet.exe" test IL2.slnx -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($Publish) {
    & "$env:DOTNET_ROOT\dotnet.exe" build src/IL.App/IL.App.csproj -t:Rebuild -c Release -r win-x64 --self-contained true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish rebuild failed' }
    & "$env:DOTNET_ROOT\dotnet.exe" publish src/IL.App/IL.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/build/win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    & (Join-Path $PSScriptRoot 'verify-published-xaml.ps1') -RuntimeDirectory artifacts/build/win-x64
}
