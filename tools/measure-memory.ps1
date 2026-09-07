<#
.SYNOPSIS
  Samples Llampec's memory footprint so the "idle RAM budget" in the README can be checked reproducibly.
.DESCRIPTION
  PrivateWS  = private working set (what Task Manager's "Memory" column shows): physical RAM used only by this process.
  WorkingSet = total working set, including DLL pages shared with other processes.
  PrivateBytes = committed private virtual memory (reservations by the GC and graphics driver; not physical usage).
.EXAMPLE
  .\tools\measure-memory.ps1            # 30 samples, 1 s apart
  .\tools\measure-memory.ps1 -Seconds 120
#>
param([int]$Seconds = 30, [string]$ProcessName = "Llampec")

$samples = @()
for ($i = 0; $i -lt $Seconds; $i++) {
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Error "Process '$ProcessName' is not running."; exit 1 }
    $p.Refresh()
    # WMI performance class: names are not localized, unlike Get-Counter paths.
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue
    $samples += [pscustomobject]@{
        Time         = (Get-Date).ToString("HH:mm:ss")
        PrivateWSMB  = if ($perf) { [math]::Round($perf.WorkingSetPrivate / 1MB, 1) } else { $null }
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateBytesMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Threads      = $p.Threads.Count
        Handles      = $p.HandleCount
        CpuSec       = [math]::Round($p.TotalProcessorTime.TotalSeconds, 2)
    }
    Start-Sleep -Seconds 1
}

$samples | Format-Table -AutoSize
$pws = $samples | Measure-Object PrivateWSMB -Minimum -Maximum -Average
$ws  = $samples | Measure-Object WorkingSetMB -Minimum -Maximum -Average
$cpu = $samples[-1].CpuSec - $samples[0].CpuSec
"Private working set (Task Manager 'Memory'): min {0} MB, avg {1:N1} MB, max {2} MB" -f $pws.Minimum, $pws.Average, $pws.Maximum
"Total working set (incl. shared DLLs):       min {0} MB, avg {1:N1} MB, max {2} MB" -f $ws.Minimum, $ws.Average, $ws.Maximum
"Private bytes (committed, not physical):     {0} MB" -f $samples[-1].PrivateBytesMB
"CPU time used during the {0}s sample: {1:N2} s" -f $Seconds, $cpu
"Threads: {0}   Handles: {1}   Arch: {2}" -f $samples[-1].Threads, $samples[-1].Handles, [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
