# -*- coding: utf-8 -*-
# dinput8 代理加载链冒烟：临时目录放 dinput8.dll + WoWTranslateDirect.dll + dlls.txt，
# 宿主 LoadLibrary("dinput8.dll") → 3s 后应能 GetModuleHandle 到引擎 DLL。
import os, shutil, subprocess, textwrap

tdir = r'D:\GitHub\WoWTranslateControl\patches\335\build\proxy_test'
os.makedirs(tdir, exist_ok=True)
a = r'D:\GitHub\WoWTranslateControl\assets\direct-dll'
shutil.copy(os.path.join(a, 'dinput8.dll'), tdir)
shutil.copy(os.path.join(a, 'WoWTranslateDirect.dll'), tdir)
with open(os.path.join(tdir, 'dlls.txt'), 'w') as f:
    f.write('WoWTranslateDirect.dll\n')

host = os.path.join(tdir, 'proxy_host.cpp')
exe = os.path.join(tdir, 'proxy_host.exe')
open(host, 'w').write(textwrap.dedent('''
    #include <windows.h>
    #include <cstdio>
    int main() {
        HMODULE h = LoadLibraryA("dinput8.dll");
        if (!h) { printf("load dinput8 proxy failed %lu\\n", GetLastError()); return 1; }
        Sleep(3000);
        HMODULE e = GetModuleHandleA("WoWTranslateDirect.dll");
        printf("engine loaded: %s\\n", e ? "YES" : "NO");
        return e ? 0 : 2;
    }
''').strip())

import subprocess, os
clroot = r'E:\炫彩IDE\data\VC\VC2022\14.41.34120'
sdk = r'E:\炫彩IDE\data\VC\Windows Kits\10'
ver = '10.0.19041.0'
j = os.path.join
env = os.environ.copy()
env['INCLUDE'] = ';'.join([j(clroot,'include'), j(sdk,'Include',ver,'um'), j(sdk,'Include',ver,'shared'), j(sdk,'Include',ver,'ucrt')])
env['LIB'] = ';'.join([j(clroot,'lib','x86'), j(sdk,'Lib',ver,'um','x86'), j(sdk,'Lib',ver,'ucrt','x86')])
r = subprocess.run([j(clroot,'bin','Hostx86','x86','cl.exe'), '/nologo', '/O2', '/MT', '/Fe:'+exe, host],
                   capture_output=True, env=env, cwd=tdir)
print('build rc=', r.returncode, r.stdout.decode('gbk', errors='replace'))
r = subprocess.run([exe], capture_output=True, cwd=tdir, timeout=30)
print(r.stdout.decode('gbk', errors='replace'))
print('RC=', r.returncode)
