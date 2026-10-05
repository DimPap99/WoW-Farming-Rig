@echo off
REM Download pre-generated Leaflet minimap tiles from the CDN into the local cache.
REM Double-click to grab everything in scope (Classic + Forever, see
REM scripts\download-common.ps1); pass -Continent to limit, e.g.:
REM   download-leaflet.bat -Continent Azeroth
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\scripts\download-leaflet.ps1" %*
echo.
pause
