param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$required = @(
    'Llampec.exe', 'Llampec.dll', 'Llampec.deps.json', 'Llampec.runtimeconfig.json',
    'Llampec.pri', 'App.xbf', 'Flyout/FlyoutWindow.xbf', 'Assets/Llampec.ico'
)
foreach ($relative in $required) {
    $file = Join-Path $PublishDirectory $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Incomplete WinUI publication: missing or empty $relative in $PublishDirectory"
    }
}
Write-Output "WinUI application resources verified: $PublishDirectory"
