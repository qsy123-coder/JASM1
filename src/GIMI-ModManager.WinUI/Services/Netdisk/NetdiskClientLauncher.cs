using System.Diagnostics;
using GIMI_ModManager.Core.Helpers;
using Microsoft.Win32;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Netdisk;

/// <summary>点网盘卡片后「顺手把客户端拉起来」的结果，UI 据此选提示文案。</summary>
public enum NetdiskLaunchOutcome
{
    /// <summary>本次把客户端拉起来了。</summary>
    Opened,

    /// <summary>客户端本来就在跑 —— 按要求只复制链接，不重复打开。</summary>
    AlreadyRunning,

    /// <summary>认得这个平台，但机器上找不到它的 exe（没装 / 装在认不出的位置）。</summary>
    NotFound,

    /// <summary>平台名和链接都对不上任何已知客户端。</summary>
    UnknownPlatform,

    /// <summary>找到了 exe，但启动时抛异常。</summary>
    Failed
}

/// <summary>
/// 点网盘卡片后的动作：复制链接之后，把对应的网盘客户端拉起来 —— **已经在跑就什么都不做**。
///
/// <para><b>为什么「已在跑就不启动」</b>：客户端都是单实例的，再启动一次要么只是把已有窗口
/// 提到前台（等于抢用户焦点），要么留下多余进程。用户要的是「开着就只复制」。</para>
///
/// <para><b>为什么裸启动、不传链接</b>：链接此刻已经在剪贴板里了，客户端自己会读、或者用户自己粘贴。
/// 各客户端对命令行参数 / 自定义协议接受的形式并不一致（百度网盘甚至不注册任何 URL 协议），
/// 猜参数反而是引入不确定性的那一头。</para>
///
/// <para>这里是唯一碰注册表 / Process 的地方；纯逻辑（认平台、解析命令串、排候选路径）都在
/// <see cref="NetdiskClientCatalog"/> 与 <see cref="NetdiskLaunchPlanner"/>，好让测试工程能覆盖。</para>
/// </summary>
public sealed class NetdiskClientLauncher(ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<NetdiskClientLauncher>();

    /// <summary>
    /// 复制之后调这个：能认得出的平台且客户端没在跑，就把它的 exe 拉起来。任何一步拿不准都只是
    /// 返回一个「没启动」的结果，绝不让调用方因为这事报错。
    /// </summary>
    /// <param name="platformName">卡片上的平台名（如「夸克网盘」/「baidu」）。</param>
    /// <param name="url">分享链接，用于在平台名认不出时按 host 兜底。</param>
    public NetdiskLaunchOutcome Launch(string platformName, string url)
    {
        var spec = NetdiskClientCatalog.Detect(platformName, url);
        if (spec is null)
        {
            _logger.Information("[Netdisk] 平台认不出(名字={Name})，只复制不启动", platformName);
            return NetdiskLaunchOutcome.UnknownPlatform;
        }

        if (IsRunning(spec))
        {
            _logger.Information("[Netdisk] {Platform} 客户端已在运行，只复制不重复打开", spec.DisplayName);
            return NetdiskLaunchOutcome.AlreadyRunning;
        }

        var exePath = ResolveExecutable(spec);
        if (exePath is null)
        {
            _logger.Information("[Netdisk] 找不到 {Platform} 客户端的 exe，只复制不启动", spec.DisplayName);
            return NetdiskLaunchOutcome.NotFound;
        }

        try
        {
            // UseShellExecute=true：等同双击，客户端的清单 / 提权交给 shell 处理。
            // 不设 CreateNoWindow —— 它与 UseShellExecute=true 同用会抛 InvalidOperationException。
            // 不传 Arguments —— 链接已经在剪贴板里，见类注释。
            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                // 有的客户端按当前目录找自己的资源文件
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });

            _logger.Information("[Netdisk] 已拉起 {Platform} 客户端：{Exe}", spec.DisplayName, exePath);
            return NetdiskLaunchOutcome.Opened;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Netdisk] 启动 {Platform} 客户端失败：{Exe}", spec.DisplayName, exePath);
            return NetdiskLaunchOutcome.Failed;
        }
    }

    /// <summary>
    /// 主进程是否在跑。**只查主 exe 名**，不含 <c>YunDetectService.exe</c> 这类常驻助手 ——
    /// 把助手也算「在跑」会导致该启动的时候不启动，用户点了没反应，比多启动一次更糟。
    /// 查询失败（极罕见）当作未运行，同理。
    /// </summary>
    private bool IsRunning(NetdiskClientSpec spec)
    {
        foreach (var exeName in spec.ExeNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(exeName);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Netdisk] 查询进程 {ExeName} 失败，当作未运行", exeName);
                continue;
            }

            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return false;
    }

    /// <summary>逐个候选路径 <c>File.Exists</c>，取第一个存在的。</summary>
    private string? ResolveExecutable(NetdiskClientSpec spec)
    {
        var candidates = NetdiskLaunchPlanner.BuildCandidates(
            spec,
            protocolExePath: TryReadProtocolExecutable(spec),
            installLocation: TryFindInstallLocation(spec),
            programFiles: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            programFilesX86: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            driveRoots: GetFixedDriveRoots());

        foreach (var candidate in candidates)
        {
            // 候选里可能带 %ProgramFiles% 这类变量（注册表串往往是这么写的）
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded)) return expanded;
        }

        return null;
    }

    /// <summary>
    /// 从 URL 协议注册项反推 exe 路径（Windows 自己点这个协议时拉的就是它）。
    ///
    /// <para>百度网盘没有协议键，这条对它返回 <c>null</c>，由安装目录那条路兜住。</para>
    /// </summary>
    private string? TryReadProtocolExecutable(NetdiskClientSpec spec)
    {
        foreach (var protocolKey in spec.ProtocolKeys)
        {
            var command = ReadOpenCommand(protocolKey);
            if (command is null) continue;

            var exePath = NetdiskLaunchPlanner.ParseExecutablePath(command);
            if (exePath is null) continue;

            // 不盲信注册表指向哪：文件名必须正是我们认的那个客户端
            var fileName = Path.GetFileName(exePath);
            var expected = spec.ExeNames.Any(name =>
                fileName.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase));

            if (!expected)
            {
                _logger.Debug("[Netdisk] 协议 {Key} 指向 {Exe}，文件名对不上 {Platform}，跳过",
                    protocolKey, exePath, spec.DisplayName);
                continue;
            }

            return exePath;
        }

        return null;
    }

    /// <summary>读 <c>Software\Classes\&lt;key&gt;\shell\open\command</c>。HKCU 与 HKLM、32/64 视图都试一遍。</summary>
    private static string? ReadOpenCommand(string protocolKey)
    {
        var subPath = $@"Software\Classes\{protocolKey}\shell\open\command";

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var commandKey = baseKey.OpenSubKey(subPath);
                    if (commandKey?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch (Exception)
                {
                    // 这个 hive / 视图读不到就当这条没有，继续找下一条路径
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 扫卸载项，按 <c>DisplayName</c> 命中平台关键词后取 <c>InstallLocation</c>。
    /// 百度网盘只能靠这条（它没有 URL 协议）。
    /// </summary>
    private string? TryFindInstallLocation(NetdiskClientSpec spec)
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(uninstallPath);
                    if (uninstall is null) continue;

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(subKeyName);
                        if (entry is null) continue;

                        if (entry.GetValue("DisplayName") is not string displayName) continue;
                        if (!MatchesAny(displayName, spec.UninstallNameTokens)) continue;

                        if (entry.GetValue("InstallLocation") is string location
                            && !string.IsNullOrWhiteSpace(location)
                            && Directory.Exists(location))
                        {
                            return location;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[Netdisk] 扫卸载项失败 ({Hive}/{View})", hive, view);
                }
            }
        }

        return null;
    }

    private static bool MatchesAny(string value, string[] tokens)
        => tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>各固定磁盘的根目录（<c>D:\</c> 这种）。客户端常直接装在盘根下的自定义目录里。</summary>
    private static IReadOnlyList<string> GetFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}