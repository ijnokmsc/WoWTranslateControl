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


-- v31 捕获：隐藏事件帧 RegisterEvent(CHAT_MSG_*)——事件由引擎直接派发，
-- 任何聊天 UI（EUI/巨龙UI/原生）都无法拦截或改道；事件参数即纯正文与发送者。
-- （v28 过滤器、v29 OnEvent/AddMessage 钩子在该客户端+聊天插件组合下均不触发。）
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
local outgoingMode = tostring(WTC_CFG.outgoing or "off")
local outTimeout = tonumber(WTC_CFG.outTimeout or 25)

local pending, counter = {}, 0
local outPending, outCounter = {}, 0
local origSend = SendChatMessage
local dbg = { raw = 0, own = 0, ok = 0, terr = 0, to = 0, oto = 0, chatEvt = 0 }

-- UTF-8 文本的 CJK 检测（外发译文质检：翻译后的"英文"必须纯 ASCII 安全）
local function HasCJKUtf8(t)
  for i = 1, string.len(t) - 2 do
    local b = string.byte(t, i)
    if b >= 0xE4 and b <= 0xE9
      and string.byte(t, i + 1) >= 0x80 and string.byte(t, i + 2) >= 0x80 then
      return true
    end
  end
  return false
end

local function HasCJK(t)
  -- GBK 与 UTF-8 的中文首字节均 >= 0x81，命中即视为含中文
  for i = 1, string.len(t) do
    if string.byte(t, i) >= 0x81 then return true end
  end
  return false
end

-- ---- 消息事件 → 频道标签 ----
local EVENTS = {
  CHAT_MSG_SAY = "SAY", CHAT_MSG_YELL = "YELL", CHAT_MSG_WHISPER = "WHISPER",
  CHAT_MSG_PARTY = "PARTY", CHAT_MSG_PARTY_LEADER = "PARTY",
  CHAT_MSG_GUILD = "GUILD", CHAT_MSG_OFFICER = "GUILD",
  CHAT_MSG_RAID = "RAID", CHAT_MSG_RAID_LEADER = "RAID", CHAT_MSG_RAID_WARNING = "RAID",
  CHAT_MSG_BATTLEGROUND = "BATTLEGROUND", CHAT_MSG_BATTLEGROUND_LEADER = "BATTLEGROUND",
  CHAT_MSG_CHANNEL = "CHANNEL",
}
local CHAT_TYPE = {
  SAY = "SAY", YELL = "YELL", WHISPER = "WHISPER", PARTY = "PARTY",
  GUILD = "GUILD", RAID = "RAID", BATTLEGROUND = "BATTLEGROUND", CHANNEL = "CHANNEL",
}

-- DLL Poll 返回 {"id":"..","translation":"..","error":".."} 的最小 JSON 字符串读取
local function JsonGetString(json, key)
  if not json or not key then return nil end
  local ks, ke = string.find(json, '"' .. key .. '"', 1, true)
  if not ks then return nil end
  local colon = string.find(json, ":", ke + 1, true)
  if not colon then return nil end
  local i = colon + 1
  while i <= string.len(json) do
    local ch = string.sub(json, i, i)
    if ch ~= " " and ch ~= "\t" and ch ~= "\n" and ch ~= "\r" then break end
    i = i + 1
  end
  if string.sub(json, i, i) ~= '"' then return nil end
  local out, j = "", i + 1
  while j <= string.len(json) do
    local ch = string.sub(json, j, j)
    if ch == "\\" and j < string.len(json) then
      local nc = string.sub(json, j + 1, j + 1)
      if nc == '"' then out = out .. '"'
      elseif nc == "\\" then out = out .. "\\"
      elseif nc == "/" then out = out .. "/"
      elseif nc == "n" then out = out .. "\n"
      elseif nc == "r" then out = out .. "\r"
      elseif nc == "t" then out = out .. "\t"
      elseif nc == "u" then
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
    elseif ch == '"' then
      return out
    else
      out = out .. ch
      j = j + 1
    end
  end
  return out
end

-- ---- 译文显示：统一走 DEFAULT_CHAT_FRAME（带玩家链接头，显示期拼装）----
local function DisplayLine(text, r, g, b)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage(text, r, g, b)
  end
end

-- ---- 捕获去重查询：只查不标（标记由 TryCapture 负责）----
-- v33 修复：SeenMark 先标后查会自吞事件帧自己的捕获（说/团队零捕获根因）
local seen = {}
local function SeenCheck(ch, sender, msg)
  local key = ch .. "|" .. tostring(sender) .. "|" .. msg
  return seen[key] and GetTime() - seen[key] < 60
end

-- 捕获主体：入队翻译。返回 "queued"（首次）或 "dup"（重复）。
local function TryCapture(ch, msg, sender, chanLabel)
  local key = ch .. "|" .. tostring(sender) .. "|" .. msg
  if seen[key] and GetTime() - seen[key] < 60 then
    return "dup"
  end
  seen[key] = GetTime()

  if dbg.raw < 30 then
    dbg.raw = dbg.raw + 1
    WoWTranslate_Diag("WTC_RAW #" .. dbg.raw .. " ch=" .. ch ..
      " sender=" .. tostring(sender) .. " text=" .. string.sub(msg, 1, 160))
  end

  -- 消息风暴限流：待翻译积压过多时放弃新消息（原文自然显示，防显示风暴）
  local backlog = 0
  for _, p in pairs(pending) do backlog = backlog + 1 end
  if backlog >= 24 then
    WoWTranslate_Diag("WTC_STORM skip (backlog=" .. backlog .. ")")
    return "queued"
  end
  counter = counter + 1
  local mid = "m" .. counter
  local ci = ChatTypeInfo[CHAT_TYPE[ch]]
  pending[mid] = { sender = tostring(sender or ""), text = msg,
                   chanLabel = chanLabel or "", suppressed = false,
                   r = ci and ci.r, g = ci and ci.g, b = ci and ci.b,
                   t = GetTime(), done = false }
  local ok = pcall(function()
    local rr = WoWTranslate_Translate("\1" .. ch .. "\1" .. msg, "en", "zh", mid)
    if rr ~= "ok" then
      WoWTranslate_Diag("WTC_QUEUERR " .. tostring(rr))
    end
  end)
  if not ok then
    pending[mid] = nil
  end
  return "queued"
end

-- 频道显示标签：自定义频道用事件给出的频道名，标准频道用固定中文
local CHAN_CN = { SAY = "[综合]", YELL = "[喊话]", WHISPER = "[密语]", PARTY = "[队伍]",
  GUILD = "[公会]", RAID = "[团队]", BATTLEGROUND = "[战场]" }
local function ChanLabel(ch, ...)
  if ch == "CHANNEL" then
    local nm = select(2, ...)   -- CHAT_MSG_CHANNEL: msg, sender, lang, channelName
    if nm and nm ~= "" then return "[" .. tostring(nm) .. "]" end
    return "[世界]"
  end
  return CHAN_CN[ch] or ""
end

-- 消息是否应送翻（频道已知 + 非空 + 非自己发言 + 非中文 + 非已译标识）
-- 中文判定（编码无关）：任一字节 >= 0x81 即含 CJK（GBK 双字节首位/UTF-8 首字节均为高位）
-- ——事件参数是纯正文（无头污染），该判定不再误伤"中文 ID 玩家的英文消息"
local TRANSLATE_MARK = "[译]"
local function ShouldTranslate(event, msg, sender)
  local ch = EVENTS[event]
  if not ch or not msg or msg == "" then return nil end
  local me = UnitName and UnitName("player")
  if me and me ~= "" and sender == me then return nil end
  if string.find(msg, TRANSLATE_MARK, 1, true) then return nil end   -- 已译标识防回环
  if HasCJK(msg) then return nil end   -- 中文消息不送翻（双方都装软件时的重复根源）
  return ch
end

-- ---- 压制过滤器（方案 1：replace 模式压住原文，译文由轮询显示）----
-- ChatFrame_AddMessageEventFilter(event, filter)：按事件全局注册。
-- 实测若该客户端过滤器链不回调（v28 疑似），压制退化为追加显示，无损。
local suppressCount = 0
local function WTCSuppressFilter(chatFrame, event, msg, sender, ...)
  local ch = ShouldTranslate(event, msg, sender)
  if not ch then return false end
  local r = TryCapture(ch, msg, sender, ChanLabel(ch, ...))
  if r == "queued" then
    -- 标记该条已被压制（译文回显时跳过判重的依据）
    local snd = tostring(sender or "")
    local newest, newestT
    for _, p in pairs(pending) do
      if p.sender == snd and not p.done and (newestT == nil or p.t > newestT) then
)WTCDRIVER"
R"WTCDRIVER(
        newest, newestT = p, p.t
      end
    end
    if newest then newest.suppressed = true end
    suppressCount = suppressCount + 1
    if suppressCount <= 3 then
      WoWTranslate_Diag("WTC_FILTERCAP #" .. suppressCount .. " ch=" .. ch ..
        " sender=" .. tostring(sender) .. " mode=" .. displayMode)
    end
    -- 仅 replace 模式压制原文；both 模式放行（原文照显，译文由轮询追加）
    return displayMode == "replace"
  end
  return false
end

for ev in pairs(EVENTS) do
  ChatFrame_AddMessageEventFilter(ev, WTCSuppressFilter)
end
WoWTranslate_Diag("WTC_SUPPRESS_FILTERS registered=" .. (function()
  local n = 0
  for _ in pairs(EVENTS) do n = n + 1 end
  return n
end)())

-- ---- 引擎级事件帧（兜底捕获：过滤器链不回调的客户端由此入队，无压制）----
local ef = CreateFrame("Frame")
local evlist = {}
for ev in pairs(EVENTS) do table.insert(evlist, ev) end
for _, ev in ipairs(evlist) do ef:RegisterEvent(ev) end
-- v33 诊断：事件帧收到的所有事件（前 12 个），区分"引擎未派发"与"处理分支问题"
local dbgEv, evCount = {}, 0
ef:SetScript("OnEvent", function(self, event, msg, sender, ...)
  if evCount < 12 then
    evCount = evCount + 1
    WoWTranslate_Diag("WTC_EVT #" .. evCount .. " ev=" .. tostring(event) ..
      " msg=" .. string.sub(tostring(msg), 1, 40) ..
      " sender=" .. tostring(sender))
  end
  local ch = ShouldTranslate(event, msg, sender)
  if not ch then return end
  -- 自己的消息回显不处理
  local me = UnitName and UnitName("player")
  if me and me ~= "" and sender == me then return end

  -- 过滤器已捕获过的（60s 窗口）→ 跳过
  if SeenCheck(ch, tostring(sender or ""), msg) then return end
  TryCapture(ch, msg, sender, ChanLabel(ch, ...))
end)

-- v33 探针：包装前 3 个聊天框的 OnEvent 脚本（EUI/巨龙UI 若替换了脚本，
-- 此探针能看到聊天框实际收到的事件）——仅诊断，不影响显示
for i = 1, 3 do
  local f = getglobal("ChatFrame" .. i)
  if f then
    local prev = f:GetScript("OnEvent")
    f:SetScript("OnEvent", function(fs, event, ...)
      if dbg.chatEvt < 8 then
        dbg.chatEvt = dbg.chatEvt + 1
        WoWTranslate_Diag("WTC_CF" .. i .. " #" .. dbg.chatEvt .. " ev=" .. tostring(event) ..
          " msg=" .. string.sub(tostring(msg), 1, 40))
      end
      if prev then return prev(fs, event, ...) end
    end)
  end
end

-- ---- 轮询帧：Poll 译文 + 显示 + 超时兜底 ----
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
    local otxt = JsonGetString(j, "orig") or ""
    -- 外发结果（out_N）：发英文译文（带 [TR] 标识，英文玩家一眼识别翻译文字）；
    -- 译文缺失/仍含中文（模型方向失控）→ 回退发原文
    if id and string.sub(id, 1, 4) == "out_" then
      local o = outPending[id]
      if o then
        outPending[id] = nil
        local trOk = (er == "" and tr ~= "" and not HasCJKUtf8(tr))
        if trOk then
          pcall(function() origSend("[TR] " .. tr, o.chatType, o.language, o.channel) end)
        else
          pcall(function() origSend(o.msg, o.chatType, o.language, o.channel) end)
        end
        dbg.ok = dbg.ok + 1
        if dbg.ok <= 3 then
          WoWTranslate_Diag("WTC_OUTOK id=" .. id .. " ok=" .. tostring(trOk) ..
            " send=" .. string.sub(trOk and ("[TR] " .. tr) or o.msg, 1, 60))
        end
      end
    else
    local p = id and pending[id] or nil
    if p and not p.done then
      p.done = true
      pending[id] = nil
      if er ~= "" then
        dbg.terr = dbg.terr + 1
        if dbg.terr <= 3 then
          WoWTranslate_Diag("WTC_TRANSERR id=" .. id .. " err=" .. string.sub(er, 1, 80))
        end
        -- 失败回显原文（不加诊断前缀，保持聊天观感）
        DisplayLine(p.text, 1, 0.4, 0.4)
      else
        dbg.ok = dbg.ok + 1
        if dbg.ok <= 3 then
          WoWTranslate_Diag("WTC_TRANSOK id=" .. id)
        end
        -- 中文回显去重：译文与归一化原文一致且原文未被压制（UI 已显示）
        -- → 不重复显示（压制过的仍要显示，否则消息丢失）
        if tr ~= "" and (tr == otxt or tr == p.text) and not p.suppressed then
          -- skip
        else
          local line = TRANSLATE_MARK
          if p.chanLabel ~= "" then line = p.chanLabel .. " " .. line end
          if p.sender ~= "" then
            line = line .. " |Hplayer:" .. p.sender .. "|h[" .. p.sender .. "]|h： "
          end
          line = line .. tr
          if displayMode == "both" then
            DisplayLine(dispPrefix .. line, p.r, p.g, p.b)
          else
            DisplayLine(line, p.r, p.g, p.b)
          end
        end
      end
      end
    end
  end

  local now = GetTime()
  for mid, p in pairs(pending) do
    -- 60s：世界频道消息爆发时单线程队列排队 + 串行翻译，30s 会误超时
    if now - p.t > 60 then
      pending[mid] = nil
      dbg.to = dbg.to + 1
      if dbg.to <= 3 then
        WoWTranslate_Diag("WTC_TIMEOUT id=" .. mid)
      end
      DisplayLine(p.text, 1, 0.4, 0.4)
    end
  end
  -- 外发超时：replace 模式发原文兜底（both 已发过原文）
  for oid, o in pairs(outPending) do
    if now - o.t > outTimeout then
      outPending[oid] = nil
      if outgoingMode == "replace" then
        pcall(function() origSend("[WTC]: translation failed", o.chatType, o.language, o.channel) end)
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

-- 横幅：延迟到 PLAYER_ENTERING_WORLD + 1s 显示（加载屏阶段写入会被清掉）
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
      DEFAULT_CHAT_FRAME:AddMessage("|cFF00CCFF[WTC]|r WoWTranslateDirect 2.1.2 by ijnokmsc (driver v31, mode=" ..
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
