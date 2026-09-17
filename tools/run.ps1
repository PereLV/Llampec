param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = ('win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src\Llampec.App\bin\$Configuration\net10.0-windows10.0.26100.0\$RuntimeIdentifier\Llampec.exe"
Get-Process Llampec -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process
& dotnet build (Join-Path $root 'src/Llampec.App') -c $Configuration -r $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
