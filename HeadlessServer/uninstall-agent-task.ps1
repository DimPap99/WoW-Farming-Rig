#Requires -Version 5.1
<#
.SYNOPSIS
  Removes the logon task install-agent-task.ps1 registered: the agent no longer starts at logon.

.DESCRIPTION
  An agent already running is left alone: ending the task would terminate it without a clean stop.
  Stop it with Ctrl+C in its window, by closing the window, or by logging off: each releases the
  game's keys first.

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
Write-Host "An agent running now keeps running: Ctrl+C in its window, or closing it, stops it, releasing the game's keys first."
