namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「点网盘卡片 → 把对应客户端拉起来」里与 Win32 无关的那一半：
/// 解析注册表里的 shell 命令串、按优先级排出候选 exe 路径。
///
/// 真正读注册表 / 起进程在 <c>NetdiskClientLauncher</c>（WinUI 侧），这里只做纯字符串与路径拼接，
/// 好处是能被 <c>JASM.Tests</c> 直接覆盖 —— 测试工程不引用 WinUI。
/// </summary>
public static class NetdiskLaunchPlanner
{
    private const string ExeSuffix = ".exe";

    /// <summary>
    /// <c>open\command</c> 里那些「看着像启动器、其实只是个系统宿主」的 exe。
    /// 这类命令的载荷在参数里（<c>rundll32.exe x.dll,Foo</c>），把宿主 exe 当目标启动毫无意义。
    /// </summary>
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe",
        "explorer.exe",
        "cmd.exe",
        "msiexec.exe",
        "mshta.exe",
        "wscript.exe",
        "cscript.exe"
    };

    /// <summary>
    /// 从注册表 <c>open\command</c> 串里剥出 exe 全路径。**拿不准一律返回 <c>null</c>**
    /// （调用方顺延下一个候选，而不是拿着可疑路径去启动）。
    ///
    /// 规则，按顺序：
    /// <list type="number">
    /// <item>空 / 空白 → null。</item>
    /// <item>以 <c>"</c> 开头 → 取到下一个 <c>"</c>；没有闭合引号（畸形）→ null。</item>
    /// <item>否则取第一个空白前的一段（覆盖 <c>D:\Q\quark.exe "%1"</c> 这种裸路径形态；
    /// 带空格的裸路径本身就歧义，shell 会加引号，所以按空白切是安全的）。</item>
    /// <item>后缀必须是 <c>.exe</c> —— 挡掉 <c>*.dll</c> 之类。</item>
    /// <item>文件名命中 <see cref="BlockedHosts"/> → null。</item>
    /// <item>必须是绝对路径，或以 <c>%VAR%</c> 开头（注册表里常见 <c>"%ProgramFiles%\Foo\app.exe"</c> 这种写法，
    /// <c>Path.IsPathRooted</c> 认不出来但在展开后就是绝对路径）—— 纯裸名要靠 shell / 当前目录解析，
    /// 无法验证，宁可回退到「按已知安装目录探测」。</item>
    /// <item><b>原样返回</b>，不展开 <c>%...%</c>：展开交给调用方（那里才知道要不要过 <c>File.Exists</c>），本函数保持确定、可测。</item>
    /// </list>
    /// </summary>
    public static string? ParseExecutablePath(string? openCommand)
    {
        if (string.IsNullOrWhiteSpace(openCommand)) return null;

        var text = openCommand.Trim();

        string token;
        if (text.StartsWith('"'))
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote < 0) return null; // 引号没闭合 → 畸形
            token = text[1..closingQuote];
        }
        else
        {
            var separator = text.IndexOfAny([' ', '\t']);
            token = separator < 0 ? text : text[..separator];
        }

        token = token.Trim();
        if (token.Length == 0) return null;

        if (!token.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        var fileName = Path.GetFileName(token);
        if (string.IsNullOrEmpty(fileName)) return null;
        if (BlockedHosts.Contains(fileName)) return null;

        // %VAR% 开头的路径展开后就是绝对路径，Path.IsPathRooted 认不出来但不该拒
        if (!Path.IsPathRooted(token) && !token.StartsWith('%')) return null;

        return token;
    }

    /// <summary>
    /// 按优先级排出候选 exe 全路径（已展开的外部输入除外，见调用方），大小写不敏感去重。
    /// **不做 IO** —— 调用方逐个 <c>File.Exists</c> 取第一个命中的。
    ///
    /// <para>顺序：</para>
    /// <list type="number">
    /// <item><paramref name="protocolExePath"/>：注册表协议反推的 exe。置信度最高 —— 那就是 Windows
    /// 自己点这个协议时会拉起的东西。</item>
    /// <item><paramref name="installLocation"/> × 已知子路径：迅雷装完是 <c>D:\Thunder\Program\Thunder.exe</c>，
    /// 卸载项给的根是 <c>D:\Thunder</c>，不拼子路径找不到。</item>
    /// <item>Program Files / Program Files(x86) / 各盘根 × 已知目录名 × 已知子路径：
    /// 兜「没有卸载项」和「装在自定义盘根」（本机三个客户端都是 <c>D:\&lt;名字&gt;</c> 这种）。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 刻意**不做递归目录枚举**：无界 IO、可能阻塞 UI，而常见客户端的目录布局是已知的，
    /// 编码成 <see cref="NetdiskClientSpec.ExeRelativePaths"/> 就够了。
    /// </remarks>
    public static IReadOnlyList<string> BuildCandidates(
        NetdiskClientSpec spec,
        string? protocolExePath,
        string? installLocation,
        string? programFiles,
        string? programFilesX86,
        IReadOnlyList<string>? driveRoots)
    {
        var candidates = new List<string>();

        Add(candidates, protocolExePath);

        // 卸载项给的根就是安装目录本身，直接拼已知子路径
        AddUnderRoot(candidates, spec, installLocation);

        // 其余根只是「可能装在哪」，还要再串一层已知目录名
        var searchRoots = new List<string?>(capacity: 2 + (driveRoots?.Count ?? 0))
        {
            programFiles,
            programFilesX86
        };

        if (driveRoots is not null) searchRoots.AddRange(driveRoots);

        foreach (var searchRoot in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(searchRoot)) continue;

            foreach (var hint in spec.InstallDirHints)
                AddUnderRoot(candidates, spec, Path.Combine(searchRoot.Trim(), hint));
        }

        return candidates;
    }

    /// <summary>把「安装根目录 × 已知 exe 子路径」全部追加进去。</summary>
    private static void AddUnderRoot(List<string> candidates, NetdiskClientSpec spec, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;

        var trimmedRoot = root.Trim();
        foreach (var relativePath in spec.ExeRelativePaths)
            Add(candidates, Path.Combine(trimmedRoot, relativePath));
    }

    /// <summary>去重后追加（大小写不敏感：同一份 exe 可能同时由协议和安装目录推出）。</summary>
    private static void Add(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var trimmed = path.Trim();
        if (trimmed.Length == 0) return;
        if (candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;

        candidates.Add(trimmed);
    }
}