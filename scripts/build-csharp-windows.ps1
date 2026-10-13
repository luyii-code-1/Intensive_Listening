param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'E:\dev\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
Set-Location (Join-Path $PSScriptRoot '..')
& "$env:DOTNET_ROOT\dotnet.exe" build IL2.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& "$env:DOTNET_ROOT\dotnet.exe" test IL2.slnx -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($Publish) {
    & "$env:DOTNET_ROOT\dotnet.exe" publish src/IL.App/IL.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/build/win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
}
