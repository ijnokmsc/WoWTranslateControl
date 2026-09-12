// driver_lua.h — 内嵌驱动 Lua（v16 全自治模式）
//
// 经 FrameScript_Execute(0x819210) 一次性注入到全局 Lua 状态，之后完全由客户端
// 事件驱动，不再需要 Interface/AddOns 里的 WoWTranslate 插件：
//   1. 中和旧插件：定义 UnitXP 空桩（WoWTranslate_API.CheckDLL pcall 探测
//      UnitXP("WoWTranslate","ping")=="pong"，桩返回 nil → 旧插件整体惰性化，
//      只显示原文，不与驱动双重翻译）。
//   2. 捕获：hook ChatFrame1..N 的 AddMessage（保留原函数）+ hook ChatFrame_OnEvent
//      记录当前事件频道（EVENT_TO_CHANNEL / SYSTEM_EVENTS 移植自插件 2.0）。
//   3. 翻译：文本段+超链接占位符(http://ph.wt/N)后加 "\1<频道>\1" 标签调
//      WoWTranslate_Translate（标签供控制台 C0-C8 频道开关）。
//   4. 显示：隐藏帧 OnUpdate 每 0.1s 调 WoWTranslate_Poll，按 WTC.displayMode：
//        replace —— 压住原文，译文经原始 AddMessage 原格式写回；错误/超时(30s)回显原文
//        both    —— 原文立即照常显示，译文到达后加 WTC.prefix 另起一行追加
//
// 编码（v15 定案）：TriumvirateWoW = AwesomeWotlk 改件，Lua 字符串是 UTF-8 字节，
// 全程原样透传，不做任何 GBK 转换。
//
// ⚠ 本文件是 C++ raw string：Lua 里不得出现 )" 序列。
#pragma once

#include <string>

namespace wt {

// Lua 字符串字面量转义（单引号包裹）：反斜杠/引号/换行/回车/控制符
inline std::string LuaEscape(const std::string& s)
{
    static const char* hex = "0123456789ABCDEF";
    std::string out;
    out.reserve(s.size() + 8);
    for (size_t i = 0; i < s.size(); ++i)
    {
        unsigned char c = (unsigned char)s[i];
        switch (c)
        {
            case '\\': out += "\\\\"; break;
            case '\'': out += "\\'";  break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (c < 0x20 || c == 0x7F)
                {
                    out += "\\x";
                    out += hex[(c >> 4) & 0xF];
                    out += hex[c & 0xF];
                }
                else
                {
                    out += (char)c;
                }
        }
    }
    return out;
}

inline const char* DriverLuaCode()
{
    return R"WTCDRIVER(
-- v17 就绪门禁：FrameXML 未加载完（NUM_CHAT_WINDOWS/ChatFrame1/DEFAULT_CHAT_FRAME/
-- ChatFrame_OnEvent 任一为 nil）时静默返回，C++ 侧 500ms 后重试；绝不抛 Lua 错误——
-- v16 崩溃根因之一就是注入过早导致 chunk 运行时错误。
if not WTC_DRIVER_LOADED and NUM_CHAT_WINDOWS and DEFAULT_CHAT_FRAME and ChatFrame_OnEvent and ChatFrame1 then
WTC_DRIVER_LOADED = true

local function WTC_MAIN()

-- ---- 中和旧 WoWTranslate 插件（其 API 层依赖 GS DLL 的 UnitXP 桥）----
if UnitXP == nil then
  UnitXP = function() return nil end
end

local WTC_CFG = WTC or {}
local displayMode = tostring(WTC_CFG.displayMode or "replace")
local dispPrefix = tostring(WTC_CFG.prefix or "[译]")
-- 外发翻译：off=关闭 | replace=只发英文 | both=原文+英文都发
local outgoingMode = tostring(WTC_CFG.outgoing or "off")

local CHANNELS = {
  CHAT_MSG_SAY = "SAY", CHAT_MSG_YELL = "YELL", CHAT_MSG_WHISPER = "WHISPER",
  CHAT_MSG_PARTY = "PARTY", CHAT_MSG_GUILD = "GUILD", CHAT_MSG_OFFICER = "GUILD",
  CHAT_MSG_RAID = "RAID", CHAT_MSG_RAID_LEADER = "RAID", CHAT_MSG_RAID_WARNING = "RAID",
  CHAT_MSG_BATTLEGROUND = "BATTLEGROUND", CHAT_MSG_BATTLEGROUND_LEADER = "BATTLEGROUND",
  CHAT_MSG_CHANNEL = "CHANNEL",
}
local SYSTEM_EVENTS = {
  CHAT_MSG_SYSTEM = true, CHAT_MSG_EMOTE = true, CHAT_MSG_TEXT_EMOTE = true,
  CHAT_MSG_MONSTER_SAY = true, CHAT_MSG_MONSTER_YELL = true,
  CHAT_MSG_MONSTER_EMOTE = true, CHAT_MSG_MONSTER_WHISPER = true,
  CHAT_MSG_CHANNEL_JOIN = true, CHAT_MSG_CHANNEL_LEAVE = true,
  CHAT_MSG_LOOT = true, CHAT_MSG_MONEY = true, CHAT_MSG_OPENING = true,
  CHAT_MSG_SKILL = true, CHAT_MSG_COMBAT_HONOR_GAIN = true,
  CHAT_MSG_COMBAT_XP_GAIN = true, CHAT_MSG_COMBAT_MISC_INFO = true,
}

local curChannel, curSystem = nil, false
local pending, counter = {}, 0
local outPending, outCounter = {}, 0
local origSend = nil

-- v19 诊断旗标（各只回报一次，进 DLL 日志定位链路断点）
local dbgHooked, dbgCapture, dbgCjk = false, false, 0   -- dbgCjk 是计数器（false 无法比较）
local dbgQueueErr, dbgTransOk, dbgTransErr, dbgTimeout, dbgDisplayed = false, false, false, false, false
local dbgOutOk, dbgOutErr, dbgOutSkip = false, false, false

local function HasLatin(t)
  return string.find(t, "%a") ~= nil
end

-- UTF-8 CJK 检测：CJK 统一表意区 U+4E00..U+9FFF 的首字节落在 0xE4..0xE9
local function HasCJK(t)
  for i = 1, string.len(t) do
    local b = string.byte(t, i)
    if b >= 0xE4 and b <= 0xE9 then return true end
  end
  return false
end

-- 超链接查找：|cXXXXXXXX? |H<id>|h[<显示文本>]|h |r?
local function FindAllLinks(text)
  local links = {}
  local pos = 1
  local n = string.len(text)
  while pos <= n do
    local h1, h2 = string.find(text, "|H", pos, true)
    if not h1 then break end
    local start = h1
    local c1, c2 = string.find(text, "|c%x%x%x%x%x%x%x%x", pos)
    if c1 and c2 + 1 == h1 then start = c1 end
    local d1 = string.find(text, "|h[", h2 + 1, true)
    if not d1 then
      pos = h2 + 1
    else
      local d2 = string.find(text, "%]|h", d1 + 3)
      if not d2 then
        pos = h2 + 1
      else
        local e = d2 + 2
        if string.sub(text, e + 1, e + 2) == "|r" then e = e + 2 end
        table.insert(links, { s = start, e = e, c = string.sub(text, start, e) })
        pos = e + 1
      end
    end
  end
  return links
end

local function SplitSegs(text)
  local links = FindAllLinks(text)
  local segs = {}
  if table.getn(links) == 0 then
    table.insert(segs, { t = "text", c = text })
    return segs
  end
  local last = 0
  for _, l in ipairs(links) do
    if l.s > last + 1 then
      table.insert(segs, { t = "text", c = string.sub(text, last + 1, l.s - 1) })
    end
    table.insert(segs, { t = "link", c = l.c })
    last = l.e
  end
  if last < string.len(text) then
    table.insert(segs, { t = "text", c = string.sub(text, last + 1) })
  end
  return segs
end

local function BuildText(segs)
  local parts, n = {}, 0
  for _, s in ipairs(segs) do
    if s.t == "text" then
      table.insert(parts, s.c)
    else
      n = n + 1
      table.insert(parts, "http://ph.wt/" .. n)
    end
  end
  return table.concat(parts, "")
end

local function Reconstruct(segs, translated)
  local links = {}
  for _, s in ipairs(segs) do
    if s.t == "link" then table.insert(links, s.c) end
  end
  if table.getn(links) == 0 then return translated end
  for i = 1, table.getn(links) do
    local phs = { "http://ph.wt/" .. i, "https://ph.wt/" .. i,
                  "http://ph .wt/" .. i, "http: //ph.wt/" .. i }
    for _, p in ipairs(phs) do
      local a, b = string.find(translated, p, 1, true)
      if a then
        translated = string.sub(translated, 1, a - 1) .. links[i] .. string.sub(translated, b + 1)
        break
      end
    end
  end
  return translated
end
)WTCDRIVER"
R"WTCDRIVER(
-- DLL Poll 返回 {"id":"..","translation":"..","error":".."} 的最小 JSON 字符串读取
local function JsonGetString(json, key)
  if not json or not key then return nil end
  local ks, ke = string.find(json, "\"" .. key .. "\"", 1, true)
  if not ks then return nil end
  local colon = string.find(json, ":", ke + 1, true)
  if not colon then return nil end
  local i = colon + 1
  while i <= string.len(json) do
    local ch = string.sub(json, i, i)
    if ch ~= " " and ch ~= "\t" and ch ~= "\n" and ch ~= "\r" then break end
    i = i + 1
  end
  if string.sub(json, i, i) ~= "\"" then return nil end
  local out, j = "", i + 1
  while j <= string.len(json) do
    local ch = string.sub(json, j, j)
    if ch == "\\" and j < string.len(json) then
      local nc = string.sub(json, j + 1, j + 1)
      if nc == "\"" then out = out .. "\""
      elseif nc == "\\" then out = out .. "\\"
      elseif nc == "/" then out = out .. "/"
      elseif nc == "n" then out = out .. "\n"
      elseif nc == "r" then out = out .. "\r"
      elseif nc == "t" then out = out .. "\t"
      elseif nc == "u" then
        -- \uXXXX → UTF-8 裸字节（纵深防御：即使服务端转义也能正确显示，v23 前是丢弃成 ?）
        local cp = tonumber(string.sub(json, j + 2, j + 5), 16)
        if cp and cp >= 32 then
          if cp < 0x80 then
            out = out .. string.char(cp)
          elseif cp < 0x800 then
            out = out .. string.char(0xC0 + math.floor(cp / 64), 0x80 + cp % 64)
          else
            out = out .. string.char(0xE0 + math.floor(cp / 4096),
              0x80 + math.floor(cp / 64) % 64, 0x80 + cp % 64)
          end
        end
        j = j + 4
      else out = out .. nc end
      j = j + 2
    elseif ch == "\"" then
      return out
    else
      out = out .. ch
      j = j + 1
    end
  end
  return out
end
)WTCDRIVER"
R"WTCDRIVER(
-- 移植部署版 GS 插件 v2.3 的 StripChatPrefix：剥掉行首 [频道] [玩家]: 头
--（超链接或纯文本变体，含 |c 色码），只把正文送翻译——链接/名字进模型只会诱发
-- 幻觉（v20 实测：模型给译文加了 ？频道? 前缀）。返回剥完后的正文，空串=无可译。
-- ⚠ 必须定义在 HandleIncoming 之前：local 函数定义顺序=可见性（v22 曾因放后面
--   在 HandleIncoming 里落空成全局 nil，游戏内报 attempt to call global）。
local function StripChatPrefixBody(text)
  if not text then return "", "" end
  -- 全角冒号归一化：zhCN 聊天格式用 ：，统一成 : 后所有模式可复用
  local w = string.gsub(text, "：" , ":")
  local hadChannel = false
  local nameConsumed = false
  w = string.gsub(w, "^%s+", "")
  w = string.gsub(w, "^|c%x%x%x%x%x%x%x%x", "")
  w = string.gsub(w, "^%s+", "")
  -- 频道头：超链接形态（自定义频道）或 纯文本 [频道名] 形态（本地防御/寻求组队等系统频道）
  local t2 = string.gsub(w, "^|Hchannel:[^|]-|h.-|h", "")
  if t2 ~= w then
    w = t2
    hadChannel = true
  else
    t2 = string.gsub(w, "^%[[^%]]-%]%s+%[[^%]]-%]%s*:%s*", "")
    if t2 ~= w then
      w = t2
      hadChannel = true
      nameConsumed = true
    else
      t2 = string.gsub(w, "^%[[^%]]-%]%s+[%w_%-]+%s*:%s*", "")
      if t2 ~= w then
        w = t2
        hadChannel = true
        nameConsumed = true
      end
    end
  end
  w = string.gsub(w, "^%s+", "")
  w = string.gsub(w, "^|c%x%x%x%x%x%x%x%x", "")
  w = string.gsub(w, "^|r", "")
  -- 发送者：超链接形态 或 [名字]: 形态
  local t3 = string.gsub(w, "^|Hplayer:[^|]-|h.-|h", "")
  if t3 ~= w then
    w = t3
    nameConsumed = true
  else
    t3 = string.gsub(w, "^%[[^%]]-%]%s*:%s*", "")
    if t3 ~= w then
      w = t3
      nameConsumed = true
    else
      t3 = string.gsub(w, "^%[[^%]]-%]|r%s*:%s*", "")
      if t3 ~= w then
        w = t3
        nameConsumed = true
      end
    end
  end
  w = string.gsub(w, "^|r", "")
  w = string.gsub(w, "^%s*:%s*", "")
  if hadChannel and not nameConsumed then
    w = string.gsub(w, "^[%w_%-]+%s*:%s*", "")
  end
  -- zhCN 动词前缀（"说：/大喊：/密语："…）：名字剥掉后残留的纯中文短前缀+冒号。
  -- 限定"纯中文且 <=12 字节"——含 ASCII 的正文（如 hello:world）绝不误伤
  if nameConsumed then
    local frag = string.match(w, "^(.-):")
    if frag and #frag <= 12 and frag ~= "" and not string.find(frag, "[a-zA-Z0-9]") then
      w = string.sub(w, #frag + 2)
      w = string.gsub(w, "^%s+", "")
    end
  end
  w = string.gsub(w, "^%s+", "")
  -- body 是 w 的后缀：prefix 从 w 头部取（归一化后的头，显示时 ： 变 : 属可接受差异）
  local body = w
  local prefix = string.sub(w, 1, #w - #body)
  return body, prefix
end

local function Passthrough(frame, orig, text, r, g, b, id, hold)
  return orig(frame, text, r, g, b, id, hold)
end

local function HandleIncoming(frame, orig, text, r, g, b, id, hold)
  if not text then return Passthrough(frame, orig, text, r, g, b, id, hold) end
  if not curChannel or curSystem then
    return Passthrough(frame, orig, text, r, g, b, id, hold)
  end
  -- 自己发出的消息回显不翻（外发英文译文回显时防 EN→ZH 重翻绕一圈）
  if UnitName and UnitName("player") then
    local me = UnitName("player")
    if me and me ~= "" and string.find(text, "|Hplayer:" .. me .. ":", 1, true) then
      return Passthrough(frame, orig, text, r, g, b, id, hold)
    end
  end
  -- 先剥 [频道] [玩家]: 头，正文才做语言判断与翻译（头里的中文频道名不该触发跳过）
  local body, prefix = StripChatPrefixBody(text)
  if body == "" then
    return Passthrough(frame, orig, text, r, g, b, id, hold)
  end
  -- 正文已含中文或纯符号/数字 → 不送翻（首次记 diag 证明捕获链路是通的）
  if HasCJK(body) or not HasLatin(body) then
    if dbgCjk < 3 then
      dbgCjk = dbgCjk + 1
      WoWTranslate_Diag("WTC_SKIP_CJK ch=" .. tostring(curChannel) .. " body=" .. string.sub(body, 1, 80))
    end
    return Passthrough(frame, orig, text, r, g, b, id, hold)
  end
  local segs = SplitSegs(body)
  local toSend = BuildText(segs)
  if toSend == "" then
    return Passthrough(frame, orig, text, r, g, b, id, hold)
  end
  if not dbgCapture then
    dbgCapture = true
    -- 取证：原始 AddMessage 全文（含超链接原始字节，定位乱码来源）
    WoWTranslate_Diag("WTC_RAW ch=" .. tostring(curChannel) ..
      " text=" .. string.sub(text, 1, 200))
    WoWTranslate_Diag("WTC_CAPTURE ch=" .. tostring(curChannel) ..
      " send=" .. string.sub(toSend, 1, 60))
  end
  counter = counter + 1
  local mid = tostring(counter)
  local both = (displayMode == "both")
  pending[mid] = { frame = frame, orig = orig, text = text, segs = segs,
                   prefix = prefix,
                   r = r, g = g, b = b, id = id, hold = hold,
                   t = GetTime(), done = both }
  local ok = pcall(function()
    local r = WoWTranslate_Translate("\1" .. curChannel .. "\1" .. toSend, "en", "zh", mid)
    if r ~= "ok" then
      WoWTranslate_Diag("WTC_QUEUERR " .. tostring(r))
    end
  end)
  if not ok then
    pending[mid] = nil
    return Passthrough(frame, orig, text, r, g, b, id, hold)
  end
  if both then
    orig(frame, text, r, g, b, id, hold)
  end
end

)WTCDRIVER"
R"WTCDRIVER(
-- ---- hook 聊天框 ----
local hookedCount = 0
for i = 1, NUM_CHAT_WINDOWS do
  local f = getglobal("ChatFrame" .. i)
  if f and f.AddMessage and not f.WTCDirectHooked then
    f.WTCDirectHooked = true
    hookedCount = hookedCount + 1
    local orig = f.AddMessage
    f.AddMessage = function(self, text, r, g, b, id, hold)
      HandleIncoming(self, orig, text, r, g, b, id, hold)
    end
  end
end
WoWTranslate_Diag("WTC_HOOKED windows=" .. tostring(NUM_CHAT_WINDOWS) .. " hooked=" .. tostring(hookedCount))

-- ---- hook ChatFrame_OnEvent 记录频道（AddMessage 都发生在 origOnEvent 内部）----
-- ⚠ 本客户端（3.3.5 AwesomeWotlk，与部署的 GS 插件 v2.3 一致）：以 (self, event, ...)
-- 调用——vendor 1.12 版签名 function(event) 曾致 event=聊天框对象 → 频道永远 nil →
-- 驱动加载成功但零捕获（v19 实测症状）。
local origOnEvent = ChatFrame_OnEvent
ChatFrame_OnEvent = function(self, event, ...)
  curChannel = CHANNELS[event]
  curSystem = SYSTEM_EVENTS[event] == true
  local res = origOnEvent(self, event, ...)
  curChannel = nil
  curSystem = false
  return res
end

-- ---- hook SendChatMessage：外发中文 → 英文（\1ZH2EN\1 标签走控制台方向路由）----
local OUT_TYPES = {
  SAY = true, YELL = true, WHISPER = true, PARTY = true, GUILD = true,
  OFFICER = true, RAID = true, RAID_WARNING = true, BATTLEGROUND = true,
  CHANNEL = true,
}
origSend = SendChatMessage
SendChatMessage = function(msg, chatType, language, channel)
  if outgoingMode == "off" or not msg or msg == "" or not HasCJK(msg)
    or not chatType or not OUT_TYPES[chatType] then
    return origSend(msg, chatType, language, channel)
  end
  outCounter = outCounter + 1
  local oid = "out_" .. outCounter
  outPending[oid] = { msg = msg, chatType = chatType,
                      language = language, channel = channel, t = GetTime() }
  local ok = pcall(function()
    WoWTranslate_Translate("\1ZH2EN\1" .. msg, "zh", "en", oid)
  end)
  if not ok then
    outPending[oid] = nil
    return origSend(msg, chatType, language, channel)
  end
  if not dbgOutSkip then
    dbgOutSkip = true
    WoWTranslate_Diag("WTC_OUTCAPTURE mode=" .. outgoingMode)
  end
  if outgoingMode == "both" then
    origSend(msg, chatType, language, channel)   -- 原文立即发出，英文随后
  end
  -- replace 模式：压住不发，等译文（10s 超时发原文，见轮询循环）
end

-- ---- 轮询帧：Poll 译文 + 30s 超时兜底 ----
local pollAcc = 0
local pollFrame = CreateFrame("Frame")
pollFrame:SetScript("OnUpdate", function(self, elapsed)
  pollAcc = pollAcc + elapsed
  if pollAcc < 0.1 then return end
  pollAcc = 0
  for _ = 1, 16 do
    local ok, j = pcall(WoWTranslate_Poll)
    if not ok or not j or j == "" then break end
    local id = JsonGetString(j, "id")
    local tr = JsonGetString(j, "translation") or ""
    local er = JsonGetString(j, "error") or ""
    -- 外发翻译结果（out_N id）：replace 模式在此刻补发英文
    if id and string.sub(id, 1, 4) == "out_" then
      local o = outPending[id]
      if o then
        outPending[id] = nil
        local sendText = o.msg
        if er == "" and tr ~= "" then sendText = tr end
        pcall(function() origSend(sendText, o.chatType, o.language, o.channel) end)
        if not dbgOutOk then
          dbgOutOk = true
          WoWTranslate_Diag("WTC_OUTOK id=" .. id .. " send=" .. string.sub(sendText, 1, 60))
        end
      end
    else
      local p = id and pending[id] or nil
      if p and not p.done then
      p.done = true
      pending[id] = nil
      if er ~= "" then
        WoWTranslate_Diag("WTC_TRANSERR id=" .. id .. " err=" .. string.sub(er, 1, 80))
        if displayMode ~= "both" then
          p.orig(p.frame, p.text, p.r, p.g, p.b, p.id, p.hold)
        end
      else
        if not dbgTransOk then
          dbgTransOk = true
          WoWTranslate_Diag("WTC_TRANSOK id=" .. id)
        end
        local finalText = Reconstruct(p.segs, tr)
        if not dbgDisplayed then
          dbgDisplayed = true
          WoWTranslate_Diag("WTC_DISPLAY final=" .. string.sub(p.prefix .. finalText, 1, 200))
        end
        if displayMode == "both" then
          p.orig(p.frame, p.prefix .. dispPrefix .. finalText, p.r, p.g, p.b, p.id, p.hold)
        else
          p.orig(p.frame, p.prefix .. finalText, p.r, p.g, p.b, p.id, p.hold)
        end
      end
      end
    end
  end
  local now = GetTime()
  for mid, p in pairs(pending) do
    if now - p.t > 30 then
      pending[mid] = nil
      if not p.done then
        WoWTranslate_Diag("WTC_TIMEOUT id=" .. mid .. " (translation never returned)")
        p.orig(p.frame, p.text, p.r, p.g, p.b, p.id, p.hold)
      end
    end
  end
  -- 外发翻译 10s 超时：replace 模式发原文兜底（both 已发过原文，直接丢弃）
  for oid, o in pairs(outPending) do
    if now - o.t > 10 then
      outPending[oid] = nil
      if outgoingMode == "replace" then
        pcall(function() origSend(o.msg, o.chatType, o.language, o.channel) end)
      end
      if not dbgOutErr then
        dbgOutErr = true
        WoWTranslate_Diag("WTC_OUTTIMEOUT id=" .. oid)
      end
    end
  end
end)

DEFAULT_CHAT_FRAME:AddMessage("|cFF00CCFF[WTC]|r Direct driver v24 loaded (mode=" ..
  displayMode .. (displayMode == "both" and (", prefix=" .. dispPrefix) or "") ..
  ", outgoing=" .. outgoingMode .. ")")

end  -- WTC_MAIN

-- pcall 包裹：任何 Lua 错误都被限制在本 chunk 内（错误消息经合法 chunkname 格式化，
-- 客户端栈保持平衡），结果经 Diag 回报 C++（决定重试还是完成）
local ok, err = pcall(WTC_MAIN)
WoWTranslate_Diag(ok and "WTC_DRIVER_OK" or ("WTC_DRIVER_FAIL: " .. tostring(err)))

end
)WTCDRIVER";
}

} // namespace wt
