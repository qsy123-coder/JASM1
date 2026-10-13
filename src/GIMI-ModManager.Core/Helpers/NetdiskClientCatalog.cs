namespace GIMI_ModManager.Core.Helpers;

/// <summary>JASM 认得的网盘 / 下载客户端平台。</summary>
public enum NetdiskPlatform
{
    Baidu,
    Quark,
    Thunder
}

/// <summary>
/// 一个网盘客户端的静态描述：怎么从平台名或分享链接认出它、进程叫什么、去哪儿找它的 exe。
///
/// 刻意做成**纯数据表**（不含注册表 / 文件 IO），这样「认平台」和「排候选 exe 路径」两块逻辑
/// 才能放进 <c>JASM.Tests</c> 直接测 —— 测试工程只引用 Core，碰 Win32 的东西进不去。
/// </summary>
/// <param name="Platform">平台枚举。</param>
/// <param name="DisplayName">展示名（日志里用）。</param>
/// <param name="ExeNames">主进程名，**不含 <c>.exe</c>**（<c>Process.GetProcessesByName</c> 要的就是这种形式）。</param>
/// <param name="ProtocolKeys">
/// 待查的 URL 协议键，拼成 <c>Software\Classes\&lt;key&gt;\shell\open\command</c>。可为空数组
/// （百度网盘就是空的，见 <see cref="NetdiskClientCatalog"/> 的类注释）。
/// </param>
/// <param name="NameTokens">平台名（卡片上那串，如「夸克网盘」/「baidu」）的匹配关键词，小写比较。</param>
/// <param name="UrlHosts">分享链接的 host，**精确**匹配（不是 Contains），小写。</param>
/// <param name="UninstallNameTokens">卸载项 <c>DisplayName</c> 的匹配关键词，用来找 <c>InstallLocation</c>。</param>
/// <param name="InstallDirHints">安装目录名，会在 Program Files 与各盘根下尝试。</param>
/// <param name="ExeRelativePaths">相对某个安装根目录的 exe 子路径（如迅雷的 <c>Program\Thunder.exe</c>）。</param>
public sealed record NetdiskClientSpec(
    NetdiskPlatform Platform,
    string DisplayName,
    string[] ExeNames,
    string[] ProtocolKeys,
    string[] NameTokens,
    string[] UrlHosts,
    string[] UninstallNameTokens,
    string[] InstallDirHints,
    string[] ExeRelativePaths);

/// <summary>
/// 网盘客户端目录表 + 平台识别。
///
/// <para><b>为什么百度网盘的 <see cref="NetdiskClientSpec.ProtocolKeys"/> 是空的</b>：实测百度网盘
/// 压根不注册 URL 协议 —— 既没有 <c>baidupan://</c>，也没有任何指向 <c>BaiduNetdisk.exe</c> 的
/// <c>Software\Classes\&lt;scheme&gt;</c> 键，只有一个 <c>.torrent</c> 文件关联。夸克、迅雷都有协议，
/// 所以「查协议反推 exe 路径」这条路对百度走不通，调用方必须再补一条「卸载项的 InstallLocation」。</para>
///
/// <para>平台名优先于链接 host：卡片上显示的名字是数据里直接给的，而链接可能是短链 / 跳转，
/// 名字比 host 更权威。</para>
/// </summary>
public static class NetdiskClientCatalog
{
    /// <summary>目录表。顺序即识别优先级（先匹配到的先算）。</summary>
    public static readonly IReadOnlyList<NetdiskClientSpec> All =
    [
        new NetdiskClientSpec(
            NetdiskPlatform.Baidu,
            "百度网盘",
            ExeNames: ["BaiduNetdisk"],
            ProtocolKeys: [], // 百度网盘不注册 URL 协议，只能靠卸载项 InstallLocation 找
            NameTokens: ["百度", "baidu"],
            UrlHosts: ["pan.baidu.com", "yun.baidu.com", "eyun.baidu.com"],
            UninstallNameTokens: ["百度网盘", "baidu"],
            InstallDirHints: ["百度网盘", "BaiduNetdisk"],
            ExeRelativePaths: ["BaiduNetdisk.exe", @"Program\BaiduNetdisk.exe"]),

        new NetdiskClientSpec(
            NetdiskPlatform.Quark,
            "夸克网盘",
            ExeNames: ["quark"],
            ProtocolKeys: ["qklink", "Quark", @"Applications\quark.exe"],
            NameTokens: ["夸克", "quark"],
            UrlHosts: ["pan.quark.cn", "quark.cn"],
            UninstallNameTokens: ["夸克", "quark"],
            InstallDirHints: ["Quark", "夸克网盘"],
            ExeRelativePaths: ["quark.exe"]),

        new NetdiskClientSpec(
            NetdiskPlatform.Thunder,
            "迅雷",
            ExeNames: ["Thunder"],
            ProtocolKeys: ["thunder"],
            NameTokens: ["迅雷", "thunder", "xunlei"],
            UrlHosts: ["pan.xunlei.com", "xunlei.com"],
            UninstallNameTokens: ["迅雷", "thunder", "xunlei"],
            // 迅雷装完是 <安装根>\Program\Thunder.exe，不是根目录下，见 ExeRelativePaths
            InstallDirHints: ["Thunder", "迅雷", "Xunlei"],
            ExeRelativePaths: [@"Program\Thunder.exe", "Thunder.exe"])
    ];

    /// <summary>识别平台。先看平台名、再看链接 host，都不中就返回 <c>null</c>（调用方据此只复制不启动）。</summary>
    public static NetdiskClientSpec? Detect(string? platformName, string? url)
        => FromDisplayName(platformName) ?? FromUrlHost(url);

    /// <summary>按平台名识别。命中 <see cref="NetdiskClientSpec.NameTokens"/> 里任一关键词即算（大小写不敏感）。</summary>
    public static NetdiskClientSpec? FromDisplayName(string? platformName)
    {
        if (string.IsNullOrWhiteSpace(platformName)) return null;

        var name = platformName.Trim();
        foreach (var spec in All)
        {
            foreach (var token in spec.NameTokens)
            {
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return spec;
            }
        }

        return null;
    }

    /// <summary>按链接 host 识别。只认 http/https，且 host 必须**完全等于**表里的值。</summary>
    public static NetdiskClientSpec? FromUrlHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host;
        if (host.Length == 0) return null;

        // 必须精确相等：用 Contains 的话 pan.baidu.com.evil.com 也会命中
        foreach (var spec in All)
        {
            foreach (var candidate in spec.UrlHosts)
            {
                if (host.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return spec;
            }
        }

        return null;
    }

    /// <summary>按枚举取目录表项。</summary>
    public static NetdiskClientSpec Find(NetdiskPlatform platform)
        => All.First(spec => spec.Platform == platform);
}