namespace TuringDesk.Desktop.Services.SceneEngine;

internal sealed record PlaybackDirective(ApplicationRuleAction Action, string? TargetId, string Reason)
{
    public static PlaybackDirective Keep(string reason = "default") => new(ApplicationRuleAction.KeepRunning, null, reason);
}

internal sealed class DesktopPlaybackRuleEngine
{
    public PlaybackDirective Evaluate(DesktopPlaybackSettings settings)
    {
        var foreground = DesktopScenePerformancePolicy.GetForegroundApp();

        // Explicit per-application rules beat global fullscreen/maximized behavior.
        foreach (var rule in settings.ApplicationRules)
        {
            if (string.IsNullOrWhiteSpace(rule.ExeName)) continue;
            var matches = rule.Condition switch
            {
                ApplicationRuleCondition.Fullscreen => foreground is { IsFullscreen: true } &&
                    string.Equals(foreground.ExeName, rule.ExeName, StringComparison.OrdinalIgnoreCase),
                _ => DesktopScenePerformancePolicy.IsProcessRunning(rule.ExeName)
            };
            if (!matches) continue;
            return new PlaybackDirective(rule.Action, rule.TargetId, $"app:{rule.ExeName}");
        }

        if (foreground is { IsFullscreen: true })
        {
            return settings.FullscreenBehavior switch
            {
                PlaybackBehavior.Stop => new(ApplicationRuleAction.Stop, null, "fullscreen"),
                PlaybackBehavior.Pause => new(ApplicationRuleAction.Pause, null, "fullscreen"),
                _ => PlaybackDirective.Keep("fullscreen")
            };
        }

        if (foreground is { IsMaximized: true })
        {
            return settings.MaximizedBehavior switch
            {
                PlaybackBehavior.Stop => new(ApplicationRuleAction.Stop, null, "maximized"),
                PlaybackBehavior.Pause => new(ApplicationRuleAction.Pause, null, "maximized"),
                _ => PlaybackDirective.Keep("maximized")
            };
        }

        return PlaybackDirective.Keep();
    }
}
