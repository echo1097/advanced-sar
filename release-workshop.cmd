@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Virtual environment not found at .venv\Scripts\python.exe
    pause
    exit /b 1
)

".venv\Scripts\python.exe" "scripts\release\main.py" %*
set "exitCode=%ERRORLEVEL%"

echo.
pause
exit /b %exitCode%
