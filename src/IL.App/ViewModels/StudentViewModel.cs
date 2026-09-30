using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IL.App.Services;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Playback;
using IL.Core.Settings;
using IL.Core.Student;

namespace IL.App.ViewModels;

public sealed record LessonListItem(ImportedLesson Lesson)
{
    public string Id => Lesson.Id;
    public string Title => Lesson.Manifest.Title;
    public string Detail => $"{Lesson.Manifest.Duration:mm\\:ss} · v{Lesson.Manifest.PackageVersion}";
}
public sealed record CueRow(int Index, SrtCue Cue)
{
    public string Time => Cue.Start.ToString(@"mm\:ss");
    public string Text => Cue.Text;
}

public partial class StudentViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<IAudioPlayer> _factory;
    private readonly Action<Action> _dispatch;
    private readonly IlpLibrary _library;
    private readonly IlpImporter _importer;
    private readonly LessonProgressStore _progressStore;
    private readonly AppSettingsStore _settings;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private Dictionary<string, LessonProgress> _progress = [];
    private HashSet<string> _revealed = [];
    private readonly HashSet<string> _revealedMaterials = [], _hiddenMaterials = [];
    private IAudioPlayer? _player;
    private ImportedLesson? _lesson;
    private string? _audioPath, _playerSource;
    private int _generation;
    private int? _preroll, _returnCue;
    private TimeSpan? _repeatEnd;
    private DateTimeOffset _lastProgressSave;
    private bool _switching, _disposed;

    public StudentViewModel(Func<IAudioPlayer> factory, string libraryDirectory, LessonProgressStore progressStore,
        AppSettingsStore settings, Action<Action>? dispatch = null)
    {
        _factory = factory; _library = new(libraryDirectory); _importer = new(libraryDirectory);
        _progressStore = progressStore; _settings = settings; _dispatch = dispatch ?? (action => action());
    }
    public ObservableCollection<LessonListItem> Lessons { get; } = [];
    public ObservableCollection<CueRow> Cues { get; } = [];
    public ObservableCollection<LessonQuestion> Questions { get; } = [];
    public IReadOnlyList<double> PlaybackRates { get; } = [0.5, 0.75, 0.8, 1, 1.25, 1.5, 2];
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;
    public ImportedLesson? CurrentLesson => _lesson;
    public bool CanPlay => _audioPath is not null && !IsBusy;
    public bool CanReturn => _returnCue is not null;
    public string PlayLabel => IsPlaying ? "暂停" : "播放";
    public string PositionLabel => FormatTime(PositionSeconds);
    public string DurationLabel => FormatTime(DurationSeconds);
    public IReadOnlySet<string> RevealedCloze => _revealed;
    public event Action? PresentationChanged;
    public bool IsScrubbing { get; set; }

    [ObservableProperty] private LessonListItem? selectedLesson;
    [ObservableProperty] private CueRow? activeCue;
    [ObservableProperty] private string title = "精听";
    [ObservableProperty] private string status = "选择课程或导入精听包";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay))] private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlayLabel))] private bool isPlaying;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PositionLabel))] private double positionSeconds;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DurationLabel))] private double durationSeconds;
    [ObservableProperty] private double seekPositionSeconds;
    [ObservableProperty] private double playbackRate = 1;
    [ObservableProperty] private double volume = 1;
    [ObservableProperty] private bool singleSentenceLoop;
    [ObservableProperty] private bool showSubtitles = true;
    [ObservableProperty] private bool showAllCloze;
    [ObservableProperty] private bool followTranscript = true;
    [ObservableProperty] private bool revealAnswer;
    [ObservableProperty] private int transcriptFontSize = 18;

    partial void OnSelectedLessonChanged(LessonListItem? value)
    {
        var item = value;
        SelectLesson(item);
    }
    private void SelectLesson(LessonListItem? item)
    {
        var generation = ++_generation;
        CurrentLoad = RunAsync(async () =>
        {
            if (generation != _generation) return;
            await CloseMediaAsync();
            if (item is null) { Title = "精听"; return; }
            _switching = true;
            try
            {
                var lesson = await _library.LoadByIdAsync(item.Id) ?? throw new IOException("课程文件缺失或完整性校验失败");
                if (generation != _generation) return;
                _lesson = lesson; _audioPath = lesson.AudioPath;
                Title = lesson.Manifest.Title;
                DurationSeconds = lesson.Manifest.Duration.TotalSeconds;
                foreach (var (cue, index) in lesson.Cues.Select((c, i) => (c, i))) Cues.Add(new(index, cue));
                var settings = await _settings.LoadAsync();
                TranscriptFontSize = settings.TranscriptFontSize;
                var saved = _progress.GetValueOrDefault(lesson.Id);
                _revealed = saved?.RevealedCloze.ToHashSet() ?? [];
                var resume = TimeSpan.FromMilliseconds(saved?.PositionMs ?? 0);
                if (saved is null && settings.SkipOpeningPrompts && lesson.Manifest.Exercises.Questions.FirstOrDefault() is { } question
                    && lesson.Manifest.Exercises.MaterialForQuestion(question.Id) is { CueIndexes.Count: > 0 } material
                    && material.CueIndexes[0] < Cues.Count)
                {
                    _preroll = material.CueIndexes[0];
                    resume = PlaybackNavigation.CueNavigationPosition(Cues[_preroll.Value].Cue);
                }
                UpdatePosition(resume);
                Status = $"{Cues.Count} 条字幕 · v{lesson.Manifest.PackageVersion}";
                await SaveProgressAsync();
                PresentationChanged?.Invoke();
            }
            finally { _switching = false; OnPropertyChanged(nameof(CanPlay)); }
        });
    }

    public async Task RefreshAsync()
    {
        try
        {
            await SaveProgressAsync();
            _progress = await _progressStore.LoadAsync();
            var id = SelectedLesson?.Id;
            var loaded = await _library.LoadAsync();
            Lessons.Clear();
            foreach (var lesson in loaded.Lessons) Lessons.Add(new(lesson));
            SelectedLesson = Lessons.FirstOrDefault(x => x.Id == id);
            await CurrentLoad;
            if (loaded.Issues.Count > 0) Status = $"{loaded.Issues.Count} 个课程无法读取";
        }
        catch (Exception ex) { Status = $"读取课库失败：{ex.Message}"; }
    }
    public Task<IlpManifest> ReadManifestAsync(string file) => _importer.ReadManifestAsync(file);
    public async Task ImportAsync(string file, bool replace = false, string? title = null)
    {
        try
        {
            var imported = await _importer.ImportFileAsync(file, replace, title);
            await RefreshAsync();
            SelectedLesson = Lessons.FirstOrDefault(x => x.Id == imported.Id);
            await CurrentLoad;
        }
        catch (Exception ex) { Status = $"导入失败：{ex.Message}"; }
    }
    public Task OpenAudioAsync(string file) => RunAsync(async () =>
    {
        await CloseMediaAsync();
        _audioPath = file;
        Title = Path.GetFileNameWithoutExtension(file);
        await EnsurePlayerLoadedAsync();
        DurationSeconds = _player!.Duration.TotalSeconds;
        UpdatePosition(TimeSpan.Zero);
        OnPropertyChanged(nameof(CanPlay));
        Status = "音频已打开";
    });

    [RelayCommand] private Task TogglePlaybackAsync() => RunAsync(async () =>
    {
        if (_audioPath is null) return;
        _repeatEnd = null;
        await EnsurePlayerLoadedAsync();
        if (_player!.IsPlaying)
        {
            await _player.PauseAsync();
            IsPlaying = false;
            await SaveProgressAsync();
        }
        else
        {
            if (PositionSeconds >= DurationSeconds) await SeekCoreAsync(TimeSpan.Zero);
            if (SingleSentenceLoop && ActiveCue is { } cue && (PositionSeconds < cue.Cue.Start.TotalSeconds || PositionSeconds >= cue.Cue.End.TotalSeconds))
                await SeekCoreAsync(PlaybackNavigation.CueSeekPosition(cue.Cue), cue.Index);
            await _player.PlayAsync();
            IsPlaying = true;
            _returnCue = null; OnPropertyChanged(nameof(CanReturn));
        }
    });
    [RelayCommand] private Task PreviousCueAsync() => StepCueAsync(-1);
    [RelayCommand] private Task NextCueAsync() => StepCueAsync(1);
    private Task StepCueAsync(int delta) => RunAsync(async () =>
    {
        if (ActiveCue is null) return;
        var row = Cues[Math.Clamp(ActiveCue.Index + delta, 0, Cues.Count - 1)];
        await SeekCoreAsync(PlaybackNavigation.CueNavigationPosition(row.Cue), row.Index);
    });
    [RelayCommand] private Task JumpCueAsync(CueRow? row) => RunAsync(async () =>
    {
        if (row is null || !Cues.Contains(row)) return;
        if (!IsPlaying && ActiveCue is not null) { _returnCue ??= ActiveCue.Index; OnPropertyChanged(nameof(CanReturn)); }
        await SeekCoreAsync(PlaybackNavigation.CueNavigationPosition(row.Cue), row.Index);
    });
    [RelayCommand] private Task ReturnCueAsync() => RunAsync(async () =>
    {
        var index = _returnCue;
        _returnCue = null; OnPropertyChanged(nameof(CanReturn));
        if (index is { } i && i < Cues.Count) await SeekCoreAsync(PlaybackNavigation.CueNavigationPosition(Cues[i].Cue), i);
    });
    [RelayCommand] private Task PreviousQuestionAsync() => StepQuestionAsync(-1);
    [RelayCommand] private Task NextQuestionAsync() => StepQuestionAsync(1);
    private Task StepQuestionAsync(int delta) => RunAsync(async () =>
    {
        var materials = _lesson?.Manifest.Exercises.EffectiveMaterials.Where(m => m.QuestionIds.Count > 0 && m.CueIndexes.Count > 0).ToArray() ?? [];
        if (materials.Length == 0) return;
        var index = Array.FindIndex(materials, m => m.CueIndexes.Contains(ActiveCue?.Index ?? -1) || m.LeadInCueIndexes.Contains(ActiveCue?.Index ?? -1));
        var target = index >= 0 ? Math.Clamp(index + delta, 0, materials.Length - 1) : delta >= 0 ? 0 : materials.Length - 1;
        var material = materials[target];
        var cue = material.LeadInCueIndexes.FirstOrDefault(material.CueIndexes[0]);
        if (cue >= 0 && cue < Cues.Count) await SeekCoreAsync(PlaybackNavigation.CueNavigationPosition(Cues[cue].Cue), cue);
    });
    [RelayCommand] private Task RepeatSentenceAsync() => RunAsync(async () =>
    {
        if (ActiveCue is not { } row) return;
        SingleSentenceLoop = false;
        await SeekCoreAsync(PlaybackNavigation.CueNavigationPosition(row.Cue), row.Index);
        _repeatEnd = row.Cue.End;
        await EnsurePlayerLoadedAsync();
        await _player!.PlayAsync(); IsPlaying = true;
    });
    public Task SeekAsync(double seconds) => RunAsync(async () =>
    {
        if (_audioPath is not null && double.IsFinite(seconds)) await SeekCoreAsync(TimeSpan.FromSeconds(seconds));
    });
    [RelayCommand] private Task ToggleLoopAsync() => RunAsync(async () =>
    {
        SingleSentenceLoop = !SingleSentenceLoop && ActiveCue is not null;
        await ConfigureLoopAsync();
    });
    partial void OnSingleSentenceLoopChanged(bool value) => _ = RunAsync(ConfigureLoopAsync);
    partial void OnPlaybackRateChanged(double value)
    {
        try { if (_player is not null) _player.PlaybackRate = value; }
        catch (Exception ex) { Status = ex.Message; }
    }
    partial void OnVolumeChanged(double value)
    {
        try { if (_player is not null) _player.Volume = value; }
        catch (Exception ex) { Status = ex.Message; }
    }
    partial void OnShowSubtitlesChanged(bool value) => PresentationChanged?.Invoke();
    partial void OnShowAllClozeChanged(bool value) => PresentationChanged?.Invoke();
    partial void OnActiveCueChanged(CueRow? value)
    {
        Questions.Clear();
        if (_lesson?.Manifest.Exercises.MaterialForCue(value?.Index ?? -1) is { } material)
            foreach (var question in _lesson.Manifest.Exercises.QuestionsForMaterial(material)) Questions.Add(question);
        RevealAnswer = false;
    }
    public bool IsClozeHidden(int cue, int word)
    {
        var material = _lesson?.Manifest.Exercises.MaterialForCue(cue);
        var show = material is not null && _revealedMaterials.Contains(material.Id) || ShowAllCloze && !(material is not null && _hiddenMaterials.Contains(material.Id));
        return _lesson?.Manifest.Exercises.ClozeWordIndexes.GetValueOrDefault(cue)?.Contains(word) == true && !show && !_revealed.Contains($"{cue}:{word}");
    }
    public async Task ToggleClozeAsync(int cue, int word)
    {
        if (_lesson?.Manifest.Exercises.ClozeWordIndexes.GetValueOrDefault(cue)?.Contains(word) != true) return;
        var key = $"{cue}:{word}";
        if (!_revealed.Add(key)) _revealed.Remove(key);
        PresentationChanged?.Invoke();
        await RunAsync(SaveProgressAsync);
    }
    public void ToggleMaterialCloze()
    {
        var material = _lesson?.Manifest.Exercises.MaterialForCue(ActiveCue?.Index ?? -1);
        if (material is null) return;
        if (ShowAllCloze) { if (!_hiddenMaterials.Add(material.Id)) _hiddenMaterials.Remove(material.Id); }
        else { if (!_revealedMaterials.Add(material.Id)) _revealedMaterials.Remove(material.Id); }
        PresentationChanged?.Invoke();
    }
    public Task PauseAsync() => RunAsync(async () =>
    {
        if (_playerSource is not null) await _player!.PauseAsync();
        IsPlaying = false; await SaveProgressAsync();
    });
    public async Task RemoveSelectedAsync()
    {
        var selected = SelectedLesson;
        if (selected is null) return;
        await RunAsync(async () =>
        {
            await CloseMediaAsync();
            await _library.RemoveAsync(selected.Id);
            _progress.Remove(selected.Id);
            await _progressStore.SaveAsync(_progress);
        });
        SelectedLesson = null; await RefreshAsync();
    }
    public async Task RemoveByIdAsync(string id)
    {
        await RunAsync(async () => { await _library.RemoveAsync(id); _progress.Remove(id); await _progressStore.SaveAsync(_progress); });
        await RefreshAsync();
    }
    private async Task SeekCoreAsync(TimeSpan target, int? index = null)
    {
        _repeatEnd = null; _preroll = index;
        UpdatePosition(target);
        if (_playerSource is not null) await _player!.SeekAsync(TimeSpan.FromSeconds(PositionSeconds));
        await ConfigureLoopAsync();
        await SaveProgressAsync();
    }
    private Task ConfigureLoopAsync() => _playerSource is null ? Task.CompletedTask : SingleSentenceLoop && ActiveCue is { } row
        ? _player!.SetLoopAsync(PlaybackNavigation.CueSeekPosition(row.Cue), row.Cue.End) : _player!.SetLoopAsync(null, null);
    private async Task EnsurePlayerLoadedAsync()
    {
        if (_audioPath is null) throw new InvalidOperationException("请先选择课程");
        if (_playerSource == _audioPath) return;
        _switching = true;
        try
        {
            if (_player is null) { _player = _factory(); _player.StateChanged += OnPlayerChanged; }
            await _player.OpenAsync(_audioPath);
            _playerSource = _audioPath;
            if (_player.Duration > TimeSpan.Zero) DurationSeconds = _player.Duration.TotalSeconds;
            await _player.SeekAsync(TimeSpan.FromSeconds(PositionSeconds));
            _player.PlaybackRate = PlaybackRate; _player.Volume = Volume;
            await ConfigureLoopAsync();
        }
        finally { _switching = false; }
    }
    private void OnPlayerChanged(object? sender, EventArgs args) => _dispatch(() =>
    {
        if (_disposed || _switching || _playerSource is null) return;
        IsPlaying = _player!.IsPlaying;
        UpdatePosition(_player.Position);
        if (_player.ErrorMessage is { } error) Status = error;
        if (_repeatEnd is { } end && _player.Position >= end)
        {
            _repeatEnd = null; _ = PauseAsync();
        }
        if (DateTimeOffset.UtcNow - _lastProgressSave >= TimeSpan.FromSeconds(5)) _ = RunAsync(SaveProgressAsync);
    });
    private void UpdatePosition(TimeSpan position)
    {
        PositionSeconds = Math.Clamp(position.TotalSeconds, 0, DurationSeconds);
        if (!IsScrubbing) SeekPositionSeconds = PositionSeconds;
        if (_preroll is { } i && i < Cues.Count && position >= Cues[i].Cue.Start) _preroll = null;
        var active = _preroll ?? PlaybackNavigation.ActiveCueIndexForPosition(_lesson?.Cues ?? [], position);
        ActiveCue = active >= 0 && active < Cues.Count ? Cues[active] : null;
    }
    private async Task SaveProgressAsync()
    {
        if (_lesson is null) return;
        _lastProgressSave = DateTimeOffset.UtcNow;
        _progress[_lesson.Id] = new(_lesson.Id, (long)(PositionSeconds * 1000), DateTimeOffset.Now, _revealed.ToHashSet());
        await _progressStore.SaveAsync(new Dictionary<string, LessonProgress>(_progress));
    }
    private async Task CloseMediaAsync()
    {
        await SaveProgressAsync();
        _switching = true;
        try
        {
            if (_playerSource is not null) { await _player!.SetLoopAsync(null, null); await _player.StopAsync(); }
            _playerSource = _audioPath = null; _lesson = null; _preroll = _returnCue = null; _repeatEnd = null;
            Cues.Clear(); Questions.Clear(); ActiveCue = null;
            SingleSentenceLoop = IsPlaying = false;
            ShowSubtitles = true; ShowAllCloze = false;
            _revealedMaterials.Clear(); _hiddenMaterials.Clear();
            PositionSeconds = SeekPositionSeconds = DurationSeconds = 0;
            OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(CanReturn));
        }
        finally { _switching = false; }
    }
    private async Task RunAsync(Func<Task> operation)
    {
        await _operations.WaitAsync();
        try { if (!_disposed) { IsBusy = true; await operation(); } }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; _operations.Release(); }
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_player is not null) _player.StateChanged -= OnPlayerChanged;
            await SaveProgressAsync();
            if (_player is not null) await _player.DisposeAsync();
        }
        finally { _operations.Release(); }
    }
}
