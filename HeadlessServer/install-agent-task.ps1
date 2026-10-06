#Requires -Version 5.1
<#
.SYNOPSIS
  Starts this PC's bot agent at logon: registers a Task Scheduler task that runs run-agent.ps1
  when you log on.

.DESCRIPTION
  The agent has to run in your desktop session to see the game and drive it, which a Windows
  service cannot, so it starts from a logon task: for the current user, interactive, without
  administrator rights. The task runs run-agent.ps1 in a minimized window. That script starts the
  agent again whenever it ends with anything but 0, and Task Scheduler restarts the script if it
  fails. The task has no time limit, runs on batteries, and runs once: logging on again while it
  runs starts nothing new. Running this script again replaces the task.

  The agent runs the Release build: build it first (build.bat). Its settings are
  headless_appsettings.json's (the Bot Manager, the Pathing Service, ApiKey); -Manager,
  -PathingService and -Name put them on its command line instead.

  To stop the agent, press Ctrl+C in its window: it releases the game's keys first, and the loop
  ends. uninstall-agent-task.ps1 removes the task.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-agent-task.ps1
  powershell -ExecutionPolicy Bypass -File .\install-agent-task.ps1 -Manager http://botbox:5030 -PathingService http://botbox:5010 -Name "PC 1"
#>
[CmdletBinding()]
param(
    [string]$Manager = "",          # the Bot Manager to dial out to (--manager)
    [string]$PathingService = "",   # the Pathing Service a run's files are synced from (--pathing-service)
    [string]$Name = "",             # what the Bot Manager calls this agent (--name)
    [string]$TaskName = "WoW Farming Rig agent"
)

$ErrorActionPreference = "Stop"

$launcher = Join-Path $PSScriptRoot "run-agent.ps1"
if (-not (Test-Path $launcher)) {
    throw "run-agent.ps1 is not beside this script ($PSScriptRoot)."
}

$built = Get-ChildItem -Path (Join-Path $PSScriptRoot "bin") -Filter "HeadlessServer.dll" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "[\\/]Release[\\/]" } | Select-Object -First 1
if (-not $built) {
    Write-Warning "HeadlessServer has no Release build yet: run build.bat, or the agent fails at every start until you do."
}

# powershell.exe's command line: each value quoted, so a name with spaces stays one argument.
$arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Minimized", "-File", "`"$launcher`"")
if ($Manager) { $arguments += @("-Manager", "`"$Manager`"") }
if ($PathingService) { $arguments += @("-PathingService", "`"$PathingService`"") }
if ($Name) { $arguments += @("-Name", "`"$Name`"") }

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument ($arguments -join " ") -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description "This PC's bot agent (WoW Farming Rig): HeadlessServer --agent, kept running by run-agent.ps1." -Force | Out-Null

Write-Host "Registered '$TaskName': the agent starts when $user logs on."
Write-Host "Start it now: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Remove it:    .\uninstall-agent-task.ps1"
