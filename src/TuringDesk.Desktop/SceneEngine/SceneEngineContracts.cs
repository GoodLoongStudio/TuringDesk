namespace TuringDesk.Desktop.SceneEngine;

public interface ISceneRenderer : IAsyncDisposable
{
    SceneProjectType ProjectType { get; }
    bool IsLoaded { get; }
    ScenePlaybackState State { get; }

    Task LoadAsync(ScenePackage package, string packageRoot, CancellationToken cancellationToken = default);
    Task ApplyPropertiesAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
    Task SetPlaybackStateAsync(ScenePlaybackState state, CancellationToken cancellationToken = default);
    Task SetTargetFpsAsync(int fps, CancellationToken cancellationToken = default);
    Task ResizeAsync(int pixelWidth, int pixelHeight, double dpiScale, CancellationToken cancellationToken = default);
}

public interface ISceneRendererFactory
{
    bool CanCreate(SceneProjectType type);
    ISceneRenderer Create(SceneProjectType type);
}

public sealed record InstalledScene(
    ScenePackage Package,
    string PackageRoot,
    string ManifestPath,
    DateTimeOffset InstalledAtUtc);

public sealed record SceneRuntimeContext(
    string MonitorId,
    int PixelWidth,
    int PixelHeight,
    double DpiScale,
    bool IsPrimary,
    bool IsSpanning);
