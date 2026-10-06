# Temporary experiment, separate from the previous NonRudeHWND trial.
# Use a fresh STA process: powershell.exe -NoProfile -STA -File <this script>
[CmdletBinding()]
param(
    [ValidateRange(1, 30)][int]$DelaySeconds = 5,
    [ValidateRange(1, 120)][int]$DurationSeconds = 60,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Ejecuta la prueba con powershell.exe -NoProfile -STA -File.'
}
if (-not ('LlampecFullscreenEdgeTrial' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'FullscreenEdgeTrial.cs')
}
if ($SelfTest) {
    [LlampecFullscreenEdgeTrial]::SelfTest()
    return
}

Write-Host "Activa la ventana que quieras probar. La prueba dura $DurationSeconds segundos."
Write-Host 'Usa el monitor con la barra principal. Acerca el raton al borde inferior y retiralo para ocultarla.'
Write-Host 'Ctrl+Alt+F activa o desactiva la pantalla completa; la consola indica la pantalla y la ventana.'
Write-Host 'Seleccionar otra ventana (barra o Alt+Tab) restaura la ventana anterior.'
Write-Host 'El gesto tactil, Inicio, otros paneles de Windows y Llampec conservan la pantalla completa.'
Write-Host 'Al desactivarla, el atajo sigue disponible hasta terminar el tiempo de prueba.'
Write-Host 'Si haces clic en la barra y toma el foco, vuelve a la app para que se cubra de nuevo.'
Write-Host 'Mantiene tus ajustes de barra. No cierres PowerShell: la ventana se restaura al terminar.'
for ($edgeCountdown = $DelaySeconds; $edgeCountdown -gt 0; $edgeCountdown--) {
    Write-Host "Inicio en $edgeCountdown..."
    Start-Sleep -Seconds 1
}
[LlampecFullscreenEdgeTrial]::Run($DurationSeconds)
