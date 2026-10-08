@ECHO OFF
rem The Release build (build.bat first), run directly: "dotnet run" would put it in a job that ends
rem it the moment this window closes, before it releases the game's keys.
cd /d "%~dp0"
set "BUILT="
for /d %%d in ("bin\Release\net*") do set "BUILT=%%d\HeadlessServer.dll"
if not defined BUILT (
    echo HeadlessServer has no Release build yet: run build.bat first.
    pause
    exit /b 1
)

dotnet "%BUILT%" %*
pause
