# Product Requirements Document: 每日更新页面（应用内）

**Version**: 1.1
**Date**: 2026-10-14
**Author**: Sarah (Product Owner)
**Quality Score**: 91/100

> **v1.1 修订**：v1.0 把「每日」理解为按 `updated_at`（最后修改时间）分组 —— 这是错的。
> wave-mod 站点在其 `src/lib/mods-domain/daily.ts` 里明写规则：**只按 `created_at` 归组，绝不用 `updated_at`**，
> 且日期一律换算到 **Asia/Shanghai**、窗口内的空天也要列出来。本版按该规则改写第 5/6/8 节，
> 并撤回 v1.0 里所有围绕 `updated_at` 的设计（含对 `ModMarketService` 的改动）。详见第 12 节「修订记录」。

---

## Executive Summary

新客户端（JASM1 线）的 Mod 市场工具栏上有一个「每日更新」按钮，点击后唯一的行为是把用户踢到系统默认浏览器打开 `https://www.wave-mod.top/updates`。用户离开应用、浏览完还得自己切回来。

本需求把它改成应用内页面：展示最近 N 天**新增**的 mod，按天分组，点开任一条复用现有的详情抽屉看详情并下载。入口位置不变（仍是市场工具栏那个按钮），只是从「跳浏览器」变成「页内导航」。

**分组口径不自己发明，逐条对齐 wave-mod 站点**（`src/lib/mods-domain/daily.ts`）：按 `created_at` 换算到 Asia/Shanghai 的日历日归组、绝不用 `updated_at`、窗口内空天也列出。这三条决定用户在应用里看到的日期能否与站点一致 —— 而「对得上」正是这一页唯一的价值。

预期影响：入口的跳失消失；应用第一次拥有「时间维度」的浏览能力，为后续日更订阅、一键安装等能力打地基。

---

## Problem Statement

**Current Situation**

- 入口在 `src/GIMI-ModManager.WinUI/Views/ModMarketPage.xaml:266-271`，工具栏第 5 列一个文字按钮。
- 行为只有一跳：打开外部浏览器指向 `https://www.wave-mod.top/updates`。应用内不渲染任何内容。
- ⚠️ **该站点现已整体下线**：2026-10-14 实测 `wave-mod.top` 的根路径与 `/updates` 一律返回 **HTTP 410 Gone**；其旧 Supabase 项目（`xqwzgcxwdwpmkdbmzmve`）的 `mods` 表已读不到任何行。也就是说这个按钮**当前点了也是死链**，这本身构成上线该功能的独立理由。
- 因此不存在「站点在跑、应用内看不到」的过渡态 —— 这一页无论做不做，用户都已经拿不到那份内容了。

**Proposed Solution**

新增应用内「每日更新」页：数据仍来自应用自己的 Supabase `mods` 表（新线 `izxivkmgtrkkpxtedeco`，与市场同一张表），查询条件为「最近 N 天内创建」，按 Asia/Shanghai 日历日分组呈现，点卡片复用市场现成的详情抽屉。

**Business Impact**

- 恢复一个**当前已断**的功能入口（站点 410）。
- 消除「跳出应用」的断点，且不再依赖任何第三方站点的可用性。
- 为「日更」这类时间维度能力打地基。

---

## Success Metrics

**Primary KPIs**（本项目当前未见统一埋点设施，MVP 阶段以**日志计数 + 人工手测**为准，不为此新引入埋点依赖）

- 入口有效性：点击「每日更新」100% 进入应用内页面，不再触发 `Launcher.LaunchUriAsync`。
- 日期可信度：**应用内某一批 mod 所属的日期，与 wave-mod 站点对同一批 mod 的日期一致**（按同一条规则推算，见第 5 节）。
- 降级可用性：切断网络 / Supabase 不可达时，页面仍能渲染出内容并显示降级横幅，且**分组日期不变**（快照行同样带 `created_at`）。

**Validation**: 发布前按「MVP Scope」的验收清单做本机手测；发布后首版收集用户日志，确认无加载异常。

---

## User Personas

### Primary: 追更型 mod 玩家（与 Mod 市场用户同一群体）

- **Role**: 每天或隔天开一次 JASM，关心「最近有什么新东西」
- **Goals**: 快速扫一眼最近几天新增了什么，看到感兴趣的立刻看详情、下载
- **Pain Points**: 现在点「每日更新」要么跳出应用，要么（站点下线后）是个死链
- **Technical Level**: 初级到中级

---

## User Stories & Acceptance Criteria

### Story 1: 在应用内查看最近新增

**As a** 追更型 mod 玩家
**I want to** 点「每日更新」后在应用里直接看到最近 N 天新增的 mod，并按天分组
**So that** 我不用跳出应用就能扫到「今天 / 昨天新来了什么」

**Acceptance Criteria:**
- [ ] 点击 Mod 市场工具栏的「每日更新」按钮，导航到新的应用内页面（`Frame.Navigate`），不打开外部浏览器
- [ ] 分组依据是 mod 的 **`created_at`**，**不是 `updated_at`** —— 一个几个月前创建、昨天被人改了标题的 mod，必须仍然留在它创建的那一天
- [ ] 日期一律按 **Asia/Shanghai**（固定 UTC+8）换算，**不按本机时区**
- [ ] 分组标题格式与站点一致：`2026年10月14日（今天）` / `（昨天）` / 无后缀
- [ ] 窗口内的**空天也要出现**，显示「这天没有新增」，而不是直接跳过
- [ ] 组内条目按 `created_at` 倒序
- [ ] 数据窗口为最近 7 天（含今天），窗口外的 mod 不出现
- [ ] 可以正常返回，返回后 Mod 市场页状态不丢

### Story 2: 从列表进入详情并下载

**As a** 追更型 mod 玩家
**I want to** 点开列表里的一条 mod 看它的详情，并像在市场里一样下载
**So that** 发现感兴趣的东西时动作不被打断

**Acceptance Criteria:**
- [ ] 点击卡片打开详情抽屉（复用 `ModDetailPanel`），内容与 Mod 市场详情完全一致
- [ ] 详情里的下载入口可正常使用（沿用市场既有下载路径）
- [ ] 关闭抽屉后列表滚动位置与分组保持不丢

### Story 3: 没有更新 / 数据降级时的诚实反馈

**As a** 追更型 mod 玩家
**I want to** 在「最近没人更新」或「数据不是最新的」时看清楚原因，而不是看到一片空白
**So that** 我不会误以为功能坏了

**Acceptance Criteria:**
- [ ] 整个窗口无任何新增时，显示明确空状态文案（「最近 7 天没有新的更新」），不显示空白列表
- [ ] 走离线快照通道时，显示降级横幅说明数据快照的时间；**分组日期与在线模式完全一致**（快照行同样带 `created_at`，无需任何近似口径）
- [ ] 加载失败时显示错误信息并给可重试入口，不出现永久 loading
- [ ] 撞到单次请求条数上限时，页面顶部明示「只显示最新的 N 条」，**不静默截断**

---

## Functional Requirements

### 5. 分组规则（本页的核心，逐条对齐 wave-mod）

规则出处：wave-mod 仓库 `src/lib/mods-domain/daily.ts`。三条硬规则，不得自行发挥：

| 规则 | 内容 | 为什么 |
|---|---|---|
| 分组字段 | **`created_at`**（首次创建时间） | 下架再上线、改标题、补网盘链接都只动 `updated_at`。按 `updated_at` 分组会让老 mod 冒充「今天的新更新」，且日期与站点对不上 |
| 时区 | **Asia/Shanghai**，固定 UTC+8 | 中国无夏令时，固定偏移即可（站点注释原文：「中国无夏令时，按固定 24h 递减即可」）。**不能按本机时区** —— 否则同一批 mod 在不同机器上落到不同的天 |
| 空天 | **窗口内每一天都出一节** | 「今天还没更新」本身就是用户想知道的信息 |

### Core Features

**Feature 1: 页面与导航入口**

- Description: 新建 `DailyUpdatesPage` / `DailyUpdatesViewModel`，替换现有的外链行为。
- User flow: 市场点「每日更新」→ `Frame.Navigate` → `OnNavigatedTo` 发起查询 → 渲染分节列表 → 可返回。
- Edge cases: 冷启动直接进页面也要能加载；重复进入不重复堆叠。
- Error handling: 查询失败不抛异常到 UI 线程，走页面级错误提示 + 重试。

**Feature 2: 窗口查询**

- Description: 查询 Supabase `mods` 表，取「最近 N 天内创建」的一批，按 `created_at` 倒序。
- 实现复用：**复用 `ModMarketService.GetModsAsync` 的既有参数** —— `sortBy: "Newest"` 给出 `order=created_at.desc`，`updatedAfter` 过滤 `created_at >= …`（该参数名字叫「更新」但比的确实是 `created_at`，正好是这里要的语义；它是市场页「最近更新」在用的既有行为，本次不动）。
- ⚠️ **无需新增任何服务层参数**，也无需把 `updated_at` 加进 `GridSelect` —— 列表列里本来就有 `created_at`。v1.0 曾计划新增 `updatedAtAfter`，已撤回。
- 窗口下界：上海窗口首日零点换算成 UTC 绝对时刻；服务层会把它截断到 UTC 日（只会更早），保证不漏，多捞的部分在客户端按上海日精确收口。
- 条数上限：单次请求 1000 条（即 PostgREST `db-max-rows` 硬上限）。按库里每日 5~15 条的量，7 天窗口远够不着；超出时页面明示截断。
- Error handling: 无结果返回空列表而非报错。

**Feature 3: 按天分组与渲染**

- Description: 复用市场卡片视觉与 `WrapGridPanel`，外层按上海日历日分节。
- User flow: 一次拉取窗口内全部结果 → 客户端按 `created_at` 的上海日归组 → 顺序渲染日期节 + 卡片。
- Edge cases: 空天只出一行「这天没有新增」说明，不铺空面板；单日条目很多时分组不被截断；卡片缺缩略图沿用市场的占位与重试逻辑。
- Error handling: 分组逻辑对空集合、单条集合都不能崩。

**Feature 4: 详情抽屉复用**

- Description: 点击卡片设置 `SelectedMod`，复用 `ModDetailPanel`。
- 与市场 `OpenModDetail` / `CloseDetailPanel` 行为完全一致。

**Feature 5: 移除站点出口**

- Description: 去掉 `ModMarketViewModel.OpenWebsite` 命令与 `ModWebsiteUrl` 常量，按钮改绑导航命令。
- Edge cases: 删除前确认没有别处引用（含 XAML 绑定与本地化资源）。

### Out of Scope

- 一键安装 / 批量下载 / 日更订阅 / 收藏 / 更新提醒
- 站内通知或系统通知推送
- 站点那样的「日期胶囊」跳转条（`daily-update-pills.tsx`）—— 本页是单页滚动，不需要胶囊
- 窗口天数可调、角色 / NSFW 筛选、搜索
- 在页内嵌入 WebView

---

## Technical Constraints

### Performance

- 页面进入后 3 秒内出结果（联网、Supabase 正常），与 Mod 市场首屏一致。
- 单次请求 ≤ 1000 条；7 天窗口实际约 50~100 条。

### Security

- 复用既有的 `Supabase` 命名 HttpClient 与 `ModMarketOptions` 配置（`App.xaml.cs`），不新增密钥或凭证。
- 沿用现有 `is_published=eq.true&is_available=eq.true` 过滤。

### Integration

- **Supabase / PostgREST**: `mods` 表，复用 `ModMarketService.GetModsAsync`。
- **离线快照**: `ModMarketSnapshotService`（COS gz 快照）—— 快照行**带 `created_at`**，分组与排序可原样复用，**降级无需口径让步**（站点自己的兜底路径也是复用同一套分组）。
- **导航框架**: `Services/PageService.cs` 注册表 + `App.xaml.cs` 的 `AddTransient` + `Services/NavigationService.cs`。

### Technology Stack

- .NET 9 / WinUI 3 / Windows App SDK 1.7，CommunityToolkit.Mvvm 8.4。
- 页面在 code-behind 构造器中通过 `App.GetService<TViewModel>()` 取 VM（与 `ModMarketPage.xaml.cs:29` 一致）。

### 落点清单（影响文件）

| 文件 | 改动 |
|---|---|
| `Views/DailyUpdatesPage.xaml` + `.xaml.cs` | 新建页面（复用市场卡片模板与 `ModDetailPanel`） |
| `ViewModels/DailyUpdatesViewModel.cs` | 新建 VM（窗口查询 / 上海日分组 / 降级横幅 / 截断提示） |
| `Models/DailyUpdateGroup.cs` | 新建（日期节模型 + 站点同款标题） |
| `Services/PageService.cs` | 加 `Configure<DailyUpdatesViewModel, DailyUpdatesPage>()` |
| `App.xaml.cs` | 两个 `AddTransient`（Page + VM） |
| `Views/ModMarketPage.xaml:266-271` | 按钮 Command 改绑导航 |
| `ViewModels/ModMarketViewModel.cs` | 去掉 `OpenWebsite` / `ModWebsiteUrl`，改为导航命令 |

**`Services/ModMarketService.cs` 不在改动清单里** —— v1.0 曾要改它（加 `updated_at` 到 `GridSelect`、新增 `updatedAtAfter` 参数、改降级告警），v1.1 全部撤回。

---

## MVP Scope & Phasing

### Phase 1: MVP（本次交付）

- 应用内「每日更新」页 + 入口改造（原按钮 → 页内导航）
- 最近 7 天窗口，按上海日历日的 `created_at` 分组（含空天），组内 `created_at` 倒序
- 复用详情抽屉
- 空状态 / 降级横幅 / 截断提示 / 错误重试

**MVP Definition**: 用户点「每日更新」能在应用内按天看到最近 7 天的新增、并能打开详情 —— 这个闭环成立即算达标。

### Phase 2: Enhancements（上线后）

- 窗口天数可调（对齐站点的 `?days=`，1~30，默认 14）
- 日期胶囊跳转条（对齐站点 `daily-update-pills.tsx`）
- 角色 / NSFW 筛选

### Future Considerations

- 收藏 / 订阅日更、更新提醒
- 从日更列表直接触发下载 / 安装

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation Strategy |
|------|------------|--------|---------------------|
| 分组口径与站点再次漂移 | Med | High | 规则出处写进代码注释（`DailyUpdatesViewModel` / `DailyUpdateGroup`），并注明站点文件路径；日后站点改规则时能对上号 |
| 快照行缺 `created_at` | Low | Med | 快照实测带该列；VM 对 Kind 缺失的时间戳按 UTC 解，避免日期整体挪一天 |
| 本机时区与上海不同导致日期错位 | Low | High | 固定 UTC+8 偏移，不依赖本机时区，也不依赖 tz 数据库 |
| 单日导入量暴涨导致截断 | Low | Low | 1000 条上限 + 超限明示，不静默截断 |
| 删除入口按钮后 XAML / 本地化资源残留引用 | Low | Low | 删除前全局检索 `ModWebsiteUrl` / `OpenWebsite` |

---

## Dependencies & Blockers

**Dependencies:**

- 自己那份 Supabase `mods` 表（新线 `izxivkmgtrkkpxtedeco`）—— 已实测可用，数据按天批次导入（每批同一时刻 `04:00Z` = 北京 12:00，每日 5~15 条，全部 `game_key = wuthering-waves`）
- Mod 市场现有的 `ModMarketService` / `ModDetailPanel` / 卡片模板（已在仓库内）

**Known Blockers:**

- 无。（v1.0 记的「`updated_at` 语义待核实」随分组改用 `created_at` 而消失。）

---

## Appendix

### 12. 修订记录

**v1.0（2026-10-14）**：初版。数据源选「复用应用内 Supabase」，分组按 `updated_at`，降级按 `created_at` 近似。

**v1.1（2026-10-14）**：用户反馈「时间对不上」，核查 wave-mod 仓库后确认 v1.0 的分组字段选错了。

- **改**：分组字段 `updated_at` → **`created_at`**（站点 `daily.ts` 明写「只按 createdAt 归组，绝不使用 updatedAt」）
- **改**：日期换算从「本地时区」→ **Asia/Shanghai 固定 UTC+8**
- **加**：窗口内**空天**也列出（站点规则）
- **改**：分组标题格式对齐站点的 `fullLabel`（`2026年10月14日（今天）`）
- **撤**：`ModMarketService` 的全部改动（新增 `updatedAtAfter`、`GridSelect` 加 `updated_at`、降级告警）—— 改用既有的 `updatedAfter`（其真实语义就是比 `created_at`）+ `sortBy: "Newest"`
- **撤**：「降级时按 `created_at` 近似」的整套说法 —— 分组本来就按 `created_at`，降级没有任何口径让步
- **记**：wave-mod 站点已整体 410、其旧 Supabase 已空，本页因此是在**恢复一个已断的入口**，而非单纯把外链搬进来

### Glossary

- **每日更新**: 本需求中的功能名，指「最近 N 天**新增**的 mod」列表页。
- **降级（Degraded）**: Supabase 不可达时改用 COS 离线快照提供数据的状态，市场页已有此机制与横幅。
- **详情抽屉**: `ModDetailPanel`，市场页右侧滑出的 mod 详情面板。

### References

- **分组规则出处**: `D:\BaiduNetdiskDownload\WaveMod\src\lib\mods-domain\daily.ts`（及 `src/app/api/updates/route.ts`）
- 现状代码：`Views/ModMarketPage.xaml:266-271`、`ViewModels/ModMarketViewModel.cs`
- 数据通道：`Services/ModMarketService.cs`（`GetModsAsync`、`BuildQuery`）
- 快照降级：`Services/ModMarketSnapshotService.cs`、`Core/ModMarket/MarketQuery.cs`
- 导航框架：`Services/PageService.cs`、`Services/NavigationService.cs`、`App.xaml.cs`
- 站点：`https://www.wave-mod.top/updates`（**已 410 Gone**）

---

*This PRD was created through interactive requirements gathering with quality scoring to ensure comprehensive coverage of business, functional, UX, and technical dimensions.*
