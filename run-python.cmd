@echo off
setlocal
set "ERIS_RUN_PY="
if defined ERIS_PYTHON if exist "%ERIS_PYTHON%" set "ERIS_RUN_PY=%ERIS_PYTHON%"
if not defined ERIS_RUN_PY if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" set "ERIS_RUN_PY=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if defined ERIS_RUN_PY goto found
where py >nul 2>&1
if errorlevel 1 goto missing
py -3 -c "import sys; sys.exit(0 if sys.version_info >= (3,11) else 1)"
if errorlevel 1 goto missing
py -3 %*
exit /b %errorlevel%
:found
"%ERIS_RUN_PY%" -c "import sys; sys.exit(0 if sys.version_info >= (3,11) else 1)"
if errorlevel 1 goto missing
"%ERIS_RUN_PY%" %*
exit /b %errorlevel%
:missing
echo Python 3.11 or newer is required for optional Codex integration or source builds.
echo Start.cmd still works without Python in the Windows release package.
echo Install Python with the py launcher, or set ERIS_PYTHON to python.exe.
exit /b 1
