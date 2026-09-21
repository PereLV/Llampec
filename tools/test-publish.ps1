param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [ValidateSet('win-arm64', 'win-x64')][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$required = @(
    'Llampec.exe', 'Llampec.dll', 'Llampec.deps.json', 'Llampec.runtimeconfig.json',
    'Llampec.pri', 'App.xbf', 'Flyout/FlyoutWindow.xbf', 'Assets/Llampec.ico',
    'licenses/Mouser.LICENSE.txt'
)
foreach ($relative in $required) {
    $file = Join-Path $PublishDirectory $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Incomplete WinUI publication: missing or empty $relative in $PublishDirectory"
    }
}
if ($RuntimeIdentifier) {
    $hostBytes = [System.IO.File]::ReadAllBytes((Join-Path $PublishDirectory 'Llampec.exe'))
    if ($hostBytes.Length -lt 64 -or [BitConverter]::ToUInt16($hostBytes, 0) -ne 0x5A4D) {
        throw 'The application host is not a Windows executable.'
    }
    $peOffset = [BitConverter]::ToInt32($hostBytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset -gt ($hostBytes.Length - 6) -or [BitConverter]::ToUInt32($hostBytes, $peOffset) -ne 0x4550) {
        throw 'The application host has an invalid PE header.'
    }
    $expectedMachine = if ($RuntimeIdentifier -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
    if ([BitConverter]::ToUInt16($hostBytes, $peOffset + 4) -ne $expectedMachine) {
        throw "The application host does not match $RuntimeIdentifier."
    }
}
Write-Output "WinUI application resources verified: $PublishDirectory"
