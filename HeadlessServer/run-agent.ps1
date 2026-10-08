#Requires -Version 5.1
<#
.SYNOPSIS
  Keeps this PC's bot agent running: starts HeadlessServer --agent, and starts it again whenever
  it ends with anything but 0.

.DESCRIPTION
  The logon task (install-agent-task.ps1) runs this script at logon. The agent ends with:
    0      it was stopped: Ctrl+C in its window, closing it, a logoff. This loop ends too.
    75     a run could not be torn down: only a new process takes its threads.
    other  it failed. HeadlessServer builds a failed host again by itself, so an exit means the
           process itself could not go on.
  Every exit but 0 starts it again: after 5 s, doubling up to 5 minutes while it keeps failing
  within a minute of starting, and back to 5 s once it ran 10 minutes. Each start and end is a
  line in run-agent.log beside this script.

  It runs the Release build directly, as run.bat does: build it first (build.bat). "dotnet run"
  would put the agent in a job that ends it the moment dotnet.exe ends, which closing this window
  ends first, before the agent releases the game's keys. The agent's settings are
  headless_appsettings.json's; -Manager, -PathingService and -Name put them on its command line.

.EXAMPLE
  .\run-agent.ps1
  .\run-agent.ps1 -Manager http://botbox:5030 -PathingService http://botbox:5010 -Name "PC 1"
#>
[CmdletBinding()]
param(
    [string]$Manager = "",          # the Bot Manager to dial out to (--manager)
    [string]$PathingService = "",   # the Pathing Service a run's files are synced from (--pathing-service)
    [string]$Name = ""              # what the Bot Manager calls this agent (--name)
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$RestartExitCode = 75   # HeadlessServer's AgentProcess.RestartExitCode
$FirstDelaySeconds = 5
$LongestDelaySeconds = 300
$QuickFailure = [TimeSpan]::FromMinutes(1)
$LongRun = [TimeSpan]::FromMinutes(10)
$LogFile = Join-Path $PSScriptRoot "run-agent.log"

# One array, splatted, so a name with spaces stays one argument.
$agentArgs = @("--agent")
if ($Manager) { $agentArgs += @("--manager", $Manager) }
if ($PathingService) { $agentArgs += @("--pathing-service", $PathingService) }
if ($Name) { $agentArgs += @("--name", $Name) }

function Write-Log([string]$Line) {
    $stamped = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Line
    Write-Host $stamped
    # A log file that cannot be written (held open, a full disk) must not end the loop.
    try {
        Add-Content -Path $LogFile -Value $stamped
    } catch {
        Write-Host "(run-agent.log could not be written: $($_.Exception.Message))"
    }
}

# The newest Release build, looked for at every start, so a build made meanwhile is the one run.
function Find-Build {
    Get-ChildItem -Path (Join-Path $PSScriptRoot "bin") -Filter "HeadlessServer.dll" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "[\\/]Release[\\/]" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

$delay = $FirstDelaySeconds
while ($true) {
    # Timed with a stopwatch: a clock changed for daylight saving would misjudge the run.
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $built = Find-Build
    if ($built) {
        Write-Log "Starting the agent: dotnet $($built.FullName) $($agentArgs -join ' ')"
        & dotnet $built.FullName @agentArgs
        $code = $LASTEXITCODE
    } else {
        Write-Log "HeadlessServer has no Release build: run build.bat."
        $code = 1
    }
    $ran = $timer.Elapsed

    if ($code -eq 0) {
        Write-Log "The agent was stopped (exit 0): it is not started again."
        break
    }

    if ($ran -ge $LongRun) {
        $delay = $FirstDelaySeconds
    }

    $why = if ($code -eq $RestartExitCode) { "a run could not be torn down" } else { "it failed" }
    Write-Log "The agent ended with $code ($why) after $([int]$ran.TotalSeconds) s: starting it again in $delay s."
    Start-Sleep -Seconds $delay
    if ($ran -lt $QuickFailure) {
        $delay = [Math]::Min($delay * 2, $LongestDelaySeconds)
    }
}
