@echo off
REM Download pre-baked navmesh tiles from the CDN into the local cache.
REM Double-click to grab everything in scope (Classic + Forever, see
REM scripts\download-common.ps1); pass a name to limit, e.g.:
REM   download-navmesh.bat Kalimdor
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\download-navmesh.ps1" %*
echo.
pause
