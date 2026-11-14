@echo off
rem ============================================================================
rem  DirMover build script  (architecture doc appendix B, A15)
rem
rem  Usage:
rem    build.cmd build      compile Core + App
rem    build.cmd test       run Core unit tests
rem    build.cmd publish    publish self-contained win-x64 output to publish\
rem    build.cmd portable   build the portable package into dist\ (folder + zip,
rem                         ready to attach to a GitHub release)
rem
rem  publish clears its output directory before publishing (and portable rebuilds
rem  its staged folder from scratch), so the produced file list contains only
rem  files from THIS build (docs/02 sec 9.5
rem  item 1). A stale file from an older generation used to ride along into the
rem  publish folder; that makes "what did we actually ship" unverifiable.
rem
rem  Why this script exists instead of a long command in the docs: the App layer
rem  (WinUI 3) historically needed two command-line switches on this machine
rem  (NETSDK1083 / NETSDK1082 / "AnyCPU is not supported for Self Contained mode").
rem  On the current package pair those errors no longer reproduce, but the
rem  switches are kept so this script keeps working if the packages are
rem  downgraded. Fixing them in one place beats copy-pasting two flags.
rem
rem  The switches are deliberately NOT in .csproj: they compensate for this
rem  build environment, they are not project properties.
rem
rem  ASCII-only on purpose. cmd.exe parses batch files with the active OEM code
rem  page (936 on zh-CN); a UTF-8 file with multi-byte comments gets split
rem  mid-line and every fragment is run as a command. Line endings must be CRLF
rem  or "goto :label" fails with "batch label not found".
rem ============================================================================

chcp 65001 >nul
setlocal EnableExtensions

set "ROOT=%~dp0"
pushd "%ROOT%"

rem Two switches, kept as insurance for older WindowsAppSDK / BuildTools
rem generations. On the current pair (WindowsAppSDK 1.8.260921001 +
rem SDK.BuildTools 10.0.26100.4654) a bare "dotnet build" already succeeds with
rem zero extra properties (0 warning / 0 error) and MSB4062 no longer
rem reproduces. They are still passed because older generations did hit
rem NETSDK1083 / MSB4062, and because the framework resources.pri staging step
rem depends on Build running (see DirMover.App.csproj StageFrameworkPri).
rem   Platform=x64  keeps the output path stable under bin\x64\... .
set "APPFLAGS=-p:Platform=x64 -p:EnableCoreMrtTooling=false"

if /i "%~1"=="build"   goto :do_build
if /i "%~1"=="test"    goto :do_test
if /i "%~1"=="publish" goto :do_publish
if /i "%~1"=="portable" goto :do_portable
goto :usage

rem ---------------------------------------------------------------- env probe
rem Locate a dotnet that actually has an SDK. "Program Files\dotnet" on this
rem machine has only host/shared/swidtag and NO sdk folder, so the dotnet on
rem PATH reports "No .NET SDKs were found" - which looks like a broken project
rem but is really a missing SDK. The real SDK lives under %USERPROFILE%\.dotnet.
rem
rem Probe order: DOTNET_ROOT -> %USERPROFILE%\.dotnet -> PATH -> give up.
:check_dotnet
set "DOTNET="

if defined DOTNET_ROOT if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not defined DOTNET for /f "delims=" %%p in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%p"

if not defined DOTNET goto :no_dotnet

rem Require a .NET 8 SDK specifically: a bare 6.x/7.x SDK would fail much later
rem with a far more confusing error.
"%DOTNET%" --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 goto :no_sdk

exit /b 0

rem ---------------------------------------------------------------- build
:do_build
call :check_dotnet
if errorlevel 1 exit /b 1

echo [1/2] Building DirMover.Core ...
"%DOTNET%" build "src\DirMover.Core\DirMover.Core.csproj" -c Debug --nologo
if errorlevel 1 (
    echo [FAILED] DirMover.Core did not compile.
    goto :fail
)

echo.
echo [2/2] Building DirMover.App ...
"%DOTNET%" build "src\DirMover.App\DirMover.App.csproj" -c Debug --nologo %APPFLAGS%
if errorlevel 1 (
    echo [FAILED] DirMover.App did not compile.
    goto :fail
)

echo.
echo [OK] Both projects compiled.
goto :done

rem ---------------------------------------------------------------- test
:do_test
call :check_dotnet
if errorlevel 1 exit /b 1

echo Running Core unit tests ...
"%DOTNET%" test "tests\DirMover.Core.Tests\DirMover.Core.Tests.csproj" -c Debug --nologo
if errorlevel 1 (
    echo [FAILED] Not all unit tests passed.
    goto :fail
)

goto :done

rem ---------------------------------------------------------------- publish
:do_publish
call :check_dotnet
if errorlevel 1 exit /b 1

call :clear_output_dir "publish"
if errorlevel 1 goto :fail

echo Publishing self-contained win-x64 output to publish\ ...
"%DOTNET%" publish "src\DirMover.App\DirMover.App.csproj" -c Release -r win-x64 --self-contained --nologo %APPFLAGS% -o "publish"
if errorlevel 1 (
    echo [FAILED] Publish failed.
    goto :fail
)

echo.
echo [OK] Output written to publish\ . No NETSDK1198 (the dangling PublishProfile
echo      property was removed from DirMover.App.csproj).
goto :done


rem ---------------------------------------------------------------- portable
rem Portable package: the only shipping form. The old single-file variant is
rem retired on purpose - it self-extracted native dlls into %TEMP% on every
rem launch, which is the opposite of what a portable build should do.
rem
rem Output (this is what the GitHub release attaches):
rem   dist\DirMover-<ver>-portable-win-x64\       the runnable folder
rem   dist\DirMover-<ver>-portable-win-x64.zip     the same folder, zipped
rem Both carry one top-level folder, so extracting never litters the target dir.
:do_portable
call :check_dotnet
if errorlevel 1 exit /b 1

call :read_version
if errorlevel 1 goto :fail

set "STAGE_NAME=DirMover-%VER%-portable-win-x64"
set "STAGE=%ROOT%dist\%STAGE_NAME%"
set "ZIP=%ROOT%dist\%STAGE_NAME%.zip"

rem Guarded cleanup: the path is always %ROOT%\dist\DirMover-<ver>-portable-win-x64
rem and :read_version already refused an empty VER, so this can never widen into
rem a delete of the repository root (same doctrine as :clear_output_dir).
if not exist "%ROOT%dist" mkdir "%ROOT%dist"
if exist "%STAGE%" rd /s /q "%STAGE%"
if exist "%STAGE%" goto :stage_locked
if exist "%ZIP%" del /f /q "%ZIP%"

echo Publishing portable output to "%STAGE%" ...
"%DOTNET%" publish "src\DirMover.App\DirMover.App.csproj" -c Release -r win-x64 --self-contained --nologo %APPFLAGS% -o "%STAGE%"
if errorlevel 1 (
    echo [FAILED] Portable publish failed.
    goto :fail
)

copy /y "%ROOT%docs\portable-readme.txt" "%STAGE%\README-Portable.txt" >nul
if errorlevel 1 (
    echo [FAILED] Could not copy README-Portable.txt into the package.
    goto :fail
)

echo Zipping ...
rem The Windows-bundled bsdtar (System32, 1803+) is far faster than
rem Compress-Archive on a ~200 MB WinUI payload. It is referenced by absolute
rem path on purpose: a Git-for-Windows / MSYS tar earlier on PATH is GNU tar,
rem which reads "D:\..." as rsh host:path and fails with
rem "Cannot connect to D: resolve failed".
set "SYSTAR=%SystemRoot%\System32\tar.exe"
if not exist "%SYSTAR%" goto :portable_zip_ps
"%SYSTAR%" -a -c -f "%ZIP%" -C "%ROOT%dist" "%STAGE_NAME%"
if errorlevel 1 goto :portable_zip_ps
goto :portable_zip_done

:portable_zip_ps
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGE%' -DestinationPath '%ZIP%' -CompressionLevel Optimal -Force"
if errorlevel 1 (
    echo [FAILED] Could not create the portable zip.
    goto :fail
)

:portable_zip_done
echo.
echo [OK] Folder  : dist\%STAGE_NAME%\
echo [OK] Package : dist\%STAGE_NAME%.zip
for %%f in ("%ZIP%") do echo [OK] Size    : %%~zf bytes
echo [OK] SHA-256 : printed below (paste it into the release notes)
powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 -LiteralPath '%ZIP%').Hash.ToLower()"
goto :done

:stage_locked
echo [ERROR] Could not clear "%STAGE%".
echo         Most likely DirMover.App.exe is still running from that folder.
echo         Close it (check Task Manager) and run this command again.
exit /b 1

rem ---------------------------------------------------------------- version
rem Read the single source of truth for the version (Catalog.AppVersion).
rem Refusing an empty version is deliberate: it would silently produce
rem "DirMover--portable-win-x64.zip" and a release asset nobody can tell apart.
:read_version
set "VER="
set "RAWVER="
for /f "tokens=2 delims==" %%v in ('findstr /c:"public const string AppVersion" "src\DirMover.Core\Catalog.cs"') do set "RAWVER=%%v"
if not defined RAWVER goto :no_version
set "VER=%RAWVER: =%"
set VER=%VER:"=%
set VER=%VER:;=%
if not defined VER goto :no_version
exit /b 0

:no_version
echo [ERROR] Could not read Catalog.AppVersion from src\DirMover.Core\Catalog.cs.
echo         Refusing to build a package with an empty version.
exit /b 1

rem ---------------------------------------------------------------- clear output
rem Wipe one publish output directory before publishing (docs/02 sec 9.5 item 1).
rem
rem Argument: a relative directory name; it must match the only whitelisted name
rem below. The absolute path is always built from %ROOT% (= %~dp0),
rem never from the caller, so no invocation can turn this into a recursive delete
rem of the repository root (a bare "rd /s /q %~dp0" is exactly the failure mode
rem this whitelist exists to prevent).
rem
rem On failure the script STOPS (exit /b 1) instead of publishing on top of a
rem half-deleted folder: a mixed old/new output cannot be told apart afterwards.
rem The usual cause is a locked file, i.e. DirMover.App.exe still running from
rem that folder.
:clear_output_dir
set "CLEAR_NAME=%~1"
if /i not "%CLEAR_NAME%"=="publish" goto :clear_refused

set "CLEAR_PATH=%ROOT%%CLEAR_NAME%"
if not exist "%CLEAR_PATH%" exit /b 0

echo Clearing "%CLEAR_PATH%" ...
rd /s /q "%CLEAR_PATH%"
if exist "%CLEAR_PATH%" goto :clear_failed
exit /b 0

:clear_refused
echo [ERROR] Refusing to clear unexpected directory: "%CLEAR_NAME%"
echo         Only "publish" may be cleared by this script.
echo         (portable stages its own folder under dist\)
exit /b 1

:clear_failed
echo [ERROR] Could not clear "%CLEAR_PATH%".
echo         Most likely DirMover.App.exe is still running from that folder.
echo         Close it (check Task Manager) and run this command again.
echo         Publishing was stopped on purpose: publishing into a half-cleared
echo         folder mixes old and new files and makes the output unverifiable.
exit /b 1

rem ---------------------------------------------------------------- exit paths
:no_dotnet
echo [ERROR] No dotnet.exe found.
echo         Looked in: %%DOTNET_ROOT%%, %%USERPROFILE%%\.dotnet, PATH.
echo         Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
popd
endlocal
exit /b 1

:no_sdk
echo [ERROR] "%DOTNET%" has no .NET 8 SDK ^(runtime only^).
echo         Install the .NET 8 SDK, or set DOTNET_ROOT to an SDK install.
popd
endlocal
exit /b 1

:usage
echo Usage:
echo   build.cmd build     compile Core + App
echo   build.cmd test      run Core unit tests
echo   build.cmd publish   publish self-contained win-x64 output to publish\
echo   build.cmd portable  build the portable package (folder + zip) into dist\
popd
endlocal
exit /b 2

:fail
popd
endlocal
exit /b 1

:done
popd
endlocal
exit /b 0
