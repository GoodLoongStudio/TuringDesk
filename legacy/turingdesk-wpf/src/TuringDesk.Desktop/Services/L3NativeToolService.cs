using System.IO;
using System.Text.RegularExpressions;

namespace TuringDesk.Desktop.Services;

/// <summary>
/// Bounded native capability layer for Level 3.
///
/// L3 is intentionally not a raw terminal. It exposes a small set of TuringDesk-
/// owned operations that are cheap, auditable and do not require Node/Harness:
/// status inspection, app/file lookup and explicit app/file opening. Privileged,
/// destructive or arbitrary-shell requests are never executed here and are routed
/// to the explicit L4 boundary instead.
/// </summary>
public sealed class L3NativeToolService
{
    private static readonly Regex NaturalAppOpenPattern = new(
        @"^(?:请|帮我|麻烦)?\s*(?:打开|启动|运行)\s*(?:一下|下)?\s*(?:应用|程序)?\s*[:：]?\s*(?<target>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NaturalFileSearchPattern = new(
        @"^(?:请|帮我|麻烦)?\s*(?:找|查找|搜索|搜)\s*(?:一下|下)?\s*(?:文件|文件夹|目录)?\s*[:：]?\s*(?<target>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex NaturalFileOpenPattern = new(
        @"^(?:请|帮我|麻烦)?\s*(?:打开)\s*(?:一下|下)?\s*(?:文件|文件夹|目录)\s*[:：]?\s*(?<target>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitHighRiskRequestPattern = new(
        @"(?:帮我|替我|给我|麻烦|请)\s*(?:用|通过|执行|运行|调用|删除|删掉|卸载|安装|关机|重启|格式化|修改|写入|提权)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] HighRiskActionMarkers =
    {
        "执行命令", "运行命令", "删除文件", "删掉文件", "卸载", "安装软件", "关机", "重启电脑",
        "格式化", "修改注册表", "写注册表", "管理员权限执行", "以管理员运行", "提权"
    };

    private static readonly string[] ShellTopicMarkers =
    {
        "powershell", "cmd.exe", "命令行", "终端命令"
    };

    private static readonly string[] ShellExecutionMarkers =
    {
        "执行 powershell", "运行 powershell", "调用 powershell", "用 powershell", "通过 powershell",
        "打开 powershell", "启动 powershell", "执行cmd", "运行cmd", "调用cmd", "用cmd", "通过cmd"
    };

    private static readonly string[] InformationalIntentMarkers =
    {
        "什么是", "是什么", "解释", "介绍", "教程", "怎么", "如何", "为什么", "区别", "原理",
        "用法", "语法", "安全吗", "风险", "示例", "例子", "能不能", "能否", "可以吗", "是否可以"
    };

    private readonly DesktopSearchIndexService _search;

    public L3NativeToolService(DesktopSearchIndexService search)
    {
        _search = search;
    }

    public async Task<L3NativeToolResult?> TryHandleAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;

        if (IsHelp(text))
            return Answer("CLI · 本地能力", HelpText());

        if (IsUnsafeOrPrivilegedIntent(text))
        {
            return Escalate(
                "CLI · 需要深度处理",
                "这个请求涉及任意命令、系统级修改或高风险动作。L3 不会直接调用 PowerShell/CMD、删除、安装、关机或管理员操作；如确实需要，请显式进入 Harness 深度处理并在工作台内确认执行范围。");
        }

        if (IsStatusQuery(text))
            return Answer("CLI · 系统状态", BuildSystemStatus());

        if (IsClockQuery(text))
            return Answer("CLI · 本机时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));

        if (TryGetSlashArgument(text, "/apps", out var appQuery))
            return Answer("CLI · 应用搜索", FormatAppMatches(appQuery));

        if (TryGetSlashArgument(text, "/files", out var fileQuery))
            return Answer("CLI · 文件搜索", await FormatFileMatchesAsync(fileQuery, cancellationToken));

        if (TryGetSlashArgument(text, "/open-file", out var openFileQuery))
            return await OpenFileAsync(openFileQuery, cancellationToken);

        if (TryGetSlashArgument(text, "/open", out var openAppQuery))
            return OpenApp(openAppQuery);

        var fileOpenMatch = NaturalFileOpenPattern.Match(text);
        if (fileOpenMatch.Success)
            return await OpenFileAsync(fileOpenMatch.Groups["target"].Value, cancellationToken);

        var appOpenMatch = NaturalAppOpenPattern.Match(text);
        if (appOpenMatch.Success)
            return OpenApp(appOpenMatch.Groups["target"].Value);

        var fileSearchMatch = NaturalFileSearchPattern.Match(text);
        if (fileSearchMatch.Success)
            return Answer(
                "CLI · 文件搜索",
                await FormatFileMatchesAsync(fileSearchMatch.Groups["target"].Value, cancellationToken));

        return null;
    }

    private L3NativeToolResult OpenApp(string rawQuery)
    {
        var query = CleanArgument(rawQuery);
        if (query.Length == 0)
            return Answer("CLI · 应用启动", "请输入要打开的应用名称，例如：/open 记事本");

        if (LooksLikeRawCommand(query))
        {
            return Escalate(
                "CLI · 已阻止原始命令",
                "L3 只通过已发现的 Windows 应用条目启动程序，不把输入当作 shell 命令执行。需要任意命令时请显式进入深度处理。");
        }

        var matches = _search.SearchApps(query, 5);
        if (matches.Count == 0)
            return Answer("CLI · 未找到应用", $"没有找到“{query}”对应的已安装应用。你也可以直接在 L1 搜索结果中选择应用。");

        var best = matches[0];
        var confident = IsConfidentAppMatch(query, best);
        if (!confident)
        {
            return Answer(
                "CLI · 应用匹配",
                "没有足够明确的唯一匹配，未自动启动。候选：\n" +
                string.Join("\n", matches.Take(5).Select((item, index) => $"{index + 1}. {item.Name}")));
        }

        return _search.Open(best)
            ? Answer("CLI · 已执行", $"已打开 {best.Name}。")
            : Answer("CLI · 启动失败", $"找到了 {best.Name}，但 Windows 未能启动它。可在 L1 结果中再次尝试。");
    }

    private async Task<L3NativeToolResult> OpenFileAsync(
        string rawQuery,
        CancellationToken cancellationToken)
    {
        var query = CleanArgument(rawQuery);
        if (query.Length == 0)
            return Answer("CLI · 打开文件", "请输入文件名，例如：/open-file 预算.xlsx");

        var matches = await _search.SearchFilesAsync(query, 6, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return Answer(
                "CLI · 未找到文件",
                $"没有通过 {_search.FileSearchProviderName} 找到“{query}”。当前状态：{_search.FileSearchStatus}");
        }

        var exact = matches
            .Where(item =>
                item.Name.Equals(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Target.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(item.Name).Equals(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        if (exact.Length != 1)
        {
            return Answer(
                "CLI · 文件匹配",
                "为避免打开错误文件，当前没有唯一精确匹配，未自动打开。候选：\n" +
                string.Join("\n", matches.Take(6).Select((item, index) => $"{index + 1}. {item.Name} — {item.Target}")));
        }

        return _search.Open(exact[0])
            ? Answer("CLI · 已执行", $"已打开 {exact[0].Name}。")
            : Answer("CLI · 打开失败", $"找到了 {exact[0].Target}，但 Windows 默认关联未能打开它。");
    }

    private string FormatAppMatches(string rawQuery)
    {
        var query = CleanArgument(rawQuery);
        if (query.Length == 0) return "请输入应用关键词，例如：/apps vscode";

        var matches = _search.SearchApps(query, 6);
        if (matches.Count == 0) return $"没有找到“{query}”对应的已安装应用。";

        return string.Join(
            "\n",
            matches.Take(6).Select((item, index) => $"{index + 1}. {item.Name} — {item.Subtitle}"));
    }

    private async Task<string> FormatFileMatchesAsync(string rawQuery, CancellationToken cancellationToken)
    {
        var query = CleanArgument(rawQuery);
        if (query.Length == 0) return "请输入文件关键词，例如：/files 预算";

        var matches = await _search.SearchFilesAsync(query, 6, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
            return $"没有通过 {_search.FileSearchProviderName} 找到“{query}”。当前状态：{_search.FileSearchStatus}";

        return string.Join(
            "\n",
            matches.Take(6).Select((item, index) => $"{index + 1}. {item.Name} — {item.Target}"));
    }

    private static string BuildSystemStatus()
    {
        var status = SystemStatusService.Read();
        var lines = new List<string>
        {
            status.NetworkLabel,
            status.BatteryLabel
        };
        return string.Join("\n", lines);
    }

    private static bool IsHelp(string text) =>
        text.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("cli help", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("cli 帮助", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("你能做什么", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("你可以做什么", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusQuery(string text)
    {
        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase)) return true;
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("系统状态", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("电脑状态", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("电池状态", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("电量", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("网络状态", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("联网了吗", StringComparison.OrdinalIgnoreCase) ||
               compact.Contains("网络通不通", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClockQuery(string text)
    {
        if (text.Equals("/time", StringComparison.OrdinalIgnoreCase)) return true;
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact is "几点了" or "现在几点" or "现在几点了" or "今天几号" or "今天日期";
    }

    private static bool IsUnsafeOrPrivilegedIntent(string text)
    {
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var informational = InformationalIntentMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var explicitRequest = ExplicitHighRiskRequestPattern.IsMatch(normalized);

        // Discussion, explanation and coding advice about system tools belong in L3.
        // Only an actual request to perform a privileged/destructive action crosses
        // the L4 boundary. This prevents phrases such as “解释 PowerShell” or
        // “如何安装软件” from waking Harness.
        if (informational && !explicitRequest)
            return false;

        if (LooksLikeExplicitRawCommand(normalized))
            return true;

        if (HighRiskActionMarkers.Any(marker =>
                normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        var mentionsShell = ShellTopicMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (!mentionsShell)
            return false;

        return ShellExecutionMarkers.Any(marker =>
                   normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
               explicitRequest;
    }

    private static bool LooksLikeExplicitRawCommand(string text) =>
        text.Contains("&&", StringComparison.Ordinal) ||
        text.Contains("||", StringComparison.Ordinal) ||
        text.Contains('|') ||
        text.Contains('>') ||
        text.Contains('<') ||
        text.Contains(";", StringComparison.Ordinal) ||
        text.StartsWith("powershell -", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("powershell.exe ", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("cmd /", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("cmd.exe /", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRawCommand(string query) =>
        query.Contains("&&", StringComparison.Ordinal) ||
        query.Contains("||", StringComparison.Ordinal) ||
        query.Contains('|') ||
        query.Contains('>') ||
        query.Contains('<') ||
        query.Contains(";", StringComparison.Ordinal) ||
        query.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSlashArgument(string text, string command, out string argument)
    {
        argument = string.Empty;
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Length == command.Length) return true;
        if (!char.IsWhiteSpace(text[command.Length])) return false;
        argument = text[(command.Length + 1)..].Trim();
        return true;
    }

    private static string CleanArgument(string raw) =>
        raw.Trim().Trim('“', '”', '"', '\'', ' ');

    private static bool IsConfidentAppMatch(string query, DesktopSearchResult result)
    {
        var normalized = query.Trim();
        return result.Score >= 990 ||
               result.Name.Equals(normalized, StringComparison.CurrentCultureIgnoreCase) ||
               Path.GetFileNameWithoutExtension(result.Target)
                   .Equals(normalized, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string HelpText() =>
        "L3 是 TuringDesk 自己的受控 CLI / 轻 Agent，不是裸终端。当前可用：\n" +
        "/status — 查看网络/电源状态\n" +
        "/time — 查看本机时间\n" +
        "/apps <关键词> — 搜索已安装应用\n" +
        "/files <关键词> — 搜索本机文件\n" +
        "/open <应用名> — 明确匹配后启动应用\n" +
        "/open-file <文件名> — 唯一精确匹配后打开文件\n" +
        "也支持“打开记事本”“搜索文件 预算”等自然语言。任意 shell、删除、安装、关机和管理员操作不会在 L3 直接执行。";

    private static L3NativeToolResult Answer(string title, string message) =>
        new(title, message, RequiresDeepProcessing: false);

    private static L3NativeToolResult Escalate(string title, string message) =>
        new(title, message, RequiresDeepProcessing: true);
}

public sealed record L3NativeToolResult(
    string Title,
    string Message,
    bool RequiresDeepProcessing);
