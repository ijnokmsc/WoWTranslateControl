@echo off
set CLROOT=E:\Ï≈≤ IDE\data\VC\VC2022\14.41.34120
set SDK=E:\Ï≈≤ IDE\data\VC\Windows Kits\10
set INCLUDE=%CLROOT%\include;%SDK%\Include\10.0.19041.0\um;%SDK%\Include\10.0.19041.0\shared;%SDK%\Include\10.0.19041.0\ucrt
set LIB=%CLROOT%\lib\x86;%SDK%\Lib\10.0.19041.0\um\x86;%SDK%\Lib\10.0.19041.0\ucrt\x86
"%CLROOT%\bin\Hostx86\x86\cl.exe" /nologo /utf-8 /O2 /MT /EHsc /LD /W3 /std:c++17 /I third_party /Fe:build\wt_map.dll src\direct_main.cpp src\translator.cpp src\wt_log.cpp /link winhttp.lib /MAP:build\wt_map2.map
