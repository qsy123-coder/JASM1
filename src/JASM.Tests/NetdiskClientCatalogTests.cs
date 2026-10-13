using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// 网盘平台识别的边界：名字优先于链接 host、host 必须**精确**匹配、认不出要返回 null。
///
/// 顺带把「百度网盘不注册 URL 协议」这个实测事实钉住 —— 它正是启动器必须额外走
/// 「卸载项 InstallLocation」那条路的原因；一旦有人删掉那条路，这里会红。
/// </summary>
public class NetdiskClientCatalogTests
{
    [Theory]
    [InlineData("夸克网盘", "https://pan.quark.cn/s/abc", NetdiskPlatform.Quark)]
    [InlineData("百度网盘", "https://pan.baidu.com/s/abc", NetdiskPlatform.Baidu)]
    [InlineData("baidu", "https://pan.baidu.com/s/abc", NetdiskPlatform.Baidu)] // 数据里真出现过小写裸名
    [InlineData("迅雷", "https://pan.xunlei.com/s/abc", NetdiskPlatform.Thunder)]
    public void DetectRecognizesTheKnownPlatforms(string name, string url, NetdiskPlatform expected)
        => Assert.Equal(expected, NetdiskClientCatalog.Detect(name, url)?.Platform);

    [Fact]
    public void DetectFallsBackToTheUrlHostWhenTheNameIsUnusable()
        => Assert.Equal(NetdiskPlatform.Quark,
            NetdiskClientCatalog.Detect(null, "https://pan.quark.cn/s/abc")?.Platform);

    [Fact]
    public void DetectTakesTheNameOverTheUrlHost()
    {
        // 名字是数据里直接给的、卡片上显示的就是它，比可能被短链/跳转改过的链接可靠
        Assert.Equal(NetdiskPlatform.Baidu,
            NetdiskClientCatalog.Detect("baidu", "https://pan.quark.cn/s/abc")?.Platform);
    }

    [Fact]
    public void DetectIsCaseInsensitiveOnTheName()
        => Assert.Equal(NetdiskPlatform.Baidu,
            NetdiskClientCatalog.Detect("BAIDU", "https://example.com/x")?.Platform);

    [Theory]
    [InlineData("未知网盘", "https://example.com/x")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(null, "not a url")]
    public void DetectReturnsNullWhenNothingMatches(string? name, string? url)
        => Assert.Null(NetdiskClientCatalog.Detect(name, url));

    [Fact]
    public void DetectRequiresAnExactHostMatch()
    {
        // 若用 Contains 匹配 host，这种后缀攻击的域名也会命中
        Assert.Null(NetdiskClientCatalog.Detect(null, "https://pan.baidu.com.evil.com/s/abc"));
    }

    [Fact]
    public void QuarkAndThunderRegisterUrlProtocolsButBaiduDoesNot()
    {
        // 百度网盘压根没有 baidupan:// 这类协议，只能靠卸载项的 InstallLocation 找它的 exe
        Assert.Empty(NetdiskClientCatalog.Find(NetdiskPlatform.Baidu).ProtocolKeys);
        Assert.NotEmpty(NetdiskClientCatalog.Find(NetdiskPlatform.Quark).ProtocolKeys);
        Assert.NotEmpty(NetdiskClientCatalog.Find(NetdiskPlatform.Thunder).ProtocolKeys);
    }
}