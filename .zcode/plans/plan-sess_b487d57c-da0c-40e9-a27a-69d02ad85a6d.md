# Track B v16：DLL 全自治（脱离插件）

## 目标
WoWTranslateDirect.dll 不再依赖 Interface/AddOns 插件：DLL 自己捕获聊天原文 → 发控制台 127.0.0.1:8080 翻译（现有管线/过滤/术语/缓存全复用，协议零改动）→ 译文写回聊天框。显示模式两种（译文替换原文 / 原文+译文）由用户在软件端选择。

## 一、DLL 侧（patches/335/src/）

### 1. 新增主动执行 Lua 能力（direct_main.cpp）
- 地址表新增 `FrameScript_Execute = 0x819210`（IDA dump 已有完整反编译：全局 L=dword_D3F78C、registry 错误处理器、loadbuffer+pcall），启动时序言字节校验。
- **执行时机 = 主线程保证**：注册成功后，在 OnGetTop detour 内检测到 depth==0 浅栈边界时一次性执行驱动 Lua（one-shot 标志 + `g_inExecute` 重入保护，防止 FrameScript_Execute 内部再触发 gettop detour 递归注册）。
- 注入前用 C++ 把配置拼成 Lua 全局表前缀 `WTC_CONFIG={displayMode=..., prefix=..., ...}`，与驱动代码拼接后一次 ExecuteLua。

### 2. 内嵌驱动 Lua（新文件 src/driver_lua.h，raw string）
从 vendor/wow-translate 插件 Lua 移植精简核心逻辑：
- **中和旧插件**：注入时定义 `UnitXP` 空操作桩（按 WoWTranslate_API.lua 的调用约定返回安全值，使其 translate_async/poll 全部无害化），旧插件即加载也不会双重翻译。
- **捕获**：hook ChatFrame1..N 的 AddMessage（保留原函数引用）+ hook ChatFrame_OnEvent 记录当前事件频道（移植 EVENT_TO_CHANNEL / SYSTEM_EVENTS 映射表）。
- **翻译**：原文前加 `\1<频道>\1` 标签调 `WoWTranslate_Translate(text,"en","zh",id)`（标签供控制台 C0-C8 频道开关使用）；隐藏帧 OnUpdate 调 `WoWTranslate_Poll()` 取结果，30s 超时兜底。
- **显示（两种模式按 WTC_CONFIG.displayMode）**：
  - `replace`：压住原文，译文到达后经原始 AddMessage 以原格式写回；失败/超时回显原文。
  - `both`：原文立即照常显示，译文到达后另起一行加前缀 `[译]`（前缀可配置）追加。
- 移植超链接占位符还原（ReconstructMessage）。

### 3. 配置项（translator.cpp / direct_main.cpp）
- WoWTranslateDirect.json 新增 `displayMode`（"replace"/"both"，默认 "replace"）、`displayPrefix`（默认 "[译]"）。启动时读取，重登生效。

## 二、软件端（控制台，无协议改动）
- `AppConfig`：新增 `DirectDisplayMode`、`DirectDisplayPrefix`，存 settings.json。
- `DllSwitcher`：Track B 部署时把 `WoWTranslateDirect.json`（endpoint=127.0.0.1:{ListenPort}/v1/chat/completions + displayMode + displayPrefix）写入游戏目录。
- `MainWindow`：DLL 轨道区新增"显示模式"下拉框（替换原文/原文+译文），保存 settings 并更新已部署的 json。

## 三、构建与验证（按项目铁律）
1. `patches/335/build.bat`（MSVC x86 /MT）→ 拷贝 assets/direct-dll；selftest_host 过自测。
2. `dotnet build` + `dotnet test`（58 单测）+ 冒烟工具（UI 改动三件套：单测+冒烟断言+截图）。
3. 提交 commit；部署到 `D:\Games\TriumvirateWoW`（游戏目录铁律，部署前与你确认 Wow.exe 已关）。
4. 你进游戏实测：登录页 /run 注册探针 → 聊天实测两种模式 → 反馈结果（未经实测不发布）。

## 风险与对策
- 主线程外执行 Lua 会崩 → 只在 gettop detour 主线程上下文执行，one-shot + 重入锁。
- 反编译误导的老坑 → 0x819210 及新地址先做序言字节校验，失败则放弃注入并记日志（保持 v15 被动模型可用）。
- 旧插件残余 hook 时序 → UnitXP 桩在注入时即定义，早于插件加载。