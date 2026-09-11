# -*- coding: utf-8 -*-
import subprocess, os

clroot = r'E:\炫彩IDE\data\VC\VC2022\14.41.34120'
sdk = r'E:\炫彩IDE\data\VC\Windows Kits\10'
ver = '10.0.19041.0'
j = os.path.join

env = os.environ.copy()
env['INCLUDE'] = ';'.join([j(clroot, 'include'), j(sdk, 'Include', ver, 'um'),
                           j(sdk, 'Include', ver, 'shared'), j(sdk, 'Include', ver, 'ucrt')])
env['LIB'] = ';'.join([j(clroot, 'lib', 'x86'), j(sdk, 'Lib', ver, 'um', 'x86'),
                       j(sdk, 'Lib', ver, 'ucrt', 'x86')])

bdir = r'D:\GitHub\WoWTranslateControl\patches\335\build'
src = r'D:\GitHub\WoWTranslateControl\patches\335\src\selftest_host.cpp'

r = subprocess.run([j(clroot, 'bin', 'Hostx86', 'x86', 'cl.exe'), '/nologo', '/utf-8', '/O2', '/MT',
                    '/Fe:' + j(bdir, 'selftest_host.exe'), src],
                   capture_output=True, env=env, cwd=bdir)
print(r.stdout.decode('gbk', errors='replace'))
print('BUILD RC=', r.returncode)

if r.returncode == 0:
    r = subprocess.run([j(bdir, 'selftest_host.exe')], capture_output=True, cwd=bdir, timeout=30)
    print(r.stdout.decode('gbk', errors='replace'))
    print('RUN RC=', r.returncode)
