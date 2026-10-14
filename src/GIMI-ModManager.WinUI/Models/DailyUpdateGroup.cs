namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 「每日更新」页里的一个日期节：一天 + 那天的更新条目。
///
/// <para>
/// 分组口径**照抄 wave-mod 站点**（其 <c>src/lib/mods-domain/daily.ts</c> 开头那段规则）：
/// 按 mod 的 <c>created_at</c> 换算到 <b>Asia/Shanghai</b> 后的日历日归组，**不用 <c>updated_at</c>**。
/// </para>
///
/// <para>
/// 为什么不能用 updated_at：下架再上线、或只是被人改了标题 / 补了网盘链接，都只动 <c>updated_at</c>，
/// <c>created_at</c> 不变。按 updated_at 分组会让几个月前的老 mod 冒充「今天的新更新」，
/// 也让这一页的日期跟站点上看到的对不上 —— 而"对得上"正是这一页唯一的价值。
/// </para>
///
/// <para>
/// 标题格式也照站点（其 <c>fullLabel</c>）：<c>2026年10月14日（今天）</c>。
/// 在构造时算好，**不留给绑定现拼** —— 「今天 / 昨天」是相对当下而言的，一进页面就该定死。
/// </para>
/// </summary>
public sealed class DailyUpdateGroup
{
    public DailyUpdateGroup(DateOnly date, IReadOnlyList<ModMarketMod> mods, DateOnly today)
    {
        Date = date;
        Mods = mods;
        Title = BuildTitle(date, today);
        ShortLabel = BuildShortLabel(date, today);
    }

    /// <summary>上海日历下的那一天。既是分组依据，也是排序键（新的在前）。</summary>
    public DateOnly Date { get; }

    /// <summary>节标题，如「2026年10月14日（今天）」「2026年10月12日」。</summary>
    public string Title { get; }

    /// <summary>左侧日期导航里的短标签：今天 / 昨天 / M月D日（站点 <c>shortLabel</c> 同款）。</summary>
    public string ShortLabel { get; }

    /// <summary>导航里跟在短标签后面的条数，如 <c>+8</c>（站点是 <c>+{count}</c>）。</summary>
    public string NavCount => $"+{Mods.Count}";

    /// <summary>本节的条目。顺序沿用服务端返回的顺序（按 created_at 倒序），这里不再重排。</summary>
    public IReadOnlyList<ModMarketMod> Mods { get; }

    /// <summary>这一天没有任何更新。窗口内的空天照样要显示 —— 「今天还没更新」本身就是信息。</summary>
    public bool IsEmpty => Mods.Count == 0;

    private static string BuildTitle(DateOnly date, DateOnly today)
    {
        var label = $"{date.Year}年{date.Month}月{date.Day}日";

        if (date == today) return $"{label}（今天）";
        if (date == today.AddDays(-1)) return $"{label}（昨天）";

        return label;
    }

    /// <summary>
    /// 导航里的短标签。与标题**刻意不同**：导航是一条条竖排的窄按钮，塞不下「2026年10月14日」，
    /// 而「今天 / 昨天」正是用户扫这一列时最先找的两个词（站点也是这个分工）。
    /// </summary>
    private static string BuildShortLabel(DateOnly date, DateOnly today)
    {
        if (date == today) return "今天";
        if (date == today.AddDays(-1)) return "昨天";
        return $"{date.Month}月{date.Day}日";
    }
}