param(
  [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
$source = 'https://rum-sdk.oss-cn-hangzhou.aliyuncs.com/native/AlibabaCloud_RUM_Windows.zip'
$archiveHash = 'BFA05C0718A57EB7E94C9494499BD3C84305A3CC39B76D6A9096667553928994'
$dllHash = '33CEC949309F8025BE35FF19D7AE7F7BFC0F59A9FF0BEF3E05C40460F0BF6E8D'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
  if (-not $ArchivePath) {
    $ArchivePath = Join-Path $temporary 'AlibabaCloud_RUM_Windows.zip'
    Invoke-WebRequest -Uri $source -OutFile $ArchivePath
  }
  $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
  if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
    throw 'ARMS SDK archive checksum mismatch; review the vendor release before updating the pin.'
  }
  $expanded = Join-Path $temporary 'extracted'
  Expand-Archive -LiteralPath $archive -DestinationPath $expanded
  $dll = Get-ChildItem -LiteralPath $expanded -Recurse -File -Filter alibabacloud_rum.dll |
    Where-Object { $_.FullName -match '[\\/]bin[\\/]x86_64[\\/]Release[\\/]alibabacloud_rum\.dll$' } |
    Select-Object -First 1
  if (-not $dll) { throw 'The pinned x64 Release ARMS DLL was not found.' }
  if ((Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash -ne $dllHash) {
    throw 'ARMS SDK DLL checksum mismatch.'
  }
  $destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'windows\third_party\arms'
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $destination 'alibabacloud_rum.dll') -Force
  Write-Output 'Pinned ARMS SDK DLL staged for the Windows build.'
} finally {
  Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
