using System.Text.Json;

namespace TuringDesk.Desktop.Services;

internal static class AppSearchVerification
{
    private static readonly (string Query, string ExpectedName)[] Cases =
    [
        ("settings", "设置"),
        ("sz", "设置"),
        ("taskmgr", "任务管理器"),
        ("tmgr", "任务管理器"),
        ("explorer", "文件资源管理器")
    ];

    public static async Task<AppSearchVerificationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using var index = new DesktopSearchIndexService(initializeFileSearch: false);
        try
        {
            await index.AppSearchInitialization.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return WriteResult(new AppSearchVerificationResult(
                false,
                "应用索引在 10 秒内没有完成初始化",
                index.AppCount,
                index.AppSearchStatus,
                []));
        }

        if (!index.AppSearchReady)
        {
            return WriteResult(new AppSearchVerificationResult(
                false,
                "应用索引初始化完成但未进入 ready 状态",
                index.AppCount,
                index.AppSearchStatus,
                []));
        }

        var checks = new List<AppSearchVerificationCheck>(Cases.Length);
        foreach (var (query, expectedName) in Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = index.SearchApps(query, 5);
            var matched = results.Any(result => result.Name.Equals(expectedName, StringComparison.CurrentCultureIgnoreCase));
            checks.Add(new AppSearchVerificationCheck(
                query,
                expectedName,
                matched,
                results.Select(result => result.Name).ToArray()));
        }

        var success = checks.All(check => check.Passed);
        return WriteResult(new AppSearchVerificationResult(
            success,
            success ? "L1 应用搜索自测通过" : "L1 应用搜索存在未命中用例",
            index.AppCount,
            index.AppSearchStatus,
            checks));
    }

    private static AppSearchVerificationResult WriteResult(AppSearchVerificationResult result)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TuringDesk");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "app-search-self-test.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // Self-test status persistence is diagnostic only; the exit code remains
            // the source of truth for CI.
        }

        return result;
    }
}

internal sealed record AppSearchVerificationResult(
    bool Success,
    string Message,
    int AppCount,
    string Status,
    IReadOnlyList<AppSearchVerificationCheck> Checks);

internal sealed record AppSearchVerificationCheck(
    string Query,
    string ExpectedName,
    bool Passed,
    IReadOnlyList<string> Results);
