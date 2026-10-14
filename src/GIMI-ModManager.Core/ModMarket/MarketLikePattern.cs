using System.Text;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.ModMarket;

/// <summary>
/// 「搜索词 → 是否匹配某个标题」的本地实现，与在线路径（PostgREST <c>title=ilike</c>）**刻意同口径**。
///
/// <para>
/// 语义（用户视角）：搜索词里**只有 <c>*</c> 是通配符**，<c>%</c> <c>_</c> <c>\</c> 都按字面量匹配，
/// 且整体是"包含"语义（不锚定）。
/// </para>
///
/// <para>
/// 为什么是这套语义：早期版本照搬了 Postgres LIKE 的元字符规则，于是搜 <c>a_b</c> 会连带匹配
/// <c>axb</c> —— 而库里的标题大量来自文件名（<c>by _eldarC</c>、<c>Rain_9</c>），下划线是**真字面量**，
/// 用户搜它就该只出那个作者。修正后两条路径都把它当字面量。
/// </para>
///
/// <para>
/// ⚠️ <c>*</c> 是唯一的例外，而且是**被迫**的：PostgREST 会把过滤值里的所有 <c>*</c> 无条件换成
/// <c>%</c>（实测 <c>%*%</c> 命中全部），所以**在线路径无法表达字面星号**。为了让两条路径结果集一致，
/// 本地也让它保持通配。<c>*</c> 是唯一一个"用户想搜字面量但搜不了"的字符 ——
/// 库里含 <c>*</c> 的标题为 0 条，实际影响可以忽略，但这是既有事实，不要以为漏了。
/// </para>
///
/// <para>
/// 在线侧的对应实现见 <c>ModMarketService.BuildQuery</c>：它把用户输入按同样的规则转义
/// （<c>\</c>→<c>\\</c>、<c>%</c>→<c>\%</c>、<c>_</c>→<c>\_</c>，<c>*</c> 不转义）再交给 Postgres。
/// **改这里必须同时改那里**，否则降级到快照时同一关键词会给出不同结果。
/// </para>
/// </summary>
public sealed class MarketLikePattern
{
    private static readonly RegexOptions PatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline |
        RegexOptions.NonBacktracking;

    /// <summary>空模式按"匹配一切"处理（只有 search 非空白时上层才会加这个过滤，空模式线上不会出现）。</summary>
    private static readonly MarketLikePattern MatchEverything = new(new Regex(string.Empty, PatternOptions));

    private readonly Regex _regex;

    private MarketLikePattern(Regex regex)
    {
        _regex = regex;
    }

    public static MarketLikePattern Create(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return MatchEverything;
        return new MarketLikePattern(new Regex(Translate(pattern), PatternOptions));
    }

    public bool IsMatch(string? value) => value is not null && _regex.IsMatch(value);

    /// <summary>
    /// 把搜索词翻译成正则表达式：<c>*</c> 展开成 <c>.*</c>，其余字符一律字面量。
    /// 用 NonBacktracking 保证线性时间 —— 用户输入的搜索词不该有机会造成灾难性回溯。
    /// </summary>
    private static string Translate(string pattern)
    {
        var builder = new StringBuilder(pattern.Length * 2);

        foreach (var c in pattern)
            builder.Append(c == '*' ? ".*" : Regex.Escape(c.ToString()));

        return builder.ToString();
    }
}