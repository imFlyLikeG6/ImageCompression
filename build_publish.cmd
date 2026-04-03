@echo off
setlocal

REM Always run from script directory
cd /d "%~dp0"

set "COMMON_ARGS=%*"
set "EXIT_CODE=0"

echo == [1/2] Running normal publish ==
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_publish.ps1" %COMMON_ARGS%
if errorlevel 1 (
    set "EXIT_CODE=%ERRORLEVEL%"
    echo.
    echo Normal publish failed. Exit code: %EXIT_CODE%
    goto :END
)

echo.
echo == [2/2] Running small publish ==
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_publish.ps1" %COMMON_ARGS% -Small
if errorlevel 1 (
    set "EXIT_CODE=%ERRORLEVEL%"
    echo.
    echo Small publish failed. Exit code: %EXIT_CODE%
    goto :END
)

echo.
echo All publish steps completed successfully.

:END
echo.
pause
exit /b %EXIT_CODE%
