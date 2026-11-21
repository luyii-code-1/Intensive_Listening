using Avalonia.Threading;
using IL.Core.Transcription;

namespace IL.App.Views;

internal sealed class QueueRefreshSubscription : IDisposable
{
    private readonly TranscriptionQueue _queue;
    private readonly DispatcherTimer _timer;
    private int _pending;
    private bool _disposed;

    public QueueRefreshSubscription(TranscriptionQueue queue, Action refresh)
    {
        _queue = queue;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Interlocked.Exchange(ref _pending, 0);
            if (!_disposed) refresh();
        };
        _queue.Changed += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) _timer.Start();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.Changed -= OnChanged;
        _timer.Stop();
    }
}
