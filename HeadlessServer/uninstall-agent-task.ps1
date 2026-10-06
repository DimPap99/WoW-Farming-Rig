#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the logon task install-agent-task.ps1 registered: the agent no longer starts at logon.

.DESCRIPTION
  An agent already running is left alone: ending the task would end its loop and could leave the
  agent without a clean stop. Stop it with Ctrl+C in its window, which releases the game's keys
  first, or by logging off.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall-agent-task.ps1
#>
[CmdletBinding()]
param(
    [string]$TaskName = "WoW Farming Rig agent"
)

$ErrorActionPreference = "Stop"

if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    Write-Host "There is no task '$TaskName': nothing to remove."
    return
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "Removed '$TaskName': the agent no longer starts at logon."
Write-Host "An agent running now keeps running: Ctrl+C in its window stops it, releasing the game's keys first."
