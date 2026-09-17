<#
.SYNOPSIS
  Samples Llampec's memory footprint and CPU use.
.DESCRIPTION
  PrivateWS  = private working set (what Task Manager's "Memory" column shows): physical RAM used only by this process.
  WorkingSet = total working set, including DLL pages shared with other processes.
  PrivateBytes = committed private virtual memory (not reserved address space, nor physical usage).
.EXAMPLE
  .\tools\measure-memory.ps1            # 30 samples, 1 s apart
  .\tools\measure-memory.ps1 -Seconds 120
#>
param([ValidateRange(2,3600)][int]$Seconds = 30, [string]$ProcessName = "Llampec", [int]$ProcessId = 0, [string]$OutputPath)

$samples = @()
$measurementClock = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Seconds; $i++) {
    $p = if ($ProcessId) { Get-Process -Id $ProcessId -ErrorAction SilentlyContinue } else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $p) { Write-Error "Process '$ProcessName' is not running."; exit 1 }
    $p.Refresh()
    # WMI performance class: names are not localized, unlike Get-Counter paths.
    $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue
    $samples += [pscustomobject]@{
        Time         = (Get-Date).ToString("HH:mm:ss")
        ProcessId    = $p.Id
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
$measurementClock.Stop()
if ($OutputPath) { $samples | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8 }
$pws = $samples | Measure-Object PrivateWSMB -Minimum -Maximum -Average
$ws  = $samples | Measure-Object WorkingSetMB -Minimum -Maximum -Average
$cpu = $samples[-1].CpuSec - $samples[0].CpuSec
"Private working set (Task Manager 'Memory'): min {0} MB, avg {1:N1} MB, max {2} MB" -f $pws.Minimum, $pws.Average, $pws.Maximum
"Total working set (incl. shared DLLs):       min {0} MB, avg {1:N1} MB, max {2} MB" -f $ws.Minimum, $ws.Average, $ws.Maximum
"Private bytes (committed, not physical):     {0} MB" -f $samples[-1].PrivateBytesMB
"CPU time used across {0} samples ({1:N1}s elapsed): {2:N2} s" -f $Seconds, $measurementClock.Elapsed.TotalSeconds, $cpu
"Threads: {0}   Handles: {1}   PID: {2}" -f $samples[-1].Threads, $samples[-1].Handles, $p.Id
