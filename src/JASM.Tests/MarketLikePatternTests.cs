using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 锁住搜索词的语义：**只有 <c>*</c> 是通配符**，<c>%</c> <c>_</c> <c>\</c> 一律字面量，整体"包含"。
///
/// <para>
/// 这份契约是**与在线路径（PostgREST <c>title=ilike</c>）共用的**：这里的每条断言，都必须能在
/// 线上用同样一个关键词复现（在线侧靠 <c>ModMarketService.BuildQuery</c> 做等价转义）。
/// 改这里就要改那里 —— 否则降级到快照时同一关键词会给出不同结果。
/// </para>
///
/// <para>
/// 早期版本照搬了 Postgres LIKE 的元字符规则（<c>_</c> 当任意单字符），于是搜 <c>a_b</c> 会连带命中
/// <c>axb</c>。但库里标题大量来自文件名（<c>by _eldarC</c>、<c>Rain_9</c>），下划线是真字面量 ——
/// 这一组用例现在锁的就是"搜它只出那个作者"。
/// </para>
/// </summary>
public class MarketLikePatternTests
{
    [Theory]
    [InlineData("心月狐", "心月狐 by kuzan")]
    [InlineData("kuzan", "心月狐 by kuzan")]
    [InlineData("KUZAN", "心月狐 by kuzan")]
    [InlineData("月", "心月狐 by kuzan")]
    public void MatchesAsSubstring_CaseInsensitively(string pattern, string title) =>
        Assert.True(MarketLikePattern.Create(pattern).IsMatch(title));

    [Fact]
    public void DoesNotMatch_UnrelatedTitle() =>
        Assert.False(MarketLikePattern.Create("心月狐").IsMatch("哥伦比娅 by woju"));

    /// <summary>空/空白模式取「全部匹配」（上层只有 search 非空白时才加这个过滤）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyPattern_MatchesEverything(string? pattern)
    {
        Assert.True(MarketLikePattern.Create(pattern).IsMatch("任意标题"));
        Assert.True(MarketLikePattern.Create(pattern).IsMatch(string.Empty));
    }

    [Fact]
    public void NullValue_NeverMatches() =>
        Assert.False(MarketLikePattern.Create("a").IsMatch(null));

    /// <summary><c>*</c> 是唯一通配符，匹配任意长度。</summary>
    [Fact]
    public void Asterisk_MatchesAnyLength()
    {
        var like = MarketLikePattern.Create("100*");

        Assert.True(like.IsMatch("Percent 100% Wildcard"));
        Assert.True(like.IsMatch("100abc"));
        Assert.False(like.IsMatch("10"));
    }

    /// <summary>
    /// 最容易写错的一条：<c>_</c> 是**字面下划线**，不是「任意单字符」。
    /// 真实标题里的 <c>by _eldarC</c> 必须能被搜到，而 <c>by XeldarC</c> 不该被它带出来。
    /// </summary>
    [Fact]
    public void Underscore_IsLiteral_NotSingleCharacterWildcard()
    {
        var like = MarketLikePattern.Create("_eldarC");

        Assert.True(like.IsMatch("可凯露（ctrl+num+1234）by _eldarC"));
        Assert.False(like.IsMatch("by XeldarC")); // 下划线只匹配它自己
    }

    /// <summary>同理：搜 <c>a_b</c> 不该命中 <c>axb</c>。</summary>
    [Fact]
    public void Underscore_Literal_DoesNotSpanOtherCharacters()
    {
        var like = MarketLikePattern.Create("a_b");

        Assert.True(like.IsMatch("a_b underscore"));
        Assert.False(like.IsMatch("axb underscore"));
        Assert.False(like.IsMatch("ab underscore"));
    }

    /// <summary><c>%</c> 同样是字面量 —— 它只在 Postgres 的 LIKE 语法里才是通配符，对用户不该是。</summary>
    [Fact]
    public void Percent_IsLiteral()
    {
        var like = MarketLikePattern.Create("50%");

        Assert.True(like.IsMatch("折扣 50% off"));
        Assert.False(like.IsMatch("折扣 509 off")); // 不再是"任意长度"
    }

    /// <summary><c>\</c> 按字面量处理，不再吃掉后一个字符。</summary>
    [Fact]
    public void Backslash_IsLiteral()
    {
        var like = MarketLikePattern.Create(@"a\b");

        Assert.True(like.IsMatch(@"x a\b y"));
        Assert.False(like.IsMatch("x ab y")); // 反斜杠不再被当转义符吞掉
    }

    /// <summary>以反斜杠结尾的输入：字面量匹配，且不会让在线请求报错（在线侧会把它转义成 <c>\\</c>）。</summary>
    [Fact]
    public void TrailingBackslash_IsLiteral() =>
        Assert.True(MarketLikePattern.Create(@"a\").IsMatch(@"xa\y"));

    /// <summary>正则元字符必须被当字面量，否则一个 "(" 就能让匹配行为跑偏。</summary>
    [Theory]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("+")]
    [InlineData("?")]
    [InlineData("^")]
    [InlineData("$")]
    [InlineData("|")]
    [InlineData(".")]
    public void RegexMetacharacters_AreLiteral(string pattern)
    {
        var like = MarketLikePattern.Create(pattern);

        Assert.True(like.IsMatch($"x{pattern}y"));
        Assert.False(like.IsMatch("xy"));
    }

    /// <summary>NonBacktracking 的意义：这种模式在线性引擎下必须立刻返回，而不是回溯爆炸。</summary>
    [Fact]
    public void LongPatternWithManyWildcards_CompletesQuickly()
    {
        var like = MarketLikePattern.Create(new string('*', 200) + "zzz");

        Assert.False(like.IsMatch(new string('a', 5000)));
        Assert.True(like.IsMatch(new string('a', 5000) + "zzz"));
    }
}