@echo off
rem WoWTranslateDirect build script (MSVC x86, static CRT)
set CLROOT=E:\炫彩IDE\data\VC\VC2022\14.41.34120
set SDK=E:\炫彩IDE\data\VC\Windows Kits\10
set SDKVER=10.0.19041.0

set INCLUDE=%CLROOT%\include;%SDK%\Include\%SDKVER%\um;%SDK%\Include\%SDKVER%\shared;%SDK%\Include\%SDKVER%\ucrt
set LIB=%CLROOT%\lib\x86;%SDK%\Lib\%SDKVER%\um\x86;%SDK%\Lib\%SDKVER%\ucrt\x86

cd /d %~dp0

if not exist build mkdir build
del /q build\*.dll build\*.obj build\*.lib build\*.exp build\*.map 2>nul

echo === build dinput8.dll (proxy) ===
"%CLROOT%\bin\Hostx86\x86\cl.exe" /nologo /utf-8 /O2 /MT /EHsc /LD /W3 ^
  /Fe:build\dinput8.dll src\dinput8_proxy.cpp ^
  /link /DEF:src\dinput8.def
if errorlevel 1 goto fail

echo === build WoWTranslateDirect.dll (engine) ===
"%CLROOT%\bin\Hostx86\x86\cl.exe" /nologo /utf-8 /O2 /MT /EHsc /LD /W3 /std:c++17 ^
  /I third_party ^
  /Fe:build\WoWTranslateDirect.dll src\direct_main.cpp src\translator.cpp src\wt_log.cpp ^
  /link winhttp.lib
if errorlevel 1 goto fail
rem cl.exe ICE 时 errorlevel 可能不置位（v24 实测），产物存在才算成功
if not exist build\wt_map.dll (
  echo engine DLL missing after compile - treating as failure
  goto fail
)

if not exist ..\..\assets\direct-dll mkdir ..\..\assets\direct-dll
copy /y build\dinput8.dll ..\..\assets\direct-dll\dinput8.dll >nul
copy /y build\WoWTranslateDirect.dll ..\..\assets\direct-dll\WoWTranslateDirect.dll >nul
echo BUILD OK
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
