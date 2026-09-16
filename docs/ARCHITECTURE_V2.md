# WoWTranslateControl v2 架构设计

> 状态：Proposed ｜ 日期：2026-09-11 ｜ 作者：赵工 + Software Architect
> 取代：v1 单体实现（`MainWindow.xaml.cs` + Core 三类）

---

> **⚠ 3.0 状态（2026-09-14）**：本文为 v2 双轨设计的历史定案。**3.0 已单轨化**——
> Track A（GS 插件）支持整体移除（PluginConfigurator 删除、DllSwitcher 单轨化、
> R1-R6 内容规则下线、按规则命中分布统计移除），只保留 Track B Direct DLL 自治引擎。
> 禁用老插件的机制 = DLL 覆盖：Direct 的 dinput8.dll 占据同一加载入口后，
> WoWTranslate335.dll 无人加载、自然失活；不删用户文件、不改 .toc。
> 下方双轨章节保留作历史参考。

---

## 1. 现状盘点（设计输入）

### 1.1 插件侧资产（GrimfallWoW 3.3.5a 客户端，已存在）

| 资产 | 说明 | 对 v2 的约束 |
|---|---|---|
| `WoWTranslate335.dll`（2.1.1-GS） | MinHook inline hook `lua_gettop` 获取 `lua_State`，运行时字节码推导偏移，注册 7 个原生 Lua API；DLL 直接发起 HTTPS | DLL 与 Lua 接口版本必须成对；**不是上游 1.12 DLL** |
| `dinput8.dll` + `dlls.txt` | 插件加载链（代理 DLL 加载机制） | 部署器必须同时管理这三个文件 |
| Lua 插件 v2.3（8 个 .lua） | 聊天事件、信道过滤、术语表预处理（`WoWTranslate_Glossary.lua`）、本地缓存、配置面板 | 上游 Lua 是 2.0，GS 版是 2.3，**接口已分叉** |
| `SavedVariables/WoWTranslate.lua` | `WoWTranslateDB`（provider/openaiEndpoint/openaiModel/各 API key 等约 40 键）+ `WoWTranslateCache` + `WoWTranslateDebugLog` | 一键配置的写入目标；**游戏运行时写入会被退出时覆盖** |
| `WoWTranslate.ini`（DLL 旁，可选） | DLL 侧凭据通道（provider type / key / endpoint / template / response_path） | 一键配置的第二写入目标，且优先级高于面板输入 |

### 1.2 控制台侧资产（本仓库 v1，已验证有效）

| 资产 | 结论 |
|---|---|
| `ProxyServer`（TcpListener 手写 HTTP，8080→8081） | **保留**。HttpListener 依赖 http.sys 需管理员/urlacl，TcpListener 普通权限可用（已实测）。但存在每请求 `new HttpClient` 反模式（TIME_WAIT 耗尽风险），v2 必须改单例 `SocketsHttpHandler` |
| `MessageFilter` R0–R6（680 条真实流量统计驱动：88.6% 系统噪音） | **保留**，抽成管线中的可插拔 Stage |
| 响应缓存（LRU 2000，重复率 3.9x） | **保留**，升级为持久化 + 术语表版本联动失效 |
| `LlamaServerManager`（CPU：Hy-MT2-1.8B Q4_K_M，-ngl 0，-t 20） | **保留**进程管理骨架，外围补资产管理/硬件推荐/GPU 参数 |
| 系统提示词注入 + `max_tokens` 钳制（256） | **保留**——插件 DLL 不发 system 与 max_tokens，这两个改写是服务质量的根，任何 Provider 下都要做 |

### 1.3 上游参考（sanjaygbhat/wow-translate）

- 面向 **WoW 1.12**（vanilla）：DLL 通过 `dlls.txt` 加载，hook Lua，向 Provider 发 HTTPS；Lua 2.0。
- 构建链：CMake + VS2022 Win32，`dll/build.bat`，有 GitHub Actions。
- Provider 协议：OpenAI 兼容（temperature 0，读 `choices[0].message.content`）、Google Cloud Translation Basic v2、自定义 HTTPS JSON 模板。
- **关键事实：它的 DLL 产物不能在 3.3.5 上工作**（Lua 引擎偏移、`lua_State` 获取方式、Addon 通信协议全部不同）。详见 §8 风险 R1。

---

## 2. 设计目标与约束

**目标（对应用户需求 1–9）**

1. 兼容现有 WoWTranslate 3.3.5-GS 插件，不强制用户迁移即可用 v2。
2. 本地消息过滤升级为可扩展管线（规则可开关、可新增、可统计）。
3. 本地术语表：控制台侧权威存储，翻译前生效。
4. 术语表增删改管理（UI + 持久化 + 缓存联动失效）。
5. 多 Provider：llama.cpp（本地）/ OpenAI 兼容 / Google / 自定义 HTTPS JSON。
6. llama.cpp 运行实例与模型文件管理（启停、多版本、多模型）。
7. **双轨并行**：主轨沿用现役 WoWTranslate GS 插件链路（成熟、已验证）；副轨以上游 wow-translate 的 DLL 为**模板基座**构建自有 DLL，实现**直接消息获取**，方案成熟后切换为主轨。自有 DLL 按多游戏版本适配框架设计（3.3.5 先行，后续扩展 1.12 等版本）。两轨长期共存，共用控制台的同一处理管线。
8. 一键配置插件（SavedVariables + INI 双写，含游戏进程检测）。
9. 硬件探测 → 自动推荐 llama.cpp 构建（CUDA/Vulkan/CPU）与模型（量化档位）→ 下载（含镜像回退）。

**硬约束**

- 控制台必须普通权限双击可运行（v1 已验证的 TcpListener 路线不可回退）。
- 插件侧只允许配置**一个**端点（127.0.0.1:8080），Provider 切换对插件透明。
- 用户在中国网络环境：HuggingFace 直连不可靠，下载必须有 hf-mirror.com 回退。
- 单用户单机工具，不做账号/鉴权体系（对齐 wa2 v3 决策）；代理只绑 loopback。

---

## 3. 总体架构

```
WoW 客户端 3.3.5a                    WoWTranslateControl v2                 Provider 后端
┌─────────────────────┐   HTTP   ┌──────────────────────────────┐          ┌──────────────┐
│ WoWTranslate335.dll │────────→│ 代理服务 :8080                  │  local  │ llama-server │
│  (hook Lua, HTTPS)  │  :8080  │  ┌────────────────────────┐  │  :8081  │  (CPU/GPU)   │
│ Lua 插件 v2.3       │          │  │ Pipeline:              │  │         └──────────────┘
│  聊天事件/面板       │          │  │  Filter → Glossary →   │──┼──────→ ┌──────────────┐
│ WoWTranslateCache   │          │  │  Cache → Provider路由   │  │ HTTPS  │ OpenAI 兼容   │
│ SavedVariables/INI  │          │  └────────────────────────┘  │───────→ │ Google / 自定义│
└─────────────────────┘          │ llama 进程管理  资产解析器      │          └──────────────┘
        ↑ 部署/配置               │ 插件部署桥（DLL/INI/DB 写入）   │←─┐
└────────┼───────────────────    └───────────────┬──────────────┘  │
         │                                      │ 管理             │ 下载
   glossary.json / settings.json / llama 资产库 / vendor wow-translate ←─┘
```

**架构枢纽决策（一句话）**：代理在插件面前永远伪装成一个"OpenAI 兼容翻译端点"，插件的一切复杂性（本地模型/云端 API/过滤/术语/缓存）都被吸收在 8080 这一层后面。这是整份设计的地基——插件侧零改动即可获得全部新能力。

**双轨并行（2026-09-11 用户决策）**：

| 轨道 | 消息获取方式 | 译文回显 | 状态 |
|---|---|---|---|
| **Track A（主轨）** | WoWTranslate GS 插件链路：Lua 事件 → DLL HTTPS → 代理 :8080 | 插件自带聊天回显 | 现役，已验证 |
| **Track B（副轨）** | 自有 DLL 直接 hook 聊天消息源，POST 到控制台摄取 API `/internal/ingest` | DLL 走注册的 Lua API 写聊天帧（复用 GS 版已验证的注入路径） | 探索/构建中 |

两轨汇入**同一个 Pipeline 实例**（Filter → Glossary → Cache → Provider 路由），控制台是唯一的翻译中枢；Track B 成熟前不替换 Track A 的任何文件，切换 = 停用插件加载（清空 `dlls.txt` / 移除代理 DLL），控制台侧无感。

---

## 4. 模块设计

### 4.1 解决方案结构

```
WoWTranslateControl/
├─ src/
│  ├─ WoWTranslateControl.Core/        # 与 UI 解耦的类库（net10.0）
│  │  ├─ Proxy/        ProxyServer, HttpPipeline, TrafficLog
│  │  ├─ Pipeline/     IMessageStage: FilterStage, GlossaryStage, CacheStage, ProviderRouteStage
│  │  ├─ Providers/    ITranslationProvider, LlamaCppProvider, OpenAiCompatProvider,
│  │  │                GoogleCloudProvider, CustomHttpProvider, ProviderHealth
│  │  ├─ Glossary/     GlossaryStore(json, 版本号自增), GlossaryApplier(占位符保护)
│  │  ├─ Llama/        LlamaServerManager(v1 演进), LlamaAssetService
│  │  ├─ Assets/       HardwareProbe, Recommender, Downloader(HF→hf-mirror 回退, SHA256)
│  │  ├─ PluginBridge/ ClientLocator(多客户端), DllDeployer, Configurator(SavedVariables+INI)
│  │  └─ VendorBuild/  UpstreamSync, DllBuildInvoker(CMake+MSBuild, 可选功能)
│  ├─ WoWTranslateControl.App/         # WPF 壳（MVVM 轻量：CommunityToolkit.Mvvm）
│  │  Views: Dashboard / Filter / Glossary / Providers / LlamaAssets / PluginDeploy
│  └─ WoWTranslateControl.Tests/
├─ vendor/wow-translate/               # git submodule: sanjaygbhat/wow-translate（模板基座）
│  └─ patches/                         # per-version 移植层：335/ 112/ _template/（见 §4.6、§8 R1）
├─ assets/
│  ├─ bundled-addon/                   # 当前已验证的 GS 修改版 Lua 2.3（v2 的兼容基线）
│  └─ catalogs/  llama-builds.json  models.json   # 推荐/下载清单（可远程更新）
└─ docs/  ARCHITECTURE_V2.md  ADR/
```

### 4.2 处理管线（需求 2、3、4 的落点）

```csharp
public interface IMessageStage {
    string Id { get; }
    // 返回 null = 本阶段放行；返回 PipelineResult = 短路直接回包
    Task<PipelineResult?> ProcessAsync(TranslationContext ctx, CancellationToken ct);
}
```

链路：`FilterStage(R0–R6, 可扩展) → GlossaryStage → CacheStage → ProviderRouteStage`。

- **GlossaryStage**：在发模型前做**受保护替换**——术语 → `⟦G12⟧` 占位符（避免模型改写术语），译文回来后把占位符换回目标语术语。同一术语可配单向（仅 EN→ZH）或双向。woW 超链接占位符（`http://ph.wt/N`、`|H...|h`）沿用 v1 的保护逻辑，术语替换不得触碰它们。
- **CacheStage**：缓存 key 升级为 `(原文, 目标语, glossaryVersion, providerId, systemPromptHash)`。**术语表任何增删改 → `glossaryVersion++` → 全部旧缓存自然失效**。缓存持久化到 `cache.json`（重启不丢，插件端 `WoWTranslateCache` 才是第一层）。
- **FilterStage**：R0–R6 原样保留，规则数据化（JSON 目录 + 开关 + 命中计数），支持用户自定义正则规则（编号续排 R7+）。

### 4.3 Provider 抽象（需求 5）

```csharp
public interface ITranslationProvider {
    string Id { get; }                       // "llama-local" / "openai" / "google" / "custom"
    bool IsReady { get; }                    // 健康状态，UI 显示
    Task<ProviderReply> TranslateAsync(TranslationRequest req, CancellationToken ct);
}
```

- 无论哪个 Provider，`ProviderRouteStage` 前都执行 v1 已验证的改写：注入 system 提示词、钳制 `max_tokens`、丢弃插件发来的空 system、强制 `stream=false`。
- **故障转移**（可选开关）：主 Provider 失败/超时 → 备用 Provider → 最终回显原文（v1 已验证：宁可原文也不要吞聊天）。
- 并发上限 + 全局排队超时（默认 60s，超时即回显原文）：防止聊天高峰把请求堆积成雪崩（见 §8 R6）。
- 云端 key 用 DPAPI（`ProtectedData`）加密落盘，settings.json 不再明文。

### 4.4 llama.cpp 管理（需求 6、9）

```
LlamaAssetService                    LlamaServerManager（v1 演进）
├─ HardwareProbe                     ├─ start/stop/健康探测（v1 已有）
│   ├─ GPU: DXGI 枚举 + NVML(可选)    ├─ 参数按设备档位生成：
│   │   显存 → 分档 <4G/4-8G/>8G      │     CPU:  -ngl 0 -t 物理核
│   ├─ CPU: 物理核/AVX2 支持          │     GPU:  -ngl 999 -fa on + 显存相关 -c
│   └─ RAM 总量                       ├─ 端口占用即认领（v1 已有）
├─ Recommender                       └─ 崩溃检测 + 一次性自动重启（上限 3 次/小时）
│   ├─ llama-builds.json: CUDA 版本匹配驱动 / Vulkan 通用 / CPU AVX2
│   └─ models.json: 显存<4G→1.8B-Q4；4-8G→7B-Q4；云端兜底建议
└─ Downloader
    ├─ 源优先级：hf-mirror.com → huggingface.co → GitHub Release
    ├─ 断点续传 + SHA256 校验 + 磁盘空间预检
    └─ 资产库布局：assets/llama/{build-tag}/llama-server.exe
                   assets/models/{model-tag}/xxx.gguf
```

推荐矩阵（初版，`catalogs/*.json` 可在线更新）：

| 硬件画像 | llama.cpp 构建 | 推荐模型 | 预期 |
|---|---|---|---|
| 无独显，AVX2 | CPU (AVX2) | Hunyuan-MT-1.8B Q4_K_M | 现状基线，~20 tok/s |
| 独显 ≥8G + 新驱动 | CUDA build | Hunyuan-MT-7B Q4_K_M | 翻译质量档，近实时 |
| 独显 4–8G 或驱动不明 | Vulkan build | Hunyuan-MT-1.8B / Qwen2.5-3B Q4 | 通用兜底 |
| 都不满足 | — | 建议切云端 Provider | — |

### 4.5 插件部署桥（需求 1、8，服务 Track A）

**ClientLocator**：扫描常见客户端目录（注册表 + 历史已知路径 `D:\Games\GrimfallWoW\Wotlk`、TriumvirateWoW），以 `Wow.exe + Interface/AddOns` 特征确认；允许多客户端登记，部署时选择目标。

**Configurator（一键配置，需求 8）**——写两个通道：

1. `WoWTranslate.ini`（DLL 旁）：`[provider] type=openai` + `endpoint=http://127.0.0.1:8080/v1/chat/completions` + `model=WoWTranslateControl`。
2. `SavedVariables/WoWTranslate.lua`：`WoWTranslateDB.openaiEndpoint / openaiModel / provider / incomingToLang / incomingChannels` 等键值重写（保留 `WoWTranslateCache` 等未知键不动，只改目标键）。
3. **前置安全检查：检测 `Wow.exe` 进程，运行中则拒绝写入并提示**（SavedVariables 会被客户端退出时整体覆盖，见 §8 R3）。UI 上显示"游戏未运行，可安全写入"。

**DllDeployer（Track A 运行时侧）**：部署 `WoWTranslate335.dll` + `dinput8.dll` + `dlls.txt` + `bundled-addon/`（Lua 全套），先备份再覆盖（沿用 wa2 deploy_plugin.py 的备份语义）；完成清单式自检（文件存在 + dlls.txt 内容 + Lua 语法检查可选）。Track B 落地后增加"卸载/回退"动作：清空 `dlls.txt` 内容（保留文件）即可停用 Track A，不删任何文件。

### 4.6 自有 DLL 与消息摄取通道（需求 7，Track B）

**VendorBuild（构建侧）**——上游 DLL 作模板基座，多版本适配框架：

- `vendor/wow-translate` 以 git submodule 锁定上游 commit。上游 DLL 的价值是**模板**：加载链（dlls.txt + 代理 DLL）、Lua 引擎对接、HTTPS 请求层、与 Addon 的注册协议都是现成的骨架。
- 版本适配按 **per-version profile** 组织，而不是硬编码单一版本：
  ```
  patches/
  ├─ 335/                    # WotLK 3.3.5a —— 先行版本
  │   ├─ offsets/            # Lua 引擎函数偏移（运行时字节码推导，参照 GS 版日志行为）
  │   ├─ hook/               # lua_State 捕获、注册函数集、聊天消息源 hook 点
  │   └─ profile.json        # 版本元数据：Interface 号、模块基址特征、已验证状态
  ├─ 112/                    # vanilla 1.12（上游原生，几乎零补丁，作为最易达成的第一个验证档）
  └─ _template/              # 新版本适配模板：标注所有必须替换的点
  ```
- 构建产物按版本命名进 `assets/direct-dll/{version}/`（如 `WoWTranslateDirect-335.dll`），控制台根据 ClientLocator 识别到的客户端版本自动匹配对应 profile 与产物，绝不跨版本混用。
- **降级路线**：本机无 VS2022 C++ 工具链时，VendorBuild 标记不可用（UI 灰显）；上游同步仅做源码参考。

**DirectChannel（消息摄取 API，Track B 运行时侧）**：

- 控制台新增内部端点 `POST /internal/ingest`：`{sender, channel, text, lang?}` → 经**同一个 Pipeline**（Filter/Glossary/Cache/Provider）→ 返回 `{translated, kind}`（kind 命中统计与 Track A 一致）。
- 自有 DLL 直接 hook 聊天消息源拿到原文，POST 到 ingest，再把译文通过注册的 Lua API 写入聊天帧（复用 GS 版已验证的注入路径，不新增 hook 面——见 R4）。
- 安全：`/internal/*` 只绑 loopback + 简单握手 token（控制台启动时生成、DLL 通过配置文件读取），防止本机其他进程注入伪造聊天。
- 与 Track A 的关系：两轨可同时在线（各自独立 hook），控制台按来源标记流量；也可以只开一轨。UI 提供"轨道偏好"开关，Track B 验收达标后一键切换。

### 4.7 UI（WPF 壳）

六个 Tab：仪表盘（流量统计/管线各阶段命中数）、过滤（规则开关+自定义规则+命中明细）、术语表（增删改查+导入导出 CSV）、Provider（选择/健康/密钥）、llama 资产（硬件检测→推荐→下载→启停）、插件部署（客户端检测→一键配置→DLL 部署→状态自检）。

---

## 5. ADR 记录

**ADR-001：插件只认一个本地端点（枢纽）**
- 决策：所有翻译能力收敛到 127.0.0.1:8080，插件永远指向它。
- 后果：✅ 插件零改动获得全部新能力；切换/迁移不碰游戏侧。❌ 控制台成为单点（必须常驻）；控制台未启动时插件直连失败——缓解：提供"直通模式"引导插件临时指向云端，或在文档中明确控制台需开机自启。

**ADR-002：保留 TcpListener 手写 HTTP**
- 已验证普通权限可用；代价是 HTTP 语义自维护，v2 用单例 `SocketsHttpHandler` 修复 v1 的每请求 HttpClient 反模式。

**ADR-003：术语表控制台侧权威 + 版本号失效**
- 决策：不在 Lua 内维护第二份术语表；`glossaryVersion` 参与 cache key。
- 后果：✅ 单一事实源、增删即时生效。❌ 插件端 `WoWTranslateCache` 中已存在的旧译文无法由控制台直接清除——控制台在术语表变更后提示用户 `/wt clearcache`（或后续在 Lua 补丁中加版本号比对自动失效，列为增强项 E2）。

**ADR-004：vendor 上游 + 补丁层，而非直接采用上游 DLL**
- 上游是 1.12 产物，3.3.5 需移植；GS 修改版（现役）作为兼容基线与验收参照。

**ADR-005：一键配置双写（INI + SavedVariables）+ 游戏进程门禁**
- INI 管 DLL 凭据、SV 管插件行为；两者语义不同必须都写；游戏运行时一律拒绝写 SV。

**ADR-006：下载源镜像优先**
- hf-mirror.com 优先，GitHub Release 次之；所有资产 SHA256 校验后落库。

**ADR-007：双轨并行——插件链路为主，自有 DLL 为副轨（2026-09-11 用户决策）**
- 决策：Track A（WoWTranslate GS 插件，经 8080 代理）为主轨长期保留；Track B（自有 DLL，以上游为模板基座，直接消息获取 + `/internal/ingest`）为副轨并行构建；切换以"轨道偏好"开关完成，不删 Track A 文件。Track B 按 per-version profile 做多版本适配（3.3.5 先行，112 次之，`_template` 沉淀新版本适配法）。
- 后果：✅ R1 的移植风险被隔离在副轨，失败不损失任何现有能力；✅ 两轨共用同一 Pipeline，控制台投入零浪费；✅ 多版本框架让上游 1.12 原生档成为最快的验证路径（先证明 DirectChannel 通，再攻 3.3.5）。❌ 控制台需维护两套消息入口（代理 + ingest）与来源标记；❌ 两轨同开时聊天可能重复显示，需 UI 明确提示"同一时间只建议启用一轨"。

---

## 6. 实施路线

| 阶段 | 内容 | 验收 |
|---|---|---|
| S1 重构 | Core 类库拆分、Pipeline 抽象、HttpClient 单例修复、缓存持久化+版本号 key | 现有功能回归：过滤/缓存/翻译行为不变 |
| S2 术语表 | GlossaryStore+Applier+UI、占位符保护、cache key 联动 | 术语翻译命中；改术语后旧译文不再出现 |
| S3 Provider | 抽象+3 个云端 Provider+故障转移+DPAPI | llama / openai / google 三路可切换 |
| S4 资产 | HardwareProbe+Recommender+Downloader（镜像回退） | 在无 GPU/有 GPU 两台机器上各出正确推荐并可下载启动 |
| S5 部署桥 | ClientLocator+Configurator+DllDeployer | 一键后冷启动游戏即通，无需手改任何游戏文件 |
| S6 双轨基建 | `/internal/ingest` 端点 + 握手 token + 轨道偏好 UI + 流量来源标记 | ingest 与 8080 走同一 Pipeline 实例，统计正确 |
| T1 模板档验证 | submodule + 构建链 + **112 原生档**跑通 DirectChannel（上游原生，补丁最少，最快见效） | 1.12 客户端内 DLL→ingest→译文回显全链路实测通过 |
| T2 3.3.5 移植档 | patches/335 profile（偏移推导 + 聊天源 hook，参照 GS 版日志行为） | 3.3.5 游戏内实测通过后才可转正（对齐提交流：未经实测不发布） |
| T3 轨道切换 | 偏好切 Track B + Track A 卸载动作（清空 dlls.txt，保留文件） | 切换可逆、回退无残留 |

S1–S3 不触碰游戏侧，随时可上；S5 落地后 v2 即可完整替代手工配置；S6/T 系列独立推进、不阻塞其余阶段——**T 系列任何一步失败都不影响 Track A 主轨在产**。

---

## 7. 需求覆盖对照

| # | 需求 | 落点 |
|---|---|---|
| 1 | 兼容现有插件 | ADR-001 枢纽设计 + PluginBridge 兼容 GS 2.3 |
| 2 | 本地消息过滤 | FilterStage（R0–R6 数据化+自定义规则） |
| 3 | 本地术语表 | GlossaryStage + GlossaryStore |
| 4 | 术语表增删管理 | 术语表 Tab + 版本号失效联动 |
| 5 | 第三方 API | ITranslationProvider 四实现 + 故障转移 |
| 6 | llama 实例/模型管理 | LlamaAssetService + LlamaServerManager |
| 7 | 自有 DLL（双轨副轨） | ADR-007 + VendorBuild 多版本 profile + DirectChannel ingest + T 系列路线 |
| 8 | 一键配置 | Configurator 双写 + 进程门禁 |
| 9 | 硬件推荐+下载 | HardwareProbe + Recommender + 镜像 Downloader |
| 10 | 最大遗漏与风险 | §8 |

---

## 8. 最大遗漏与关键风险（需求 10）

**R1（最大遗漏，影响范围已由 ADR-007 收窄至副轨）｜上游 DLL 不可直接替换——"构建成功"≠"能用"。**
上游 sanjaygbhat/wow-translate 的 DLL 面向 1.12 客户端：Lua 引擎函数地址、`lua_State` 获取方式、DLL↔Addon 协议（Lua 2.0）全部与 3.3.5 不同；你现役的 GS 修改版是带"运行时字节码偏移推导"的深度移植（Lua 已分叉到 2.3）。把上游源码 CMake 一编就替换，客户端大概率静默失效（DLL 加载成功但 Lua API 注册不上）。**需求 7 的真实工作量不在构建，而在移植层**。v2 的应对（双轨决策后更新）：Track A 现役链路不受影响；Track B 先用 **112 原生档**验证 DirectChannel 全链路（补丁最少、最快见效），再攻 3.3.5 移植档（patches/335，以 GS 版日志行为为验收基准）；新 DLL 实测通过前只标"实验"。此外 Track B 上线切换是**整包动作**（DLL + 版本 profile 配对），绝不跨版本混用产物。

**R2（最易被忽视）｜缓存三层失效一致性。**
系统里同时存在：插件 `WoWTranslateCache`（游戏内持久）、控制台 CacheStage、术语表。术语表改了"Thorns→荆棘"后：控制台缓存靠版本号自动失效（已设计），但**插件端缓存里的旧译文永远不会自己更新**——玩家看到的仍是旧译。必须：术语表变更后 UI 提示 `/wt clearcache`，长期靠 Lua 补丁加版本比对（增强项 E2）。不做这条，术语表功能会呈现"改了没效果"的假故障。

**R3（一键配置的时序陷阱）｜游戏运行时写 SavedVariables 必被覆盖。**
客户端退出时整体重写 `WoWTranslate.lua`，运行中写入的配置会被静默冲掉，且不报任何错。Configurator 的进程检测是硬门禁不是提示项（ADR-005）。

**R4（合规/账号风险）｜私服 + MinHook inline hook。**
3.3.5 DLL 的工作原理是 inline hook 引擎函数（`lua_gettop` 等），在带反作弊的私服上属于典型检测特征，存在封号风险。这是插件本身的属性，v2 无法消除，只能：文档明示风险、过滤层降低请求频率（已设计）、不增加 hook 面（v2 任何功能都不得要求新的游戏内 hook）。

**R5（网络现实）｜HuggingFace 直连不可达。**
模型下载若无镜像回退，功能 9 在国内直接不可用。Downloader 必须镜像优先（ADR-006），且 llama.cpp 构建版本要与用户显卡驱动/CUDA runtime 匹配——推荐器需要读取驱动版本而不是猜。

**R6（容量风险）｜聊天高峰 × 本地推理 = 排队雪崩。**
插件按聊天事件逐条发请求，CPU 推理吞吐有限；高峰时请求堆积，表现为"聊天译文越来越晚出现"，用户会误判为"卡死"。v2 的三重缓解：并发上限+排队超时（超时回显原文）、FilterStage 吃掉 88.6% 噪音（已验证）、缓存吃掉 3.9x 重复。控制台应把"排队深度/最长等待"显示在仪表盘上，作为容量预警。

**R7（实现债）｜v1 的每请求 `new HttpClient`。**
长期运行会积累 TIME_WAIT 耗尽临时端口，表现为运行数小时后代理莫名 502。S1 必须先修。

**R8（密钥安全）｜云端 API key 明文落盘。**
settings.json / INI 明文存 Google/OpenAI key。单机工具可接受度有限，v2 默认 DPAPI 加密；INI 是 DLL 读取的固定通道无法加密，文档需说明该文件别随目录分享。

**你尚未意识到的关键问题（汇总）**：R1 的"构建≠能用"（工作量错判风险最大）、R2 的插件端缓存不受控、R3 的配置覆盖时序、R4 的封号面不可扩大、R6 的排队雪崩会被误判为卡死。这五条都不是编码问题，而是验收和运维问题——建议把 R2/R3/R6 写进每个阶段的验收清单。
