using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 「每日更新」页 —— 最近 N 天新增的 mod，按天分组。
///
/// <para>
/// 分组口径**逐条对齐 wave-mod 站点**（<c>src/lib/mods-domain/daily.ts</c>）：
/// 按 <c>created_at</c> 换算到 <b>Asia/Shanghai</b> 的日历日归组，**绝不用 <c>updated_at</c>**，
/// 并且把窗口内的空天也列出来。三条都是站点明写在注释里的规则 —— 跟着它走，用户在应用里
/// 看到的日期才能和站点上看到的一致，而"对得上"正是这一页唯一的价值。
/// </para>
///
/// <para>
/// 数据仍走与 Mod 市场同一条 Supabase 通道（<see cref="ModMarketService"/>），只把查询换成
/// 「按创建时间倒序取最近一批」。好处是市场那一套离线快照降级在这里自动就有，而且**降级不需要
/// 任何口径让步** —— 快照行同样带 <c>created_at</c>，分组逻辑原样复用（站点自己的兜底路径也是复用同一套）。
/// </para>
///
/// <para>
/// 与市场页的一处结构差异：这里每次加载都**整体重建**分组（窗口一次拉完，不分页），所以对外只给
/// 一个 <see cref="GroupsRebuilt"/> 事件，而不是可观察集合 —— 否则「清空 + 逐条加」会在页面上
/// 触发 N+1 次重排，每次都要重建整棵卡片树。
/// </para>
/// </summary>
public partial class DailyUpdatesViewModel : ObservableRecipient, INavigationAware
{
    /// <summary>窗口天数（含今天）。站点的 <c>?days=</c> 默认 14、允许 1~30，这里取 7。</summary>
    private const int WindowDays = 7;

    /// <summary>
    /// 一次请求的条数上限。1000 就是 Supabase(PostgREST) 的 <c>db-max-rows</c> 硬上限，写再大也会被静默夹到 1000。
    /// 按库里每日 5~15 条的量，7 天窗口远够不着它；留着是为了万一某天批量导入暴涨时**明说是截断过的**，
    /// 而不是静默少给一截。
    /// </summary>
    private const int MaxItems = 1000;

    /// <summary>
    /// 上海时区相对 UTC 的固定偏移。中国没有夏令时，用固定偏移比查时区库更准也更省 ——
    /// 站点那份 <c>daily.ts</c> 用的是同一个理由（「中国无夏令时，按固定 24h 递减即可」）。
    /// </summary>
    private const int ShanghaiUtcOffsetHours = 8;

    private readonly ILogger _logger;
    private readonly ModMarketService _modMarketService;

    /// <summary>按上海日历日倒序的分节，**含窗口内的空天**。整体替换，不做增量。</summary>
    public IReadOnlyList<DailyUpdateGroup> Groups { get; private set; } = [];

    /// <summary>分组重建完成。每次加载只发一次，页面据此重建卡片树。</summary>
    public event EventHandler? GroupsRebuilt;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 空状态那一屏显示什么。成功但 0 条是「最近 N 天没有新的更新」，加载失败是错误原因 ——
    /// 与市场页同一个教训：失败时若只写 StatusMessage，而它只在加载期间可见，用户看到的就是一片空白。
    /// </summary>
    [ObservableProperty]
    private string _emptyStateMessage = "最近 7 天没有新的更新";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>true = 当前这批数据来自 COS 兜底快照（Supabase 网关不可用）。</summary>
    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private string _degradedMessage = string.Empty;

    /// <summary>true = 窗口内的条目超过 <see cref="MaxItems"/> 条，只显示了最新的一批。</summary>
    [ObservableProperty]
    private bool _isTruncated;

    [ObservableProperty]
    private string _truncatedMessage = string.Empty;

    [ObservableProperty]
    private ModMarketMod? _selectedMod;

    public DailyUpdatesViewModel(ILogger logger, ModMarketService modMarketService)
    {
        _logger = logger.ForContext<DailyUpdatesViewModel>();
        _modMarketService = modMarketService;
    }

    // ─── Navigation ────────────────────────────────────────────

    public void OnNavigatedTo(object parameter) => _ = LoadAsync();

    public void OnNavigatedFrom() { }

    // ─── Commands ──────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private void OpenModDetail(ModMarketMod? mod) => SelectedMod = mod;

    [RelayCommand]
    private void CloseDetailPanel() => SelectedMod = null;

    // ─── Data Loading ──────────────────────────────────────────

    private async Task LoadAsync()
    {
        // 重进页面 / 连点刷新都只会有一发在飞：这一页每次都是整体重建，并发跑等于白拉一遍。
        if (IsLoading) return;

        IsLoading = true;
        StatusMessage = "正在加载...";

        // 横幅描述的是「当前这一屏数据」。数据要整体换掉了，横幅必须跟着走，
        // 否则会出现「已经拿到在线数据、头顶还挂着离线快照横幅」这种自相矛盾的画面。
        IsDegraded = false;
        DegradedMessage = string.Empty;
        IsTruncated = false;
        TruncatedMessage = string.Empty;

        try
        {
            var today = ShanghaiToday();
            var windowStart = today.AddDays(-(WindowDays - 1));

            // 窗口下界：上海窗口首日的零点，换算成绝对时刻交给服务层去比 created_at。
            // 服务层会把它截断到 UTC 日（只会更早），所以漏不掉窗口内的任何一条 ——
            // 多捞出来的那部分在下面按上海日精确收口。
            var sinceUtc = DateTime.SpecifyKind(
                windowStart.ToDateTime(TimeOnly.MinValue).AddHours(-ShanghaiUtcOffsetHours),
                DateTimeKind.Utc);

            var result = await _modMarketService.GetModsAsync(
                // Newest = order by created_at desc；降级路径的本地排序键同样是 created_at desc，
                // 两条路径口径一致，快照模式下日期不会错位。
                sortBy: "Newest",
                updatedAfter: sinceUtc,
                page: 1,
                pageSize: MaxItems);

            // 服务层把异常全收敛成 ErrorMessage 返回（它自己不抛），所以「失败」在这里而不在 catch 里。
            // 必须显式判一次：否则失败会被当成「成功但 0 条」，界面显示"最近 7 天没有新的更新"，真相被盖掉。
            if (result.ErrorMessage is { Length: > 0 } error)
            {
                ShowLoadFailure(error);
                return;
            }

            RebuildGroups(result.Mods, today, windowStart);

            IsDegraded = result.IsFromSnapshot;
            DegradedMessage = BuildDegradedMessage(result);

            // 撞到请求上限就明说。Content-Range 拿不到时会退化成 int.MaxValue 哨兵（见服务层），
            // 那也走这条分支 —— 拿不准就按"还有更多"提示，比默默少给一截强。
            IsTruncated = result.TotalCount > MaxItems;
            TruncatedMessage = IsTruncated
                ? $"最近 {WindowDays} 天的更新超过 {MaxItems} 条，本次只显示最新的 {MaxItems} 条。"
                : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "每日更新页加载失败");
            ShowLoadFailure($"加载失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 按上海日历日分节。分组在客户端做 —— 线上没有"按天聚合"这种查询，服务端只按
    /// <c>created_at</c> 倒序给一批，切天是纯粹的表现层职责（站点也是这么做的）。
    /// </summary>
    private void RebuildGroups(IReadOnlyList<ModMarketMod> mods, DateOnly today, DateOnly windowStart)
    {
        var windowKeys = new HashSet<DateOnly>();
        for (var d = today; d >= windowStart; d = d.AddDays(-1))
            windowKeys.Add(d);

        var byDate = new Dictionary<DateOnly, List<ModMarketMod>>();
        foreach (var mod in mods)
        {
            var key = ShanghaiDate(mod.CreatedAt);
            if (!windowKeys.Contains(key)) continue; // 服务层多捞的那部分在这里收口
            if (!byDate.TryGetValue(key, out var bucket)) byDate[key] = bucket = [];
            bucket.Add(mod);
        }

        // 整个窗口一条都没有：交给空状态那一屏，不铺一摞空标题。
        if (byDate.Count == 0)
        {
            Groups = [];
            IsEmpty = true;
            StatusMessage = string.Empty;
            EmptyStateMessage = $"最近 {WindowDays} 天没有新的更新";
            GroupsRebuilt?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 含空天、从今天倒序 —— 与站点一致（那边靠空天渲染日期胶囊）。
        // 这也是这一页与市场页最不一样的地方：一句「今天还没更新」比一屏空白有用。
        var groups = new List<DailyUpdateGroup>();
        for (var d = today; d >= windowStart; d = d.AddDays(-1))
            groups.Add(new DailyUpdateGroup(d, byDate.TryGetValue(d, out var bucket) ? bucket : [], today));

        Groups = groups;
        IsEmpty = false;
        StatusMessage = string.Empty;
        EmptyStateMessage = string.Empty;

        GroupsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>今天的上海日历日。</summary>
    private static DateOnly ShanghaiToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(ShanghaiUtcOffsetHours));

    /// <summary>
    /// 一条 mod 落在上海日历的哪一天。日期一律按上海日算（与站点一致），**不按本机时区** ——
    /// 否则同一批 mod 在不同机器上会落到不同的天，这一页的日期又跟站点对不上了。
    /// </summary>
    private static DateOnly ShanghaiDate(DateTime createdAt)
    {
        var utc = createdAt.Kind switch
        {
            DateTimeKind.Utc => createdAt,
            DateTimeKind.Local => createdAt.ToUniversalTime(),
            // 接口给的是带 Z 的 UTC 时间戳；万一 Kind 在反序列化里丢了，按 UTC 解 ——
            // 按本地解会让日期整体挪一天，那是这一页最不能出的错。
            _ => DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
        };

        return DateOnly.FromDateTime(utc.AddHours(ShanghaiUtcOffsetHours));
    }

    /// <summary>
    /// 失败必须看得见。与市场页同样的理由：清掉旧分组、把原因写进空状态那一屏（那一屏带重试按钮），
    /// 而不是留着一堆旧卡片、只在角落里刷一行字。
    /// </summary>
    private void ShowLoadFailure(string message)
    {
        _logger.Warning("每日更新页加载失败：{Message}", message);

        Groups = [];
        IsEmpty = true;
        StatusMessage = message;
        EmptyStateMessage = message;

        GroupsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 降级横幅的正文。这里**不需要**任何"口径已变"的说明 —— 快照行同样带 <c>created_at</c>，
    /// 分组与排序与在线完全一致（站点的兜底路径复用的也是同一套分组）。只需要讲清数据有多旧。
    ///
    /// <para>时刻取快照的 Last-Modified 并换算到本地时区 —— 用户关心的是"这堆数据有多旧"，不是 UTC。</para>
    /// </summary>
    private static string BuildDegradedMessage(ModMarketResult result)
    {
        if (!result.IsFromSnapshot) return string.Empty;

        var when = result.SnapshotGeneratedAt is { } generatedAt
            ? generatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未知时间";

        return $"Supabase 暂不可用，当前显示 {when} 的数据快照";
    }
}