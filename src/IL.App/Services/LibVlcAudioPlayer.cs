using LibVLCSharp.Shared;
using System.Runtime.InteropServices;

namespace IL.App.Services;

public sealed class LibVlcAudioPlayer : IAudioPlayer
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ParseWithOptions(nint media,MediaParseOptions options,int timeout);
    [DllImport("libSystem.B.dylib", EntryPoint="setenv")]
    private static extern int SetNativeEnvironment([MarshalAs(UnmanagedType.LPUTF8Str)] string name,[MarshalAs(UnmanagedType.LPUTF8Str)] string value,int overwrite);
    private readonly nint _nativeLibrary;
    private readonly ParseWithOptions? _parseNative;
    private readonly Action<string>? _trace;
    private readonly LibVLC _lib;
    private readonly MediaPlayer _player;
    private readonly Timer _timer;
    private readonly object _state = new();
    private Media? _media;
    private TimeSpan _position, _duration;
    private TimeSpan? _pendingSeek, _loopStart, _loopEnd;
    private volatile bool _playing, _ended, _changing, _disposed;
    private volatile string? _error;
    private double _rate = 1, _volume = 1;
    private bool _applyOptions;

    public LibVlcAudioPlayer(Action<string>? trace=null)
    {
        _trace=trace;
        var native = Environment.GetEnvironmentVariable("ILP_LIBVLC_PATH");
        if (string.IsNullOrWhiteSpace(native) && OperatingSystem.IsWindows()) native = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (string.IsNullOrWhiteSpace(native) && OperatingSystem.IsMacOS()) native = Path.Combine(AppContext.BaseDirectory, "libvlc", "lib");
        if (OperatingSystem.IsMacOS() && native is not null)
        {
            var plugins=Path.GetFullPath(Path.Combine(native, "..", "plugins"));
            // .NET's environment updates do not update getenv() for native libraries on macOS.
            if(SetNativeEnvironment("VLC_PLUGIN_PATH",plugins,1)!=0)throw new IOException("无法设置播放引擎插件目录");
        }
        if (native is not null) LibVLCSharp.Shared.Core.Initialize(native);
        else LibVLCSharp.Shared.Core.Initialize();
        if(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            _nativeLibrary=NativeLibrary.Load(Path.Combine(native!,OperatingSystem.IsWindows()?"libvlc.dll":"libvlc.dylib"));
            _parseNative=Marshal.GetDelegateForFunctionPointer<ParseWithOptions>(NativeLibrary.GetExport(_nativeLibrary,"libvlc_media_parse_with_options"));
        }
        _lib = new LibVLC("--no-video", "--no-media-library", "--audio-time-stretch");
        _player = new MediaPlayer(_lib);
        _player.Playing += OnPlaying;
        _player.Paused += OnPaused;
        _player.Stopped += OnPaused;
        _player.EndReached += OnEnded;
        _player.EncounteredError += OnError;
        _timer = new Timer(Poll, null, Timeout.Infinite, Timeout.Infinite);
    }
    public string EngineVersion => _lib.Version;
    public event EventHandler? StateChanged;
    public TimeSpan Position { get { lock (_state) return _position; } }
    public TimeSpan Duration { get { lock (_state) return _duration; } }
    public bool IsPlaying => _playing;
    public string? ErrorMessage => _error;
    public double PlaybackRate
    {
        get => _rate;
        set
        {
            if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _rate = Math.Clamp(value, 0.25, 4);
            if (_player.IsPlaying && _player.SetRate((float)_rate) != 0) throw new IOException("播放引擎无法设置速度");
        }
    }
    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _volume = Math.Clamp(value, 0, 1);
            _player.Volume = (int)Math.Round(_volume * 100, MidpointRounding.AwayFromZero);
        }
    }
    public async Task OpenAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("音频文件不存在", path);
        _changing = true;
        Media? next = null;
        try
        {
            _trace?.Invoke("open: stop");
            await Task.Run(_player.Stop, ct).ConfigureAwait(false);
            _trace?.Invoke("open: create media");
            next = new Media(_lib, Path.GetFullPath(path), FromType.FromPath);
            _trace?.Invoke("open: parse");
            // LibVLCSharp resolves Parse on its native callback thread. Yield before releasing or rebinding media.
            var parsed = await ParseAsync(next,ct).ConfigureAwait(false);
            _trace?.Invoke($"open: parsed {parsed}");
            if(parsed != MediaParsedStatus.Done) throw new IOException("文件无法解析为有效音频");
            _trace?.Invoke("open: tracks");
            if(!next.Tracks.Any(t => t.TrackType == TrackType.Audio)) throw new IOException("文件无法解析为有效音频");
            _trace?.Invoke("open: bind media");
            _player.Media = next;
            var old = _media;
            _media = next;
            next = null;
            _trace?.Invoke("open: release previous media");
            old?.Dispose();
            _trace?.Invoke("open: duration");
            lock (_state)
            {
                _duration = TimeSpan.FromMilliseconds(Math.Max(0, _media.Duration));
                _position = TimeSpan.Zero;
                _pendingSeek = _loopStart = _loopEnd = null;
                _ended = _playing = false;
                _error = null;
            }
            _timer.Change(0, 100);
        }
        finally { _trace?.Invoke("open: cleanup"); next?.Dispose(); _changing = false; }
        Changed();
    }
    private async Task<MediaParsedStatus> ParseAsync(Media media,CancellationToken ct)
    {
        if(_parseNative is null)return await media.Parse(MediaParseOptions.ParseLocal,15000,ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        // Complete asynchronously so media release and event detach never run inside LibVLC's parse callback.
        var completion=new TaskCompletionSource<MediaParsedStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Parsed(object? sender,MediaParsedChangedEventArgs args)=>completion.TrySetResult(args.ParsedStatus);
        media.ParsedChanged+=Parsed;
        using var cancellation=ct.Register(()=>{media.ParseStop();completion.TrySetCanceled(ct);});
        try
        {
            ct.ThrowIfCancellationRequested();
            if(_parseNative(media.NativeReference,MediaParseOptions.ParseLocal,15000)==-1)return MediaParsedStatus.Failed;
            return await completion.Task.ConfigureAwait(false);
        }
        finally {media.ParsedChanged-=Parsed;}
    }
    public Task PlayAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); EnsureMedia();
        _ended = false; _error = null; _applyOptions = true;
        if (!_player.Play()) throw new IOException("播放引擎无法开始播放");
        _playing = true; Changed();
        return Task.CompletedTask;
    }
    public Task PauseAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); EnsureMedia();
        _player.SetPause(true); _playing = false; Changed();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _changing = true;
        try
        {
            await Task.Run(_player.Stop, ct).ConfigureAwait(false);
            lock (_state) { _playing = _ended = false; _pendingSeek = null; _position = TimeSpan.Zero; }
        }
        finally { _changing = false; }
        Changed();
    }
    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); EnsureMedia();
        var target = TimeSpan.FromMilliseconds(Math.Clamp(position.TotalMilliseconds, 0, Duration.TotalMilliseconds));
        lock (_state) { _pendingSeek = target; _position = target; _ended = false; }
        if ((!OperatingSystem.IsMacOS() || _player.IsPlaying) && _player.IsSeekable)
        {
            _player.SeekTo(target);
            lock (_state) _pendingSeek = null;
        }
        Changed();
        return Task.CompletedTask;
    }
    public Task SetLoopAsync(TimeSpan? start, TimeSpan? end, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (start < TimeSpan.Zero || end < TimeSpan.Zero || (start is not null && end is not null && end <= start))
            throw new ArgumentException("循环范围无效");
        lock (_state) { _loopStart = start; _loopEnd = end; }
        return Task.CompletedTask;
    }
    private void Poll(object? ignored)
    {
        if (_disposed || _changing || _media is null) return;
        try
        {
            TimeSpan? pending, start, end;
            lock (_state) { pending = _pendingSeek; start = _loopStart; end = _loopEnd; }
            if (_ended && start is not null && end is not null)
            {
                lock (_state) _pendingSeek = start;
                _ended = false; _applyOptions = true;
                if (!_player.Play()) throw new IOException("循环播放失败");
                _playing = true; pending = start;
            }
            var nativePlaying = _player.IsPlaying;
            if (_applyOptions && nativePlaying)
            {
                if (_player.SetRate((float)_rate) != 0) throw new IOException("播放引擎无法设置速度");
                _player.Volume = (int)Math.Round(_volume * 100);
                _applyOptions = false;
            }
            var sampled = TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
            if (pending is not null)
            {
                sampled = pending.Value;
                if ((!OperatingSystem.IsMacOS() || nativePlaying) && _player.IsSeekable)
                {
                    _player.SeekTo(sampled);
                    lock (_state) { if (_pendingSeek == pending) _pendingSeek = null; }
                }
            }
            else if (nativePlaying && start is not null && end is not null && sampled >= end)
            {
                _player.SeekTo(start.Value); sampled = start.Value;
            }
            bool changed;
            _trace?.Invoke($"poll: native={_player.Time} sampled={sampled.TotalMilliseconds} playing={nativePlaying} pending={pending?.TotalMilliseconds} ended={_ended} loop={start?.TotalMilliseconds}-{end?.TotalMilliseconds}");
            lock (_state)
            {
                if (_ended) sampled = _duration;
                changed = sampled != _position;
                _position = sampled;
            }
            if (changed) Changed();
        }
        catch (Exception ex) { if (!_disposed) { _error = ex.Message; _playing = false; Changed(); } }
    }
    private void OnPlaying(object? sender, EventArgs args) { _playing = true; Changed(); }
    private void OnPaused(object? sender, EventArgs args) { _playing = false; Changed(); }
    private void OnEnded(object? sender, EventArgs args) { _ended = true; _playing = false; Changed(); }
    private void OnError(object? sender, EventArgs args) { _error = "播放失败，请检查音频文件与播放设备"; _playing = false; Changed(); }
    private void Changed() { if (!_disposed && !_changing) StateChanged?.Invoke(this, EventArgs.Empty); }
    private void EnsureMedia() { ObjectDisposedException.ThrowIf(_disposed, this); if (_media is null) throw new InvalidOperationException("请先打开音频"); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _timer.DisposeAsync().ConfigureAwait(false);
        _player.Playing -= OnPlaying; _player.Paused -= OnPaused; _player.Stopped -= OnPaused;
        _player.EndReached -= OnEnded; _player.EncounteredError -= OnError;
        await Task.Run(_player.Stop).ConfigureAwait(false);
        _player.Dispose(); _media?.Dispose(); _lib.Dispose();
        if(_nativeLibrary!=0)NativeLibrary.Free(_nativeLibrary);
    }
}
