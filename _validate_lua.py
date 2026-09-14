# -*- coding: utf-8 -*-
# 校验 driver_lua.h 内嵌 Lua（支持多段 raw string）
import re, io, sys
src = io.open('patches/335/src/driver_lua.h', encoding='utf-8').read()
segs = re.findall(r'R"WTCDRIVER\((.*?)\)WTCDRIVER"', src, re.S)
if not segs:
    print("NO CHUNK FOUND"); sys.exit(1)
lua = ''.join(segs)
io.open('_lua_debug.txt', 'w', encoding='utf-8').write(lua)
from luaparser import ast
ast.parse('WTC={}\n' + lua)
print(f"driver Lua VALID: {len(segs)} segment(s), chunk {len(lua)} bytes")
