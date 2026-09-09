<#
.SYNOPSIS
  Build agent for pair-programming with Claude (Cowork).

  Claude can read and write files in this folder but cannot type into a terminal, so this script
  watches .claude-build\request.txt and runs ONE of a fixed set of commands when it appears:

    build   -> dotnet build  (Debug)
    test    -> dotnet test   (Debug)
    run     -> stops any running Llampec.exe, builds, launches the Debug exe, tails the log
    log     -> just tails the app log (no build/restart)
    status  -> git status --porcelain=v1 + git diff --stat (no build)
    commit  -> git add -A (excluding .claude-build) + git commit -F .claude-build\commit-message.txt
    stop    -> stops Llampec.exe
    quit    -> ends this agent

  Output goes to .claude-build\output.txt (UTF-8), ending with "### EXIT <code> ###".
  Everything the agent does is also appended to .claude-build\log.txt.
  Nothing else is ever executed. Run it from the repo root and leave the window open:

    powershell -ExecutionPolicy Bypass -File tools\build-agent.ps1
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dir = Join-Path $root '.claude-build'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$request = Join-Path $dir 'request.txt'
$output  = Join-Path $dir 'output.txt'
$agentLog = Join-Path $dir 'log.txt'
$commitMsg = Join-Path $dir 'commit-message.txt'
$exe     = Join-Path $root 'src\Llampec.App\bin\Debug\net10.0-windows\Llampec.exe'
$appLog  = Join-Path $env:LOCALAPPDATA 'Llampec\llampec.log'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Log([string]$line) {
    $stamp = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $line"
    Write-Host $stamp
    [System.IO.File]::AppendAllText($agentLog, $stamp + "`r`n", $utf8)
}

function Invoke-Proc([string]$fileName, [string[]]$procArgv) {
    # Runs a CLI command with merged output, streaming to console and to output.txt.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $fileName
    $psi.Arguments = ($procArgv -join ' ')
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.EnvironmentVariables['DOTNET_CLI_UI_LANGUAGE'] = 'en'
    $psi.EnvironmentVariables['DOTNET_NOLOGO'] = '1'
    $psi.EnvironmentVariables['GIT_PAGER'] = ''
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    $sb = New-Object System.Text.StringBuilder
    $handler = {
        if ($EventArgs.Data -ne $null) {
            Write-Host $EventArgs.Data
            [void]$Event.MessageData.AppendLine($EventArgs.Data)
        }
    }
    $o = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -Action $handler -MessageData $sb
    $e = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action $handler -MessageData $sb
    [void]$p.Start()
    $p.BeginOutputReadLine()
    $p.BeginErrorReadLine()
    while (-not $p.WaitForExit(500)) { }
    $p.WaitForExit()   # flush async readers
    Start-Sleep -Milliseconds 300
    Unregister-Event -SourceIdentifier $o.Name; Unregister-Event -SourceIdentifier $e.Name
    [System.IO.File]::AppendAllText($output, $sb.ToString(), $utf8)
    return $p.ExitCode
}

function Invoke-Native([string[]]$argv) { Invoke-Proc 'dotnet' $argv }
function Invoke-Git([string[]]$argv) { Invoke-Proc 'git' $argv }

function Finish([int]$code, [System.Diagnostics.Stopwatch]$sw) {
    $line = "### EXIT $code ### ($([int]$sw.Elapsed.TotalSeconds) s)"
    [System.IO.File]::AppendAllText($output, $line + "`r`n", $utf8)
    Log $line
}

Log "Llampec build agent started, watching $request (Ctrl+C to quit)"
while ($true) {
    if (-not (Test-Path $request)) { Start-Sleep -Milliseconds 1500; continue }
    try {
        $cmd = (Get-Content $request -Raw).Trim().ToLowerInvariant()
        Remove-Item $request -Force
        [System.IO.File]::WriteAllText($output, '', $utf8)
        Log "request: $cmd"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        switch ($cmd) {
            'build' { Finish (Invoke-Native @('build', '-c', 'Debug')) $sw }
            'test'  { Finish (Invoke-Native @('test', '-c', 'Debug', '--logger', '"console;verbosity=normal"')) $sw }
            'stop'  {
                Stop-Process -Name Llampec -ErrorAction SilentlyContinue
                [System.IO.File]::AppendAllText($output, "stopped`r`n", $utf8)
                Finish 0 $sw
            }
            'run'   {
                Stop-Process -Name Llampec -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 300
                $code = Invoke-Native @('build', '-c', 'Debug')
                if ($code -eq 0) {
                    $logSizeBefore = if (Test-Path $appLog) { (Get-Item $appLog).Length } else { 0 }
                    $proc = Start-Process $exe -PassThru
                    # Poll instead of one fixed sleep, so a fast crash is caught and reported precisely.
                    $exited = $proc.WaitForExit(3000)
                    $text = if ($exited) {
                        "--- process EXITED after starting, exit code $($proc.ExitCode) ---`r`n"
                    } else {
                        "--- still running 3s after launch (this is normal) ---`r`n"
                    }
                    if (Test-Path $appLog) {
                        $newBytes = (Get-Item $appLog).Length - $logSizeBefore
                        $text += "log file: $appLog ($newBytes new bytes this run)`r`n"
                        $text += ((Get-Content $appLog -Tail 60) -join "`r`n") + "`r`n"
                    } else {
                        $text += "log file not found: $appLog`r`n"
                    }
                    [System.IO.File]::AppendAllText($output, $text, $utf8)
                }
                Finish $code $sw
            }
            'log'   {
                $text = if (Test-Path $appLog) {
                    "log file: $appLog`r`n" + ((Get-Content $appLog -Tail 80) -join "`r`n") + "`r`n"
                } else {
                    "log file not found: $appLog`r`n"
                }
                [System.IO.File]::AppendAllText($output, $text, $utf8)
                Finish 0 $sw
            }
            'status' {
                Invoke-Git @('status', '--porcelain=v1', '-b') | Out-Null
                Invoke-Git @('diff', '--stat') | Out-Null
                Finish 0 $sw
            }
            'commit' {
                if (-not (Test-Path $commitMsg)) {
                    [System.IO.File]::AppendAllText($output, "no $commitMsg -- write the commit message there first`r`n", $utf8)
                    Finish 2 $sw
                } else {
                    Invoke-Git @('add', '-A', '--', '.', "':!.claude-build'") | Out-Null
                    $code = Invoke-Git @('commit', '-F', "`"$commitMsg`"")
                    if ($code -eq 0) { Invoke-Git @('log', '-1', '--stat') | Out-Null }
                    Finish $code $sw
                }
            }
            'quit'  { Finish 0 $sw; Log 'bye'; exit 0 }
            default {
                [System.IO.File]::AppendAllText($output, "unknown command '$cmd'`r`n", $utf8)
                Finish 2 $sw
            }
        }
    }
    catch {
        $msg = "AGENT ERROR: $($_.Exception.Message)`r`n$($_.ScriptStackTrace)"
        Log $msg
        try { [System.IO.File]::AppendAllText($output, $msg + "`r`n### EXIT 99 ###`r`n", $utf8) } catch { }
    }
}
