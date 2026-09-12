import json, urllib.request, sys

URL = "http://localhost:13337/mcp"

def rpc(method, params=None, _id=0):
    payload = {"jsonrpc": "2.0", "id": _id, "method": method}
    if params is not None:
        payload["params"] = params
    req = urllib.request.Request(URL, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode())

try:
    rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                       "clientInfo": {"name": "probe", "version": "1.0"}}, 1)
    print("INIT OK")
except Exception as e:
    print("INIT FAIL:", e)
    sys.exit(1)

tools = rpc("tools/list", {}, 2)
tmap = {t["name"]: t for t in tools.get("result", {}).get("tools", [])}
print("TOOLS:", list(tmap.keys()))

def call_tool(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args}, 3)
    res = r.get("result", {})
    content = res.get("content", [])
    out = []
    for c in content:
        if c.get("type") == "text":
            out.append(c.get("text", ""))
    return "\n".join(out)

# full disassembly of settable 0x84E8D0, wow_register 0x8167E0, index2adr 0x84D9C0
for addr, n in [("0x84E8D0", 60), ("0x8167E0", 80), ("0x84D9C0", 80)]:
    for cand in ("disassemble", "get_disasm", "disasm", "get_disassembly"):
        if cand in tmap:
            try:
                txt = call_tool(cand, {"address": addr, "count": n} if n else {"address": addr})
                print(f"===== {cand} {addr} =====")
                print(txt[:4000])
            except Exception as e:
                print(f"{cand} {addr} err:", e)
            break
    else:
        print(f"no disasm tool for {addr}; available:", list(tmap.keys()))
