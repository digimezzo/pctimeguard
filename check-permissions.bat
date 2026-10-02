@echo off
rem Must be run as administrator; the folder is not readable for other accounts.
net session >nul 2>&1
if errorlevel 1 (
    echo Please run this file as administrator.
    pause
    exit /b 1
)

icacls "C:\ProgramData\PcTimeGuard"
echo.
echo Expected only:
echo   NT AUTHORITY\SYSTEM:(OI)(CI)(F)
echo   BUILTIN\Administrators:(OI)(CI)(F)
pause
