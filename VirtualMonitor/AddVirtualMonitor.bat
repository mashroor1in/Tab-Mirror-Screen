@echo off
:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrative privileges to install the kernel driver...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

:: We are now running as Admin
cd /d "%~dp0\usbmmidd_v2"
echo ===================================================
echo     Installing Tab Mirror Virtual Display Driver
echo ===================================================

:: Install the driver
echo Installing driver framework...
deviceinstaller64 install usbmmidd.inf usbmmidd

:: Plug in the virtual monitor
echo Plugging in virtual monitor...
deviceinstaller64 enableidd 1

echo.
echo SUCCESS! A virtual monitor has been added.
echo Check your Windows Display Settings to see the new screen.
echo You can now extend your desktop to it!
echo.
pause
