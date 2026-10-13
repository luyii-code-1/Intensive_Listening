param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& $Python -m venv .venv
if ($LASTEXITCODE -ne 0) { throw '创建环境失败' }
$runtime = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
& $runtime -m pip install -r requirements.txt 'pyinstaller>=6.12,<7'
if ($LASTEXITCODE -ne 0) { throw '安装依赖失败' }
& $runtime -m PyInstaller --noconfirm --clean --onefile --name MDN.Server --add-data 'templates;templates' --add-data 'settings-schema.json;.' --collect-all uvicorn --collect-all wsproto server.py
if ($LASTEXITCODE -ne 0) { throw '打包失败' }
Write-Host 'Windows 部署文件：dist/MDN.Server.exe'
