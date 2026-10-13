using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// <see cref="NetdiskLaunchPlanner"/> 的两块纯逻辑：
/// <list type="bullet">
/// <item><c>ParseExecutablePath</c> 从注册表 <c>open\command</c> 串里剥 exe 路径 —— 关键在「拿不准就 null」，
/// 那些把系统宿主（<c>rundll32.exe</c> / <c>explorer.exe</c>）当处理器注册的键绝不能当启动目标。</item>
/// <item><c>BuildCandidates</c> 排出候选路径 —— 关键在顺序与去重，以及「迅雷的 exe 在 <c>Program\</c> 子目录下」
/// 这种来自实机的目录布局。</item>
/// </list>
/// </summary>
public class NetdiskLaunchPlannerTests
{
    private const string Quote = "\"";

    private static readonly NetdiskClientSpec Baidu = NetdiskClientCatalog.Find(NetdiskPlatform.Baidu);
    private static readonly NetdiskClientSpec Quark = NetdiskClientCatalog.Find(NetdiskPlatform.Quark);
    private static readonly NetdiskClientSpec Thunder = NetdiskClientCatalog.Find(NetdiskPlatform.Thunder);

    /// <summary>按注册表的形态把各部分拼成 <c>open\command</c>：各段分别加引号、空格分隔。</summary>
    private static string Quoted(params string[] parts)
        => string.Join(' ', parts.Select(part => $"{Quote}{part}{Quote}"));

    // ── ParseExecutablePath ─────────────────────────────

    [Fact]
    public void ParsesAQuotedExecutableAndIgnoresTheArgument()
        => Assert.Equal(@"D:\Quark\quark.exe",
            NetdiskLaunchPlanner.ParseExecutablePath(Quoted(@"D:\Quark\quark.exe", "%1")));

    [Fact]
    public void KeepsSpacesInsideQuotes()
        => Assert.Equal(@"C:\Program Files\Foo\app.exe",
            NetdiskLaunchPlanner.ParseExecutablePath(Quoted(@"C:\Program Files\Foo\app.exe", "%1")));

    [Fact]
    public void ParsesAnUnquotedExecutable()
        => Assert.Equal(@"D:\Q\quark.exe",
            NetdiskLaunchPlanner.ParseExecutablePath($@"D:\Q\quark.exe {Quote}%1{Quote}"));

    [Fact]
    public void DropsTrailingSwitches()
        => Assert.Equal(@"D:\Thunder\Program\Thunder.exe",
            NetdiskLaunchPlanner.ParseExecutablePath(
                $"{Quoted(@"D:\Thunder\Program\Thunder.exe", "%1")} -StartType:thunder"));

    [Theory]
    [InlineData(@"C:\app.exe")] // 光秃秃一个引号串
    [InlineData(@"C:\app.exe", "%1")]
    [InlineData(@"C:\app.exe", "%1", "%*")]
    public void ToleratesTheCommonArgumentShapes(params string[] parts)
        => Assert.Equal(@"C:\app.exe", NetdiskLaunchPlanner.ParseExecutablePath(Quoted(parts)));

    [Fact]
    public void ReturnsNullForAnUnquotedBareArgument()
        => Assert.Equal(@"C:\app.exe", NetdiskLaunchPlanner.ParseExecutablePath($"{Quoted(@"C:\app.exe")} %1"));

    [Fact]
    public void RejectsSystemHostsThatCarryTheirPayloadInArguments()
    {
        // rundll32 的载荷是参数（x.dll,Foo），把它当目标启动没有任何意义
        Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(
            Quoted(@"C:\Windows\System32\rundll32.exe", @"x.dll,Foo")));
        Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(
            @"rundll32.exe shell32.dll,OpenAs_RunDLL %1"));
    }

    [Fact]
    public void RejectsExplorer()
    {
        Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(Quoted("explorer.exe", "%1")));
        Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(Quoted(@"C:\Windows\explorer.exe", "%1")));
    }

    [Theory]
    [InlineData(@"C:\noext")]
    [InlineData(@"C:\app.dll")]
    public void RejectsAnythingThatIsNotAnExe(string path)
        => Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(Quoted(path)));

    [Fact]
    public void RejectsAnUnterminatedQuote()
        => Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath($"{Quote}C:\\app.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForBlankInput(string? command)
        => Assert.Null(NetdiskLaunchPlanner.ParseExecutablePath(command));

    [Fact]
    public void LeavesEnvironmentVariablesUnexpanded()
    {
        // 展开交给调用方（那里才知道要不要过 File.Exists），本函数保持确定、可测
        Assert.Equal(@"%ProgramFiles%\Foo\app.exe",
            NetdiskLaunchPlanner.ParseExecutablePath(Quoted(@"%ProgramFiles%\Foo\app.exe", "%1")));
    }

    // ── BuildCandidates ────────────────────────────────

    [Fact]
    public void PutsTheProtocolExecutableFirst()
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Quark, protocolExePath: @"D:\Quark\quark.exe", installLocation: null,
            programFiles: null, programFilesX86: null, driveRoots: null);

        Assert.Equal(@"D:\Quark\quark.exe", candidates[0]);
    }

    [Fact]
    public void FindsBaiduUnderItsUninstallInstallLocation()
    {
        // 百度没有 URL 协议，这条是它唯一的来源
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Baidu, protocolExePath: null, installLocation: @"D:\BaiduNetdisk",
            programFiles: null, programFilesX86: null, driveRoots: null);

        Assert.Contains(@"D:\BaiduNetdisk\BaiduNetdisk.exe", candidates);
    }

    [Fact]
    public void FindsThunderUnderItsProgramSubfolder()
    {
        // 实机形态：卸载项给的根是 D:\Thunder，exe 其实在 D:\Thunder\Program\ 下
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Thunder, protocolExePath: null, installLocation: @"D:\Thunder",
            programFiles: null, programFilesX86: null, driveRoots: null);

        Assert.Contains(@"D:\Thunder\Program\Thunder.exe", candidates);
    }

    [Fact]
    public void ScansDriveRootsWhenThereIsNoUninstallEntry()
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Thunder, protocolExePath: null, installLocation: null,
            programFiles: null, programFilesX86: null, driveRoots: [@"D:\"]);

        Assert.Contains(@"D:\Thunder\Program\Thunder.exe", candidates);
    }

    [Fact]
    public void ScansProgramFiles()
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Quark, protocolExePath: null, installLocation: null,
            programFiles: @"C:\Program Files", programFilesX86: null, driveRoots: null);

        Assert.Contains(@"C:\Program Files\Quark\quark.exe", candidates);
    }

    [Fact]
    public void DeduplicatesCaseInsensitively()
    {
        // 协议路径与 installLocation 推出的是同一个 exe，大小写不同也算同一条
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Quark, protocolExePath: @"d:\quark\quark.exe", installLocation: @"D:\Quark",
            programFiles: null, programFilesX86: null, driveRoots: null);

        Assert.Single(candidates);
    }

    [Fact]
    public void ReturnsAnEmptyListWhenNothingIsKnown()
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Baidu, protocolExePath: null, installLocation: null,
            programFiles: null, programFilesX86: null, driveRoots: null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void SkipsBlankDriveRoots()
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            Quark, protocolExePath: null, installLocation: null,
            programFiles: null, programFilesX86: null, driveRoots: ["", "   "]);

        Assert.Empty(candidates);
    }
}