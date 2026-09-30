namespace IL.App.Services;

public interface IAudioPlayer : IAsyncDisposable
{
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }
    string? ErrorMessage { get; }
    double PlaybackRate { get; set; }
    double Volume { get; set; }
    event EventHandler? StateChanged;
    Task OpenAsync(string path, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
    Task SetLoopAsync(TimeSpan? start, TimeSpan? end, CancellationToken ct = default);
}
