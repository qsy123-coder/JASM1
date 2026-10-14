# Product Requirements Document: Mod 市场搜索优化

**Version**: 1.0
**Date**: 2026-10-14
**Author**: Sarah (Product Owner)
**Quality Score**: 92/100

---

## Executive Summary

Mod 市场工具栏的搜索框目前是「输入即搜」：每敲一个字，300ms 防抖后自动重新拉一次列表。这个交互不只是体验问题 —— 它当场就是一个会让**列表永久空掉且无法自愈**的功能性缺陷，用户已经实际撞上（反馈原文：「明明打字搜索了但是列表是空的」）。

本需求做两件事：**① 把触发方式从"输入即搜"改成"回车 / 搜索按钮"**（用户明确要求）；**② 修掉触发方式背后那个真正的 bug**（重载被静默丢弃 + 结果被判定过期丢弃，三件事叠加导致卡死）。顺带把搜索框的特殊字符语义修正为字面量匹配（现在 `_` `%` 会被当通配符），并补上应用里其它 5 处搜索框都有的 Ctrl+F。

范围**仅限 Mod 市场**。Mod 商店页（GameBanana）有同一个缺陷，但它当前在导航栏里是隐藏的（`ShellPage.xaml` 中 `Visibility="Collapsed"`），不在本次范围内。

---

## Problem Statement

**Current Situation**

三件事叠加，构成一个用户可见、且无法自行恢复的卡死：

1. **搜索的重载会被静默丢弃。** `ViewModels/ModMarketViewModel.cs:152-167` 的防抖回调在 300ms 后调用 `_ = ReloadModsAsync()`（**直接调用**），而没有走其它所有筛选器都在用的 `RequestReload()`。区别在于 `RequestReload` 在 `IsLoading` 时会记下 `_reloadPending` 稍后补一次，而直接调用不会。
2. **`LoadModsAsync` 开头有 `if (IsLoading) return;`**（`ModMarketViewModel.cs:258-262`）—— 撞上这个守卫时**请求根本不发**。
3. **过期结果会被丢弃**（`ModMarketViewModel.cs:317-324`，`_loadGeneration` 守卫）—— 上一次请求回来时发现自己过期，被丢掉。

于是：用户输入 → 列表被 `ReloadModsAsync` 清空 → 新请求没发 → 上一个请求的结果被丢弃 → **空列表、无在飞请求、无待办重载**。除非再点一次分类，页面就一直空着。

同一个陷阱在代码里已经被写明过（`ModMarketViewModel.cs:147-149`）：「换分类走 RequestReload…**不能**直接 `_ = ReloadModsAsync()`」。分类入口改过来了，**搜索入口漏了**。

此外还有一批次生问题（完整清单见附录 A）：离开页面不取消防抖、取消令牌从未传给 HTTP 请求、中文输入法下防抖被反复重置、`%` `_` 被当通配符、`*` 结尾/反斜杠结尾导致在线请求报错并静默降级快照。

**Proposed Solution**

- 触发方式改为 **回车键 + 「搜索」按钮**，彻底移除 300ms 防抖与 `CancellationTokenSource`
- 重载统一走 `RequestReload()`，与分类/筛选同一入口
- 清空输入框立即恢复全部结果
- 搜索框特殊字符按字面量处理（两条路径同时改）
- 补 Ctrl+F 聚焦搜索框

**Business Impact**

- 修掉一个**用户已经撞上、且无法自愈**的功能缺陷 —— 这是本次的主要价值，而不是交互偏好。
- 消除"一个字一次请求"：减少无谓的 Supabase 出口流量（该出口配额超限正是市场降级到离线快照的常见原因）。
- 与市场外的其它 5 处搜索框行为对齐，降低认知负担。

---

## Success Metrics

**Primary KPIs**（本项目未见统一埋点设施，以日志计数 + 人工手测为准）

- **搜索后列表非空率**：对一个库中确有匹配的关键词，搜索操作后列表必须非空。当前该指标在"加载中搜索"这一路径上为 0%（卡死）。
- **无卡死**：连续快速操作（输入 → 立刻点分类 → 立刻搜索 → 立刻清空）后，页面总能回到一个与当前筛选状态一致的列表；不再出现"空列表且无任何加载中"的状态。
- **请求次数**：输入 8 个字符的过程产生的列表请求数从 8 次降到 0 次（只有回车/按钮那一次）。

**Validation**: 按「MVP Scope」的验收清单做本机手测；发布后收集用户日志确认无异常。

---

## User Personas

### Primary: 找特定 mod 的玩家

- **Role**: 知道大概名字（角色名、皮肤名片段），到市场里找它
- **Goals**: 快速缩小到几条结果，看到就走
- **Pain Points**: **搜完列表是空的**，只能退出去重新点分类；输入过程中列表反复闪动
- **Technical Level**: 初级到中级

---

## User Stories & Acceptance Criteria

### Story 1: 搜索不再卡死

**As a** 找特定 mod 的玩家
**I want to** 搜索任何关键词后都能看到结果（或明确的"没有找到"），而不是一片空白
**So that** 我能找到东西

**Acceptance Criteria:**
- [ ] 在列表正在加载时触发搜索，**不会**出现"列表空、无加载指示、无待办重载"的卡死状态
- [ ] 库中确有匹配时，搜索后列表非空
- [ ] 库中确无匹配时，显示「没有找到 Mod」空状态（而非空白）
- [ ] 连续快速操作（输入 → 切分类 → 搜索 → 清空）后，列表内容始终与"当前分类 + 当前关键词 + 当前筛选"一致
- [ ] 快速连续触发搜索不会让列表混入上一次关键词的结果

### Story 2: 由我决定什么时候搜

**As a** 找特定 mod 的玩家
**I want to** 把词打完了再按回车或点「搜索」，而不是每打一个字就被搜一次
**So that** 列表不会在我打字过程中反复闪烁、滚动位置不会丢

**Acceptance Criteria:**
- [ ] 纯键盘输入（不按回车）**不产生任何列表请求**
- [ ] 按 **Enter** 触发搜索
- [ ] 点击「搜索」按钮触发搜索
- [ ] 搜索按钮位于输入框右侧，与工具栏其它控件同高、同圆角、同间距
- [ ] 输入法上屏过程（中文拼音候选）不产生列表请求，直到用户按回车或点按钮
- [ ] 触发搜索时，输入框内容与列表内容一致（不出现"框里是 A、列表是 B"）

### Story 3: 清空与筛选叠加

**As a** 找特定 mod 的玩家
**I want to** 点输入框的 ✕ 就能回到全部结果；搜索后切分类时关键词还在
**So that** 我不用记住"还挂着一个看不见的条件"

**Acceptance Criteria:**
- [ ] 点击输入框自带的清空（✕）按钮，**立即**恢复全部结果，无需再按回车
- [ ] 搜索生效后再切换分类 / 改筛选，关键词保留并叠加（如"在爱弥斯分类里搜 v2"）
- [ ] 关键词与筛选叠加后无结果时，空状态文案能说明是"当前条件下没有结果"

### Story 4: 特殊字符与快捷键

**As a** 找特定 mod 的玩家
**I want to** 搜 `a_b` 就只匹配含 `a_b` 的，并且能用 Ctrl+F 快速聚焦搜索框
**So that** 搜索结果符合直觉，操作顺手

**Acceptance Criteria:**
- [ ] 搜 `_`、`%`、`\` 时按**字面量**匹配，不再当通配符（在线与离线快照两条路径行为一致）
- [ ] 搜索框中输入以 `\` 结尾的内容时，在线请求不再报错、不再静默降级到快照
- [ ] Ctrl+F 聚焦搜索框（与角色页、角色管理、Mods 总览、角色详情、预设页一致）
- [ ] 见「Dependencies & Blockers」：`*` 的处理方式待定，其验收标准随该决定确定

---

## Functional Requirements

### Feature 1: 触发方式改造

- Description: 删除 `ModMarketViewModel` 的 `OnSearchTextChanged` 防抖（`_searchCts` + `Task.Delay(300)` 整块），改为显式触发。
- User flow: 输入（不搜）→ 按 Enter 或点「搜索」→ 以当前输入框内容重载列表。
- `SearchText` 仍是 `TwoWay` 绑定并保持 `UpdateSourceTrigger=PropertyChanged` —— **"输入框里的内容就是查询条件"**，不引入"已提交/未提交"两份状态（两份状态会导致"我明明改了词、切个分类却搜的是旧词"这类更难解释的行为）。
- Edge cases: 输入为空白时触发搜索 = 等价于清空搜索（走 Feature 3 的路径）。
- Error handling: 加载失败沿用现有的错误提示 + 重试。

### Feature 2: 修掉重载被丢弃

- Description: 搜索触发改走 `RequestReload()`（与 `OnSelectedCategoryChanged` / 三个筛选器同一入口）。它同时做两件事：作废在飞结果（`_loadGeneration++`）、并在 `IsLoading` 时记下 `_reloadPending`。
- Edge cases: 防抖移除后，`OnSearchTextChanged` 只剩"值变空则重载"一条职责（Feature 3）。
- Error handling: 不适用（这是正确性修复）。

### Feature 3: 清空即恢复

- Description: 输入框内容变为空白时立即触发重载（恢复全部）。
- User flow: 点 ✕（或全选删除）→ 列表回到"无关键词"的状态。
- Edge cases: 用户逐字删到空，与点 ✕ 等效 —— 都以"内容为空"为触发条件，不特判 ✕ 本身。
- Error handling: 不适用。

### Feature 4: Ctrl+F

- Description: 页面级键盘加速器，聚焦搜索框。
- Edge cases: 页面未获得焦点时不生效（与其它页面一致）。

### Feature 5: 特殊字符按字面量

- Description: 用户输入中的 LIKE 元字符按字面量处理。
- 实现要点：**不能再依赖 PostgREST 的 `*` 简写**（实测它会把值里所有 `*` 无条件转成 `%`）。改用 URL 编码的真 `%` 作为"包含"分隔符，并对用户输入做 Postgres LIKE 转义（`\` → `\\`、`%` → `\%`、`_` → `\_`）。
- **两条路径必须同时改**：在线（`Services/ModMarketService.cs` 的 `BuildQuery`）与离线快照引擎（`Core/ModMarket/…/MarketLikePattern.cs`）。后者是刻意复刻 Postgres `ilike` 语义写的，带单测；改它必须连单测一起改，否则两条路径会在降级时给出不同结果。
- Edge cases: 见 Blockers 中的 `*`。
- Error handling: 转义后仍请求失败时，沿用现有的快照降级（但不应再因为"结尾反斜杠"这种可预期的输入而失败）。

### Out of Scope

- **Mod 商店页（GameBanana）的搜索** —— 该页入口当前隐藏；它有同一类缺陷（见附录 A 第 ② 条），本次不动
- 搜索**匹配字段**的扩展：仍**只搜标题**（不含角色名、简介）。用户已确认：标题里带角色名前缀，够用
- 搜索历史、搜索建议、自动补全
- 高亮匹配片段
- 服务端全文检索 / 索引优化

---

## Technical Constraints

### Performance

- 输入过程零请求；一次搜索一次请求。
- 搜索触发的重载与现有分类/筛选重载同路径同开销（一次 PostgREST 查询，24 条/页）。

### Security

- 不新增密钥或凭证；沿用既有 `Supabase` 命名 HttpClient。
- **转义是正确性要求而非安全要求**：搜索词已经过 `Uri.EscapeDataString`（防注入），本次补的是 LIKE 元字符转义（防误匹配）。两者不可互相替代。

### Integration

- **Supabase / PostgREST**: `title=ilike.…` 过滤，`Services/ModMarketService.cs` 的 `BuildQuery`。
- **离线快照引擎**: `Core/ModMarket/MarketLikePattern.cs` + `MarketQueryEngine.Filter`，带 xUnit 单测。
- **其它页面的 Ctrl+F 实现**：`CharactersPage.xaml`、`CharacterManagerPage.xaml`、`ModsOverviewPage.xaml`、`CharacterDetailsPage.xaml`、`PresetDetailsPage.xaml` —— 新加的应与之一致。

### 落点清单（影响文件）

| 文件 | 改动 |
|---|---|
| `Views/ModMarketPage.xaml` | 搜索框旁加「搜索」按钮；页面加 Ctrl+F 键盘加速器 |
| `Views/ModMarketPage.xaml.cs` | 搜索框 KeyDown（Enter）；Ctrl+F 聚焦 |
| `ViewModels/ModMarketViewModel.cs` | 删防抖；`OnSearchTextChanged` 只留"清空即重载"；新增 `SearchCommand` 走 `RequestReload()` |
| `Services/ModMarketService.cs` | `BuildQuery` 的搜索条件：改真 `%` 分隔 + LIKE 转义 |
| `Core/ModMarket/MarketLikePattern.cs` | 元字符语义改为字面量（与在线侧对齐） |
| `JASM.Tests`（对应单测） | 跟着 `MarketLikePattern` 一起改 |

### 实现前必须完成的验证（不得跳过）

用一组精确用例在**在线**与**快照**两条路径上逐条比对同一关键词的结果集。已知必须覆盖：

1. `爱弥斯`（普通中文）—— 两条路径结果集应一致
2. `a_b`（含下划线）—— 应只匹配字面 `a_b`
3. `50%`（含百分号）—— 应只匹配字面 `%`
4. `a\`（**结尾反斜杠**）—— 在线不得报错
5. `*`（星号）—— 见 Blockers

> ⚠️ 首次实测中「`\_` 转义」出现了一处**未能解释的偏差**（库中仅 3 条标题含真下划线，但转义后的查询返回 30 条）。这意味着转义方案**尚未被验证正确**，实现时必须先把它解释清楚再往下写。

---

## MVP Scope & Phasing

### Phase 1: MVP（本次交付）

- 触发方式：回车 + 「搜索」按钮，移除防抖
- 修掉重载被丢弃导致的卡死（Feature 2）
- 清空即恢复（Feature 3）
- Ctrl+F（Feature 4）
- 特殊字符字面量匹配（Feature 5，`*` 另行决定）

**MVP Definition**: 用户把词打完按回车，必然得到一个与输入一致的列表（或明确的空状态），且在加载中随手操作也不会把页面弄成卡死 —— 这个闭环成立即算达标。

### Phase 2: Enhancements（候选，未承诺）

- Mod 商店页搜索的同类修复
- 搜索匹配扩到角色名
- 空状态文案区分"无结果"与"有筛选条件导致的无结果"

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|------|------------|--------|---------------------|
| LIKE 转义改动导致两条路径结果集不一致 | Med | High | 实现前逐条比对两条路径（见上表）；转义逻辑保持"同一处结论喂给两侧"的既有形状 |
| `*` 无法按字面量表达，留下行为不对称 | High | Low | 见 Blockers —— 需用户决策，不自行拍板 |
| 移除防抖后用户"打完忘了按回车"，以为搜索坏了 | Med | Med | 回车与按钮都是显式动作、可见；清空即恢复；空状态文案说明当前条件 |
| 改动 `MarketLikePattern` 破坏其既有单测 | Med | Med | 单测与新语义一起改，不绕过 |
| 两个入口（搜索 / 分类）再次分叉 | Low | High | 两者都必须走 `RequestReload()`；在 `RequestReload` 的注释里写明"任何会改变查询条件的入口都要走这里" |

---

## Dependencies & Blockers

**Dependencies:**

- Supabase `mods` 表（实测可用）
- `MarketLikePattern` 的既有单测（改动需同步）

**Known Blockers:**

- ⚠️ **字面星号 `*` 在在线路径上无法表达。** 实测：PostgREST 会把过滤值里的**所有** `*` 无条件转成 `%`（`ilike.%*%` 命中 1000 ≈ 全部，而库中标题含 `*` 的为 0 条）。因此：
  - 在线路径：搜 `*` 只能得到"通配"行为，做不到字面量
  - 离线快照路径：可以做到字面量（我们自己的正则引擎）
  - **两条路径必然不一致** —— 这与仓库「两条路径必须同口径」的原则冲突
  - 需决策：(a) 接受 `*` 的这一处不一致并写进注释与文档；(b) 只对 `%` `_` `\` 做字面量、`*` 明说保持通配；(c) 想别的办法（暂未见可行方案）
  - **在该决策落定前，Feature 5 的验收标准不含 `*`。**

---

## Appendix

### A. 代码走查发现的完整缺陷清单

| # | 缺陷 | 位置 |
|---|---|---|
| ① | 搜索重载被静默丢弃 → 列表永久空 | `ModMarketViewModel.cs:152-167`（不走 `RequestReload`）+ `:258-262`（`IsLoading` 提前返回）+ `:317-324`（过期丢弃） |
| ② | 商店页同一缺陷，且无代次保护 → 显示**上一个关键词**的结果 | `ModStoreViewModel.cs:331-347`、`:714+` |
| ③ | 商店页注释「跟市场页同值」是错的（400ms vs 市场 300ms） | `ModStoreViewModel.cs:34-35` |
| ④ | 离开市场页不取消防抖，待发的搜索仍会触发重载 | `ModMarketViewModel.cs:143`（空实现） |
| ⑤ | 取消令牌从未传给 HTTP 请求，新按键取消不了在飞请求 | 两个 VM 的防抖 + 服务调用 |
| ⑥ | `%` `_` 被当通配符（实测确认） | `ModMarketService.cs:480-481`、`MarketLikePattern.cs` |
| ⑦ | 结尾反斜杠 → 在线 400 → 静默降级快照 | `ModMarketService.cs:480-481` |
| ⑧ | 市场不过滤首尾空格（`"aeme "` → `%aeme %`），商店过滤 | `ModMarketViewModel.cs:314` vs `ModStoreViewModel.cs:773` |
| ⑨ | 两个搜索框都没有回车 / 快捷键（应用里其它 5 处都有） | `ModMarketPage.xaml:240-249`、`ModStorePage.xaml:247-256` |

### B. 实测记录（2026-10-14，对 Supabase `mods` 表）

| 查询 | 命中 | 说明 |
|---|---|---|
| `ilike.*爱弥斯*` | 45 | 基线 |
| `ilike.*爱_斯*` | 45 | `_` 当通配符，结果与基线**完全相同** |
| `ilike.*爱\_斯*` | 0 | `\` 转义生效 |
| `ilike.%爱弥斯%`（真百分号） | 45 | 可不用 PostgREST 星号简写 |
| `ilike.%_%` | 1000 | `_` 通配 |
| `ilike.%\_%` | 30 | ⚠️ 与「库中含 `_` 的标题 = 3 条」不符，**未解释** |
| `ilike.%*%` | 1000 | ⚠️ `*` 被无条件转成通配符 |

库中实际含特殊字符的标题数：下划线 3 条、百分号 0 条、星号 0 条。

### Glossary

- **重载（Reload）**: 从第一页重新拉取列表，丢弃已有内容。
- **代次（generation）**: `ModMarketViewModel._loadGeneration`，用于判定 `await` 回来的结果是否已过期。
- **两条路径**: 在线（Supabase / PostgREST）与离线快照降级（COS 快照 + 本地查询引擎）。

### References

- 现状代码：`ViewModels/ModMarketViewModel.cs`、`Services/ModMarketService.cs`、`Core/ModMarket/MarketLikePattern.cs`
- Ctrl+F 的既有实现：`Views/CharactersPage.xaml` 等 5 处
- 相关既有 PRD：`docs/daily-updates-page-prd.md`

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
