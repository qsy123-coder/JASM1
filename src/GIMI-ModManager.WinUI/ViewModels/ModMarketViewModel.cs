using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.Notifications;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

public partial class ModMarketViewModel : ObservableRecipient, INavigationAware
{
    private readonly ILogger _logger;
    private readonly ModMarketService _modMarketService;
    private readonly NotificationManager _notificationManager;
    private readonly INavigationService _navigationService;

    // ─── Sidebar: Characters from Supabase ─────────────────────

    public ObservableCollection<ModMarketCategory> Categories { get; } = [];

    [ObservableProperty]
    private ModMarketCategory? _selectedCategory;

    // ─── Mod Card Grid ─────────────────────────────────────────

    public ObservableCollection<ModMarketMod> Mods { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>首次加载/重新加载(列表为空时),指示器显示在内容区顶部</summary>
    [ObservableProperty]
    private bool _isInitialLoading;

    /// <summary>滚动加载更多,指示器显示在卡片流末尾</summary>
    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 空状态那一屏显示什么。成功但 0 条是「没有找到 Mod」,加载失败是错误原因 ——
    /// 之前失败时只写了 StatusMessage,而那个文案只在 IsInitialLoading 期间可见,
    /// 于是整片区域全空、连重试按钮都没有。失败的可见性必须落在这一个属性上。
    /// </summary>
    [ObservableProperty]
    private string _emptyStateMessage = "没有找到 Mod";

    /// <summary>true = 当前这批数据来自 COS 兜底快照(Supabase 网关不可用)。</summary>
    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private string _degradedMessage = string.Empty;

    // ─── Filters / Sort ────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategoryFilter = "全部分类";

    [ObservableProperty]
    private string _selectedContentFilter = "显示 NSFW";

    /// <summary>默认按「最新」排 —— 市场里的东西更新快，进来先看到刚发的（下拉里仍可切回「默认」）。</summary>
    [ObservableProperty]
    private string _selectedSortOption = "最新";

    public IReadOnlyList<string> CategoryFilterOptions { get; } =
        ["全部分类", "仅Mods", "仅NSFW", "仅非NSFW", "含直链下载"];
    public IReadOnlyList<string> ContentFilterOptions { get; } = ["显示 NSFW", "模糊 NSFW", "隐藏 NSFW"];
    public IReadOnlyList<string> SortOptions { get; } = ["默认", "最新", "最近更新", "最多点赞", "最多浏览"];

    // ─── Pagination ────────────────────────────────────────────

    private int _currentPage = 1;
    private const int PageSize = 24;

    [ObservableProperty]
    private bool _hasMorePages = true;

    [ObservableProperty]
    private ModMarketMod? _selectedMod;

    [RelayCommand]
    private void OpenDownloadManager()
    {
        _notificationManager.ShowNotification("下载管理",
            "下载管理功能即将推出，敬请期待。",
            TimeSpan.FromSeconds(4));
    }

    public ModMarketViewModel(ILogger logger, ModMarketService modMarketService,
        NotificationManager notificationManager, INavigationService navigationService)
    {
        _logger = logger.ForContext<ModMarketViewModel>();
        _modMarketService = modMarketService;
        _notificationManager = notificationManager;
        _navigationService = navigationService;
    }

    // ─── Navigation ────────────────────────────────────────────

    public async void OnNavigatedTo(object parameter)
    {
        IsInitialLoading = true;
        IsLoading = true;
        StatusMessage = "正在加载...";

        try
        {
            var categories = await _modMarketService.GetCharacterCategoriesAsync();
            Categories.Clear();
            foreach (var c in categories)
                Categories.Add(c);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load categories");
            StatusMessage = $"加载分类失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsInitialLoading = false;
        }

        // Select "全部" after loading and IsLoading is false
        if (Categories.Count > 0)
            SelectedCategory = Categories[0];
    }

    public void OnNavigatedFrom() { }

    // ─── Property Changes ─────────────────────────────────────

    // 换分类走 RequestReload,与下面三个筛选器同一个入口 —— **不能**直接 _ = ReloadModsAsync()。
    // 直接调的话,加载中点另一个分类会被 LoadModsAsync 开头的 IsLoading 守卫整个吞掉:
    // 请求根本不发,而列表已经被清空,上一次(旧分类)的结果回来就填了进去。
    partial void OnSelectedCategoryChanged(ModMarketCategory? value) => RequestReload();

    /// <summary>
    /// 输入**不**触发搜索 —— 搜索由回车键与工具栏的「搜索」按钮触发，见 <see cref="Search"/>。
    /// 这里只剩一条职责：**清空即恢复全部**。
    ///
    /// <para>
    /// 判据是"值变空"而不是"点了文本框自带的 ✕"：全选删除与点 ✕ 对用户是同一件事，
    /// 没必要去分辨那次点击。留着它是因为"清空了却还要再按一次回车才回到全部"太容易漏。
    /// </para>
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
            RequestReload();
    }

    private bool _reloadPending;

    /// <summary>
    /// 加载「代」号,只增不减,只当令牌用 —— 数值本身没有意义。
    ///
    /// <para>
    /// 加载是异步的,await 回来时这次结果可能已经过期(用户在等待期间换了分类 / 筛选)。
    /// <see cref="LoadModsAsync"/> 在开头记下当代号、await 之后对一次,不等就把结果丢掉。
    /// </para>
    /// </summary>
    private int _loadGeneration;

    partial void OnSelectedCategoryFilterChanged(string value) => RequestReload();
    partial void OnSelectedContentFilterChanged(string value) => RequestReload();
    partial void OnSelectedSortOptionChanged(string value) => RequestReload();

    /// <summary>
    /// 「换分类 / 换筛选」后的重载入口。
    ///
    /// <para>
    /// 两件事缺一不可:① 记下"稍后还要重载一次"(在飞的那次挡不住后发的请求);
    /// ② **当场作废在飞的那次结果** —— 只做 ① 是不够的,那次请求回来时列表已被清空,
    /// 它会把自己的内容写进去,于是出现「侧边栏选着爱弥斯、列表里却是爱弥斯的机甲」;
    /// 更糟的是紧接着的「加载更多」会按**新**分类查下一页、追加到这批旧内容上,
    /// 两个分类就真的混在同一屏里了(实机反馈的"来回切换会互相混入"就是这条)。
    /// </para>
    /// </summary>
    private void RequestReload()
    {
        _loadGeneration++;
        if (IsLoading) { _reloadPending = true; return; }
        _ = ReloadModsAsync();
    }

    // ─── Commands ──────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        await LoadModsAsync(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadModsAsync();

    /// <summary>
    /// 执行搜索。由回车键与工具栏的「搜索」按钮触发 —— 刻意**不做输入即搜**：
    /// 每敲一个字就重载一次，既会把用户正在看的列表反复清空、也会白白拉一串请求
    /// （该出口配额超限正是市场降级到离线快照的常见原因）。
    /// </summary>
    /// <remarks>
    /// 必须走 <see cref="RequestReload"/>，**不能**直接 <c>_ = ReloadModsAsync()</c> ——
    /// 直接调会在 <see cref="LoadModsAsync"/> 开头的 IsLoading 守卫处被整个吞掉：列表已被清空、
    /// 请求却没发出去、在飞的旧结果又因过期被丢弃，页面就卡在一片空白上且无法自愈。
    /// 这正是实机反馈的「明明打字搜索了但是列表是空的」，也是分类入口早先踩过的同一个坑。
    /// </remarks>
    [RelayCommand]
    private void Search() => RequestReload();

    [RelayCommand]
    private void OpenModDetail(ModMarketMod? mod)
    {
        SelectedMod = mod;
    }

    [RelayCommand]
    private void CloseDetailPanel()
    {
        SelectedMod = null;
    }

    /// <summary>
    /// 打开「每日更新」页（<see cref="DailyUpdatesViewModel"/>）—— 最近 7 天有更新的 mod，按天分组。
    ///
    /// 早先这里是**一跳外链**（<c>Launcher.LaunchUriAsync</c> 打开 <c>wave-mod.top/updates</c>）：
    /// 用户被踢出应用、看完还得自己切回来，站点的可用性还成了这个功能的单点依赖。
    /// 现在整页搬进应用内 —— 那批数据本来就来自同一张 Supabase 表（见 ModMarketService），
    /// 跳出去只是把已经拿得到的东西交浏览器再渲染一遍。
    /// </summary>
    [RelayCommand]
    private void OpenDailyUpdates()
    {
        _navigationService.NavigateTo(typeof(DailyUpdatesViewModel).FullName!);
    }

    // ─── Data Loading ──────────────────────────────────────────

    /// <summary>
    /// 从第一页重来。开新的一代(见 <see cref="_loadGeneration"/>)—— 这次重载之前的任何
    /// 在飞请求,回来时都对不上号,会被丢掉。
    /// </summary>
    private async Task ReloadModsAsync()
    {
        _loadGeneration++;
        _currentPage = 1;
        Mods.Clear();
        // 横幅描述的是「当前这一屏数据」。数据都清了,横幅也必须跟着走,
        // 否则会出现「快照模式下加载失败 → 空白页 + 快照横幅」这种自相矛盾的画面。
        IsDegraded = false;
        DegradedMessage = string.Empty;
        await LoadModsAsync(false);
    }

    private async Task LoadModsAsync(bool append)
    {
        if (IsLoading)
        {
            return;
        }
        IsLoading = true;
        // 首次/重载 vs 加载更多,分别控制不同位置的指示器
        IsInitialLoading = !append;
        IsLoadingMore = append;

        // Only increment page when appending (load-more). Must be done after
        // the IsLoading guard to prevent races from LayoutUpdated / ViewChanged.
        if (append) _currentPage++;

        // 这次加载属于哪一代。重载会在开始前换代(见 ReloadModsAsync),加载更多则留在当代 ——
        // 所以这里直接取当前值即可,不用自己增。await 回来时对一下,不等就说明结果过期了。
        var generation = _loadGeneration;

        StatusMessage = "正在加载...";

        try
        {
            // Map filter options to service parameters
            var contentFilter = SelectedContentFilter switch
            {
                "隐藏 NSFW" => "SFW",
                "模糊 NSFW" => "Blur",
                _ => "All"
            };
            var sortBy = SelectedSortOption switch
            {
                "最近更新" => "Newest",
                "最新" => "Newest",
                "最多点赞" => "Most Liked",
                "最多浏览" => "Most Viewed",
                _ => "Default"
            };
            var categoryFilter = SelectedCategoryFilter == "仅Mods";

            // Category-level NSFW / direct-download filters
            // nsfwOnly takes precedence over contentFilter to avoid contradictory nsfw params
            bool? nsfwOnly = SelectedCategoryFilter switch
            {
                "仅NSFW" => true,
                "仅非NSFW" => false,
                _ => null
            };
            var directDownloadOnly = SelectedCategoryFilter == "含直链下载";

            // "最近更新": 只显示 3 天内更新的内容
            DateTime? updatedAfter = SelectedSortOption == "最近更新"
                ? DateTime.UtcNow.AddDays(-3) : null;

            var result = await _modMarketService.GetModsAsync(
                character: SelectedCategory?.Key,
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                contentFilter: contentFilter,
                sortBy: sortBy,
                modsOnly: categoryFilter,
                nsfwOnly: nsfwOnly,
                directDownloadOnly: directDownloadOnly,
                updatedAfter: updatedAfter,
                page: _currentPage,
                pageSize: PageSize);

            // 过期结果一律丢弃(用户在等待期间换了分类 / 筛选)。
            // 不丢的话:列表已被那次重载清空,这批旧内容会被写进去(侧边栏选着爱弥斯、内容却是
            // 爱弥斯的机甲),随后一次「加载更多」还会按**新**分类查下一页追加到它上面 ——
            // 两个分类就混在同一屏里了。
            if (generation != _loadGeneration)
            {
                _logger.Debug("丢弃过期的加载结果(第 {Stale} 代,当前第 {Current} 代,append={Append})",
                    generation, _loadGeneration, append);
                return;
            }

            var mods = result.Mods;
            var total = result.TotalCount;

            // 服务层把异常全收敛成 ErrorMessage 返回(它自己不抛),所以「失败」在这里而不在 catch 里。
            // 必须显式判一次:否则失败会被当成「成功但 0 条」,界面显示"没有找到 Mod",真相被盖掉。
            if (result.ErrorMessage is { Length: > 0 } error)
            {
                ShowLoadFailure(error, append);
                return;
            }

            if (!append) Mods.Clear();
            foreach (var m in mods)
            {
                m.IsNsfwBlurred = m.Nsfw && SelectedContentFilter == "模糊 NSFW";
                Mods.Add(m);
            }

            // When Content-Range is available (total != returned count),
            // trust it: there are more pages as long as we haven't
            // accumulated everything. When Content-Range is missing
            // (total == returned count), guess by page fullness.
            HasMorePages = total == mods.Count
                ? mods.Count >= PageSize
                : Mods.Count < total;

            IsEmpty = Mods.Count == 0;
            StatusMessage = IsEmpty ? "没有找到 Mod" : string.Empty;
            EmptyStateMessage = IsEmpty ? "没有找到 Mod" : string.Empty;

            // 快照降级是「能用」而不是「完美」—— 用横幅说明数据来源与快照时间,
            // 别让用户以为几百条 mod 在一夜之间全被删了。
            IsDegraded = result.IsFromSnapshot;
            DegradedMessage = BuildDegradedMessage(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load mods");
            ShowLoadFailure($"加载失败：{ex.Message}", append);
        }
        finally
        {
            IsLoading = false;
            IsInitialLoading = false;
            IsLoadingMore = false;

            if (_reloadPending)
            {
                _reloadPending = false;
                _ = ReloadModsAsync();
            }
        }
    }

    /// <summary>
    /// 失败必须看得见。列表为空时把原因写进空状态区(那一屏带重试按钮);
    /// 已经有卡片时(「加载更多」失败)空状态区是收起的,只能靠通知,
    /// 但两条路都不能静默 —— 这正是这次事故里表现成「整片空白」的根因。
    /// </summary>
    private void ShowLoadFailure(string message, bool append)
    {
        _logger.Warning("Mod 市场加载失败:{Message}", message);

        StatusMessage = message;
        EmptyStateMessage = message;
        IsEmpty = Mods.Count == 0;

        // 这一页已经失败了,别让滚动事件反复重试同一页(那只会刷满日志)。
        // 重试按钮/切分类都会走重载,HasMorePages 会按新结果重算,不会卡死。
        if (append) HasMorePages = false;

        if (Mods.Count > 0)
            _notificationManager.ShowNotification("Mod 市场", message, TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// 降级横幅的正文。时刻取快照的 Last-Modified 并换算到本地时区 ——
    /// 用户关心的是"这堆数据有多旧",不是 UTC。
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
