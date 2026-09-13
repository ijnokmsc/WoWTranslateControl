// driver_lua.h — 内嵌驱动 Lua（v28 过滤式捕获）
//
// 经 FrameScript_Execute(0x819210) 一次性注入到全局 Lua 状态。
// v28 架构：用官方 ChatFrame_AddMessageEventFilter 拦截聊天消息——任何聊天 UI
// （巨龙UI/EUI/原生）都走这条标准链，不再依赖 AddMessage 钩子与头格式猜测。
// filter 直接拿到纯消息正文与发送者，replace 模式 return true 压住原文。
//
// Lua 内容里的反斜杠一律用 @BS@ 类占位符由 _gen_driver2.py 生成，禁止手写。
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


-- v28 过滤式捕获：用官方 ChatFrame_AddMessageEventFilter 拦截聊天消息——
-- 任何聊天 UI（巨龙UI/EUI/原生）都走这条标准链，不再依赖 AddMessage 钩子
-- 与头格式猜测。filter 拿到的是纯消息正文（头是显示期才拼的）。
-- 就绪门禁：FrameXML 未加载完时静默返回，C++ 侧 500ms 后重试。
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
local outTimeout = tonumber(WTC_CFG.outTimeout or 25)

local pending, counter = {}, 0
local outPending, outCounter = {}, 0
local origSend = SendChatMessage
local dbg = { hooked = 0, raw = 0, skip = 0, qerr = 0, ok = 0, terr = 0, to = 0, oto = 0 }

local function HasLatin(t)
  return string.find(t, "\a") ~= nil
end

-- UTF-8 CJK 检测：CJK 统一表意区 U+4E00..U+9FFF 的首字节落在 0xE4..0xE9
local function HasCJK(t)
  for i = 1, string.len(t) do
    local b = string.byte(t, i)
    if b >= 0xE4 and b <= 0xE9 then return true end
  end
  return false
end

-- 消息事件 → 频道标签（供控制台 C0-C8 频道开关与方向路由）
local EVENTS = {
  CHAT_MSG_SAY = "SAY", CHAT_MSG_YELL = "YELL", CHAT_MSG_WHISPER = "WHISPER",
  CHAT_MSG_PARTY = "PARTY", CHAT_MSG_PARTY_LEADER = "PARTY",
  CHAT_MSG_GUILD = "GUILD", CHAT_MSG_OFFICER = "GUILD",
  CHAT_MSG_RAID = "RAID", CHAT_MSG_RAID_LEADER = "RAID", CHAT_MSG_RAID_WARNING = "RAID",
  CHAT_MSG_BATTLEGROUND = "BATTLEGROUND", CHAT_MSG_BATTLEGROUND_LEADER = "BATTLEGROUND",
  CHAT_MSG_CHANNEL = "CHANNEL",
}

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
      if nc == "\\" then out = out .. "\\"
      elseif nc == "\\" then out = out .. "\\"
      elseif nc == "/" then out = out .. "/"
      elseif nc == "n" then out = out .. "\n"
      elseif nc == "r" then out = out .. "\r"
      elseif nc == "t" then out = out .. "\t"
      elseif nc == "u" then
        -- \uXXXX → UTF-8 裸字节（纵深防御）
        local cp = tonumber(string.sub(json, j + 2, j + 5), 16)
        if cp and cp >= 32 then
          if cp < 0x80 then out = out .. string.char(cp)
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

-- ---- 消息过滤器（捕获入口）----
-- 约定：replace 模式 return true 压住原文；both 模式 return false 放行原文；
-- 送翻失败（队列满/未配置）也放行，绝不吞消息。
local function WTCFilter(chatFrame, event, msg, sender, ...)
  local ch = EVENTS[event]
  if not ch or not msg or msg == "" then return false end
  -- 自己的消息回显不处理（外发英文回显防重翻）
  local me = UnitName and UnitName("player")
  if me and me ~= "" and sender == me then return false end
  -- 已含中文或纯符号/数字 → 不送翻
  if HasCJK(msg) or not HasLatin(msg) then
    if dbg.skip < 3 and HasCJK(msg) then
      dbg.skip = dbg.skip + 1
      WoWTranslate_Diag("WTC_SKIP_CJK ch=" .. ch .. " body=" .. string.sub(msg, 1, 60))
    end
    return false
  end
  if dbg.raw < 10 then
    dbg.raw = dbg.raw + 1
    WoWTranslate_Diag("WTC_RAW #" .. dbg.raw .. " ch=" .. ch ..
      " sender=" .. tostring(sender) .. " text=" .. string.sub(msg, 1, 160))
  end
  counter = counter + 1
  local mid = "m" .. counter
  local both = (displayMode == "both")
  local ci = ChatTypeInfo[string.sub(event, 10)]
  pending[mid] = { frame = chatFrame, sender = tostring(sender or ""),
                   text = msg, r = ci and ci.r, g = ci and ci.g, b = ci and ci.b,
                   t = GetTime(), done = both }
  local ok = pcall(function()
    local r = WoWTranslate_Translate("\1" .. ch .. "\1" .. msg, "en", "zh", mid)
    if r ~= "ok" then
      WoWTranslate_Diag("WTC_QUEUERR " .. tostring(r))
    end
  end)
  if not ok then
    pending[mid] = nil
    return false
  end
  if both then
    return false   -- 原文照常显示，译文随后追加
  end
  return true       -- replace 模式：压住原文，译文到达后显示
end

-- ---- 注册过滤器到所有聊天框（新聊天框由轮询补挂）----
local filterCount = 0
local function RegisterFilters()
  local n = 0
  for i = 1, NUM_CHAT_WINDOWS do
    local f = getglobal("ChatFrame" .. i)
    if f then
      f.WTCFiltered = f.WTCFiltered or {}
      for ev in pairs(EVENTS) do
        if not f.WTCFiltered[ev] then
          ChatFrame_AddMessageEventFilter(ev, WTCFilter)
          f.WTCFiltered[ev] = true
          n = n + 1
        end
      end
    end
  end
  if n > 0 then
    filterCount = filterCount + n
    WoWTranslate_Diag("WTC_FILTERS +" .. n .. " (total=" .. filterCount .. ")")
  end
end
RegisterFilters()

-- ---- 轮询帧：Poll 译文 + 超时兜底 + 新聊天框补挂 ----
local pollAcc = 0
local rehookAcc = 0
local pollFrame = CreateFrame("Frame")
pollFrame:SetScript("OnUpdate", function(self, elapsed)
  pollAcc = pollAcc + elapsed
  if pollAcc < 0.1 then return end
  pollAcc = 0

  rehookAcc = rehookAcc + elapsed
  if rehookAcc > 3 then
    rehookAcc = 0
    RegisterFilters()
  end

  for _ = 1, 16 do
    local ok, j = pcall(WoWTranslate_Poll)
    if not ok or not j or j == "" then break end
    local id = JsonGetString(j, "id")
    local tr = JsonGetString(j, "translation") or ""
    local er = JsonGetString(j, "error") or ""
    local p = id and pending[id] or nil
    if p and not p.done then
      p.done = true
      pending[id] = nil
      if er ~= "" then
        dbg.terr = dbg.terr + 1
        WoWTranslate_Diag("WTC_TRANSERR id=" .. id .. " err=" .. string.sub(er, 1, 80))
        if p.r then
          p.frame:AddMessage(p.text, p.r, p.g, p.b)
        else
          p.frame:AddMessage(p.text)
        end
      else
        dbg.ok = dbg.ok + 1
        if dbg.ok <= 3 then
          WoWTranslate_Diag("WTC_TRANSOK id=" .. id)
        end
        local line = ""
        if p.sender ~= "" then
          line = "|Hplayer:" .. p.sender .. "|h[" .. p.sender .. "]|h： "
        end
        line = line .. tr
        if displayMode == "both" then
          if p.r then
            p.frame:AddMessage(dispPrefix .. tr, p.r, p.g, p.b)
          else
            p.frame:AddMessage(dispPrefix .. tr)
          end
        else
          if p.r then
            p.frame:AddMessage(line, p.r, p.g, p.b)
          else
            p.frame:AddMessage(line)
          end
        end
      end
    end
  end

  local now = GetTime()
  for mid, p in pairs(pending) do
    if now - p.t > 30 then
      pending[mid] = nil
      dbg.to = dbg.to + 1
      if dbg.to <= 3 then
        WoWTranslate_Diag("WTC_TIMEOUT id=" .. mid)
      end
      if displayMode ~= "both" then
        if p.r then
          p.frame:AddMessage(p.text, p.r, p.g, p.b)
        else
          p.frame:AddMessage(p.text)
        end
      end
    end
  end
  -- 外发翻译 10s 超时：replace 模式发原文兜底（both 已发过原文）
  for oid, o in pairs(outPending) do
    if now - o.t > outTimeout then
      outPending[oid] = nil
      if outgoingMode == "replace" then
        pcall(function() origSend(o.msg, o.chatType, o.language, o.channel) end)
      end
      dbg.oto = dbg.oto + 1
      if dbg.oto <= 3 then
        WoWTranslate_Diag("WTC_OUTTIMEOUT id=" .. oid)
      end
    end
  end
end)

-- ---- 外发翻译：钩 SendChatMessage，中文 → 英文（\1ZH2EN\1 标签）----
local OUT_TYPES = {
  SAY = true, YELL = true, WHISPER = true, PARTY = true, GUILD = true,
  OFFICER = true, RAID = true, RAID_WARNING = true, BATTLEGROUND = true,
  CHANNEL = true,
}
SendChatMessage = function(msg, chatType, language, channel)
  if outgoingMode == "off" or not msg or msg == "" or not HasCJK(msg)
    or not chatType or not OUT_TYPES[chatType] then
    return origSend(msg, chatType, language, channel)
  end
  outCounter = outCounter + 1
  local oid = "out_" .. outCounter
  outPending[oid] = { msg = msg, chatType = chatType,
                      language = language, channel = channel, t = GetTime() }
  WoWTranslate_Diag("WTC_OUTCAPTURE id=" .. oid .. " type=" .. tostring(chatType) ..
    " to=" .. tostring(channel) .. " msg=" .. string.sub(msg, 1, 50))
  local ok = pcall(function()
    WoWTranslate_Translate("\1ZH2EN\1" .. msg, "zh", "en", oid)
  end)
  if not ok then
    outPending[oid] = nil
    return origSend(msg, chatType, language, channel)
  end
  if outgoingMode == "both" then
    origSend(msg, chatType, language, channel)
  end
end

-- 横幅：注入常发生在加载屏阶段（消息会被后续聊天框初始化清掉），
-- 延迟到 PLAYER_ENTERING_WORLD + 1s 后再显示，保证玩家看得到
local bannerShown = false
local bannerFrame = CreateFrame("Frame")
bannerFrame:RegisterEvent("PLAYER_ENTERING_WORLD")
bannerFrame:SetScript("OnEvent", function()
  if bannerShown then return end
  bannerShown = true
  local acc = 0
  bannerFrame:SetScript("OnUpdate", function(self, el)
    acc = acc + el
    if acc < 1 then return end
    self:SetScript("OnUpdate", nil)
    if DEFAULT_CHAT_FRAME then
      DEFAULT_CHAT_FRAME:AddMessage("|cFF00CCFF[WTC]|r WoWTranslateDirect 2.1.1 by ijnokmsc (driver v28, mode=" ..
        displayMode .. ", outgoing=" .. outgoingMode .. ")")
    end
  end)
end)

end  -- WTC_MAIN

local ok, err = pcall(WTC_MAIN)
WoWTranslate_Diag(ok and "WTC_DRIVER_OK" or ("WTC_DRIVER_FAIL: " .. tostring(err)))

end
)WTCDRIVER";
}
} // namespace wt
