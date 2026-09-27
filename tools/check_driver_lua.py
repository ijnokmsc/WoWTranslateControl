"""driver_lua.h 离线配平检查（一次性诊断脚本，不入库构建）。

1. 从 driver_lua.h 提取全部 R"WTCDRIVER( ... )WTCDRIVER" 原始字符串块并拼接；
2. 模拟 direct_main.cpp RebuildDriverChunk 的 WTC 配置前缀（LuaEscape 含换行样本）；
3. 剥掉 Lua 注释与字符串后做块（function/if/for/while/do/repeat）与括号配平。
"""
import re
import sys

H = open(r"D:\GitHub\WoWTranslateControl\patches\335\src\driver_lua.h",
         encoding="utf-8").read()

chunks = re.findall(r'R"WTCDRIVER\((.*?)\)WTCDRIVER"', H, re.S)
assert chunks, "no driver chunks found"
driver = "".join(chunks)


def lua_escape(s: str) -> str:
    out = []
    for c in s:
        if c == "\\":
            out.append("\\\\")
        elif c == "'":
            out.append("\\'")
        elif c == "\n":
            out.append("\\n")
        elif c == "\r":
            out.append("\\r")
        elif c == "\t":
            out.append("\\t")
        else:
            out.append(c)
    return "".join(out)


# RebuildDriverChunk 输出形态：outfilter 样本故意带引号/反斜杠/换行
sample_filter = ".\n。\n^%!rule'with\\quotes\n/say"
config_line = ("WTC={displayMode='replace',prefix='[译]',outgoing='replace',"
               f"outTimeout=25,outfilter='{lua_escape(sample_filter)}',outoff='WHISPER'}}\n")

for label, code in (("driver-only", driver), ("full-chunk", config_line + driver)):
    # ---- 剥注释与字符串 ----
    out, i, n = [], 0, len(code)
    while i < n:
        c = code[i]
        if code.startswith("--", i):
            if code.startswith("--[==[", i) or code.startswith("--[[", i):
                closer = code[i:i+4].replace("--", "]]", 1)
                j = code.find(closer, i)
                i = (j + len(closer)) if j != -1 else n
            else:
                j = code.find("\n", i)
                i = j if j != -1 else n
            continue
        if c in "\"'":
            q, i = c, i + 1
            while i < n:
                if code[i] == "\\":
                    i += 2
                elif code[i] == q:
                    i += 1
                    break
                else:
                    i += 1
            out.append(" STR ")
            continue
        out.append(c)
        i += 1
    stripped = "".join(out)

    # ---- 块/括号配平 ----
    stripped2 = re.sub(r"\b(function|if|for|while|do|repeat|end|until|elseif|else)\b",
                       r" \1 ", stripped)
    tokens = re.findall(r"[A-Za-z_]+|[{}()\[\]]", stripped2)
    depth = 0
    pending_do = False
    errs = []
    stack = []
    for t in tokens:
        if t in ("for", "while"):
            pending_do = True
            depth += 1
            stack.append(t)
        elif t == "do":
            if pending_do:
                pending_do = False      # 属于 for/while 头，不另计
            else:
                depth += 1
                stack.append(t)
        elif t == "function":
            depth += 1
            stack.append(t)
        elif t == "if":
            depth += 1
            stack.append(t)
        elif t == "repeat":
            depth += 1
            stack.append(t)
        elif t in ("end", "until"):
            depth -= 1
            if stack:
                stack.pop()
            if depth < 0:
                errs.append("negative depth")
        elif t in "{}()[]":
            opener = {"}": "{", ")": "(", "]": "["}.get(t)
            if opener:
                if not stack or stack[-1] != opener:
                    errs.append(f"mismatch close {t}")
                else:
                    stack.pop()
            else:
                stack.append(t)
    status = "OK" if depth == 0 and not errs else f"FAIL depth={depth} errs={errs[:5]} tail={stack[-3:]}"
    print(f"{label:12s} {status}")
    if depth != 0 or errs:
        sys.exit(1)

# 额外确认：新钩子的关键片段都在
for probe in ("ShouldSkipOutgoing", "OUT_OFF[chatType]", "WTC_OUTSKIP",
              "outfilter='", "driver v41", "3.1.0"):
    print(f"probe {probe!r}: {'found' if (probe in driver or probe in H) else 'MISSING'}")
