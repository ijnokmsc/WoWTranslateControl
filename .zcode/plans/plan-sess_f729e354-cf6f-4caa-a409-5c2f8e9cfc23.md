# WoWTranslateControl 3.0 — 单轨化 + 统计精简 + 双层缓存

## 分支与版本
1. master 上未提交的 2.1.3 工作先提交为收尾 commit，再从 master 创建分支 `3.0`
2. 版本号 2.1.3 → 3.0.0（csproj / direct_main.cpp 两处 / driver 横幅升 driver v36）；更新日志.txt 新增 3.0.0 段（含 breaking change 说明）

## 一、彻底移除 Track A（GS 插件）支持
禁用机制已确认：**只靠 DLL 覆盖**——EnsureDeployed 强制部署 Track B 的 dinput8.dll 后 WoWTranslate335.dll 天然失活，不改 .toc、不断 Lua、不删用户文件。vendor\wow-translate 模板保留。

1. 删除 `Core\PluginConfigurator.cs` 整文件
2. `Core\DllSwitcher.cs` 单轨化：删 TrackGs 常量、TrackFiles 去 WoWTranslate335.dll、Probe 的 hasGs/Track A 文案、SwitchTo 的 wtc_backup/gs 恢复分支、Rollback 的 GS 分支
3. `Models\AppConfig.cs`：删 PluginTrack 属性（老 settings.json 残留键自动忽略）
4. `MainWindow.xaml/.cs`：删"高级：GS 插件备用配置"卡片、BtnTrackGs、BtnConfigurePlugin_Click 及绑定；频道过滤卡片 GS 文案改写；RefreshDllStatus 去 Track A 分支
5. **过滤规则只留频道过滤**：MessageFilter.cs 删 R1-R6 用户规则；FilterStage.cs 去内容规则段；AppConfig 删对应开关+RuleCatalog 条目；MainWindow 删 R1-R6 复选框。保留 R7（乱码）/R8（中文在场）内部质检与 C0-C8 频道开关
6. ProxyServer 的 `\1CH\1` 标签剥离保留（非 GS 专属）
7. 测试：DllSwitcherTests 删 GS 用例保留 direct 用例；冒烟工具删 BtnConfigurePlugin/TxtPluginReport 断言；MessageFilter 删 R1-R6 用例
8. 文档：使用教程.txt 删 GS 回滚节；ARCHITECTURE_V2.md 加 3.0 单轨 ADR 备注；更新日志 3.0.0 段

## 二、删除"按规则命中分布"统计卡片
探查证实该卡片对 Track B 半死（C0-C8 频道行永远为 0——频道标签只有 GS 补丁会传；R 行外发又不计）：
- 删 MainWindow.xaml "按规则命中分布"卡片（L631-649 RuleStatsList）、_ruleStats/RuleStatRow/_ruleHits/更新与重置逻辑、RuleCatalog 驱动的行填充
- 顶部计数条（总/过滤/缓存/模型/错误/节省%）**保留**（数据源独立）；流量表格保留

## 三、双层缓存（按你的方案落地）
现状缺陷：单层 2000 条 LRU，世界频道刷屏很快把热词条目挤掉 → 大量重复消息无法命中；LastUsed 重启后失真。

**结构**（仍在 CacheStage.cs 内演进，cache.json 格式升级）：
- **第二层（热层）**：命中次数 ≥ 3（可配 hotThreshold）的词条晋升；**不参与长度淘汰**，持久保留，只有手动清缓存/版本升级才清。消息进来**先查热层** → 命中直接返回
- **第一层（温层）**：现机制的精确匹配层。按**累计文本长度**预算淘汰（替代现在的条数上限，默认 256K 字符，超限淘汰最旧 LastUsed 的 1/4）；命中计数 +1，达到阈值晋升热层
- **查找顺序**：热层 → 温层 → Glossary/模型，回写时写入温层并累加计数（热层命中也累加，用于观察）
- **持久化**：cache.json 条目从 `[译文, LastUsed]` 扩展为 `[译文, LastUsed, hits]`，加载时保留真实 LastUsed（顺带修复重启后 LRU 失真）；key 前缀 kv2→kv3（3.0 一次性清空 2.x 旧条目）；热层也落盘
- 兼容：读旧格式（无 hits）按 hits=0 温层接纳
- 测试：CacheStageTests 补晋升/长度淘汰/查找顺序/旧格式兼容用例

## 验证与发布
- dotnet build + 全部单测 + 冒烟（断言清单同步更新）全绿
- build.bat 重建 v36 引擎 DLL → assets 同步
- publish（默认 + win-x64 单文件）同步 publish/ 与 D:\WoWTranslateControl-single（教程/更新日志/glossary）
- 分支上提交，master 不动；打包 zip 由你跑 打包发布.ps1