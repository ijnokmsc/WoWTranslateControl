# -*- coding: utf-8 -*-
# Check whether the hardcoded VA table from direct_main.cpp still holds in a second client exe.
# Maps VAs to file offsets via PE section headers and compares prologue bytes.
import struct, sys, hashlib

ADDRS = [
    (0x84DBD0, "lua_gettop",       "55 8B EC 8B 4D 08 8B 41 0C 2B 41 10"),
    (0x84DBF0, "lua_settop",       "55 8B EC"),
    (0x84E350, "lua_pushstring",   "55 8B EC"),
    (0x84E400, "lua_pushcclosure", "55 8B EC"),
    (0x84E600, "lua_rawget",       "55 8B EC 8B 45 0C 56 8B 75 08 8B CE E8 AF F3 FF FF"),
    (0x84E670, "lua_getfield",     "55 8B EC 8B 45 0C 56 8B 75 08 8B CE E8 3F F3 FF FF"),
    (0x84E8D0, "lua_settable",     "55 8B EC 8B 45 0C 56 8B 75 08 8B CE E8 DF F0 FF FF"),
    (0x84E0E0, "lua_tolstring",    "55 8B EC"),
    (0x84DF60, "lua_isstring",     "55 8B EC"),
    (0x84DF20, "lua_isnumber",     "55 8B EC"),
    (0x84E030, "lua_tonumber",     "55 8B EC"),
    (0x84E280, "lua_pushnil",      "55 8B EC"),
    (0x84EC50, "lua_pcall",        "55 8B EC"),
    (0x819210, "FrameScript_Execute", "55 8B EC"),
]
BASE = 0x400000

def pe_map(path):
    data = open(path, 'rb').read()
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    nsec = struct.unpack_from('<H', data, pe+6)[0]
    optsz = struct.unpack_from('<H', data, pe+20)[0]
    ts = struct.unpack_from('<I', data, pe+8)[0]
    secs = []
    off = pe + 24 + optsz
    for i in range(nsec):
        name, vsize, vaddr, rawsize, rawoff = struct.unpack_from('<8sIIII', data, off+i*40)
        secs.append((name.rstrip(b'\0').decode(), vaddr, vsize, rawoff, rawsize))
    return data, secs, ts

def va2off(secs, va):
    rva = va - BASE
    for name, vaddr, vsize, rawoff, rawsize in secs:
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            fo = rva - vaddr + rawoff
            if fo < rawoff + rawsize:
                return fo
    return None

for path in [r"D:\Games\TriumvirateWoW\Wow.exe", r"D:\Games\GrimfallWoW\Wotlk\Wow.exe"]:
    data, secs, ts = pe_map(path)
    print("=" * 60)
    print(path)
    print("  PE TimeDateStamp: %d (%s)" % (ts, __import__('datetime').datetime.utcfromtimestamp(ts) if ts else '?'))
    ok = 0
    for va, name, sig in ADDRS:
        want = bytes.fromhex(sig.replace(' ', ''))
        off = va2off(secs, va)
        got = data[off:off+len(want)] if off is not None else b''
        mark = 'OK ' if got == want else 'DIFF'
        if got == want: ok += 1
        print("  %-20s %08X  %-4s got=%s" % (name, va, mark, got.hex(' ')[:48]))
    print("  => %d/%d match" % (ok, len(ADDRS)))
