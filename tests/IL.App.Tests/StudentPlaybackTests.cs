using IL.App.Services;
using IL.App.ViewModels;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Settings;
using IL.Core.Student;
using Xunit;

namespace IL.App.Tests;

public sealed class StudentPlaybackTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OpeningAnIlpUsesTheFileVersionAndKeepsSavedProgress(int fileVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "il-open-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seedFile = Path.Combine(root, "seed.ilp");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"), seedFile);
            var library = Path.Combine(root, "library");
            var seed = await new IlpImporter(Path.Combine(root, "seed")).ImportFileAsync(seedFile);
            var creator = new IlpCreator();
            var originalFile = Path.Combine(root, "original.ilp");
            var incomingFile = Path.Combine(root, "incoming.ilp");
            await creator.CreateAsync("原课程", seed.AudioPath, seed.TranscriptPath, originalFile, seed.Id, 2);
            await creator.CreateAsync("文件中的课程", seed.AudioPath, seed.TranscriptPath, incomingFile, seed.Id, fileVersion);
            var progress = new LessonProgressStore(Path.Combine(root, "progress.json"));
            var player = new FakePlayer();
            await using var vm = new StudentViewModel(() => player, library, progress, new AppSettingsStore(Path.Combine(root, "settings.json")));

            await vm.OpenIlpFromFileAsync(originalFile);
            await vm.SeekAsync(1.25);
            await vm.TogglePlaybackCommand.ExecuteAsync(null);
            Assert.True(vm.IsPlaying);

            await vm.OpenIlpFromFileAsync(incomingFile);

            Assert.Single(vm.Lessons);
            Assert.Equal(fileVersion, vm.CurrentLesson!.Manifest.PackageVersion);
            Assert.Equal("文件中的课程", vm.Title);
            Assert.Equal(1.25, vm.PositionSeconds);
            Assert.Equal(1250L, (await progress.LoadAsync())[seed.Id].PositionMs);
            Assert.False(vm.IsPlaying);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SelectionNavigationPlaybackAndProgressUseOneState()
    {
        var root=Path.Combine(Path.GetTempPath(),"il2-student-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(AppContext.BaseDirectory,"fixtures","dart-course.ilp");var library=Path.Combine(root,"library");var lesson=await new IlpImporter(library).ImportFileAsync(source);
            var store=new LessonProgressStore(Path.Combine(root,"lesson_progress.json"));await store.SaveAsync(new Dictionary<string,LessonProgress>{{lesson.Id,new(lesson.Id,2500,null,new HashSet<string>{"1:3"})}});
            var fake=new FakePlayer();await using var vm=new StudentViewModel(()=>fake,library,store,new AppSettingsStore(Path.Combine(root,"settings.json")));
            await vm.RefreshAsync();Assert.Null(vm.SelectedLesson);vm.SelectedLesson=vm.Lessons.Single();await vm.CurrentLoad;
            Assert.Equal(2.5,vm.PositionSeconds);Assert.Equal(1,vm.ActiveCue!.Index);Assert.Contains("1:3",vm.RevealedCloze);Assert.Equal(1,fake.OpenCount);Assert.True(vm.CanPlay);
            await vm.PreviousCueCommand.ExecuteAsync(null);Assert.Equal(0,vm.ActiveCue!.Index);Assert.Equal(1,fake.OpenCount);
            await vm.TogglePlaybackCommand.ExecuteAsync(null);Assert.True(vm.IsPlaying);Assert.Equal(1,fake.OpenCount);Assert.Equal(vm.PositionSeconds,fake.Position.TotalSeconds);
            vm.SingleSentenceLoop=true;await vm.SeekAsync(1);Assert.NotNull(fake.LoopEnd);
            await vm.TogglePlaybackCommand.ExecuteAsync(null);Assert.False(vm.IsPlaying);Assert.Equal((long)(vm.PositionSeconds*1000),(await store.LoadAsync())[lesson.Id].PositionMs);
            await vm.NextCueCommand.ExecuteAsync(null);Assert.Equal(1,vm.ActiveCue!.Index);Assert.Equal(1,fake.OpenCount);
            await vm.RemoveSelectedAsync();Assert.Empty(vm.Lessons);Assert.False(vm.CanPlay);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task LatestSelectionWinsAndNativeFailureIsVisible()
    {
        var root=Path.Combine(Path.GetTempPath(),"il2-student-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(AppContext.BaseDirectory,"fixtures","dart-course.ilp");var library=Path.Combine(root,"library");await new IlpImporter(library).ImportFileAsync(source);
            var fake=new FakePlayer{FailOpen=true};await using var vm=new StudentViewModel(()=>fake,library,new LessonProgressStore(Path.Combine(root,"progress.json")),new AppSettingsStore(Path.Combine(root,"settings.json")));
            await vm.RefreshAsync();vm.SelectedLesson=vm.Lessons[0];vm.SelectedLesson=null;await vm.CurrentLoad;Assert.Empty(vm.Cues);Assert.False(vm.CanPlay);
            vm.SelectedLesson=vm.Lessons[0];await vm.CurrentLoad;Assert.Contains("native failure",vm.Status);Assert.False(vm.IsPlaying);Assert.False(vm.IsBusy);Assert.False(vm.CanPlay);
            fake.FailOpen=false;await vm.OpenLessonAsync(vm.Lessons[0]);Assert.True(vm.CanPlay);
            await vm.TogglePlaybackCommand.ExecuteAsync(null);Assert.True(vm.IsPlaying);
        }
        finally{Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData(40)]
    [InlineData(10)]
    public async Task IlpDurationRemainsOnPackageTimelineWhenPlaybackStarts(double playerMinutes)
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-student-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp");
            var library = Path.Combine(root, "library");
            var lesson = await new IlpImporter(library).ImportFileAsync(source);
            await File.WriteAllBytesAsync(Path.Combine(library, lesson.Id, "manifest.json"),
                (lesson.Manifest with { Duration = TimeSpan.FromMinutes(22) }).ToBytes());
            var fake = new FakePlayer { Duration = TimeSpan.FromMinutes(playerMinutes) };
            await using var vm = new StudentViewModel(() => fake, library,
                new LessonProgressStore(Path.Combine(root, "progress.json")),
                new AppSettingsStore(Path.Combine(root, "settings.json")));
            await vm.RefreshAsync();
            await vm.OpenLessonAsync(vm.Lessons.Single());
            Assert.Equal(22 * 60, vm.DurationSeconds);
            Assert.Equal("22:00", vm.DurationLabel);

            await vm.TogglePlaybackCommand.ExecuteAsync(null);
            Assert.Equal(22 * 60, vm.DurationSeconds);
            Assert.Equal("22:00", vm.DurationLabel);
            await vm.SeekAsync(21 * 60);
            Assert.Equal(21 * 60, vm.SeekPositionSeconds);
            Assert.Equal(22 * 60, vm.DurationSeconds);

            await vm.OpenAudioAsync(lesson.AudioPath);
            Assert.Equal(playerMinutes * 60, vm.DurationSeconds);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SentencePlaybackUsesConfirmedPlayerState()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-play-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var library = Path.Combine(root, "library");
            await new IlpImporter(library).ImportFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"));
            var fake = new FakePlayer { IgnorePlay = true };
            await using var vm = new StudentViewModel(() => fake, library, new LessonProgressStore(Path.Combine(root, "progress.json")), new AppSettingsStore(Path.Combine(root, "settings.json")));
            await vm.RefreshAsync(); await vm.OpenLessonAsync(vm.Lessons.Single());
            await vm.PlaySelectedCueAsync(vm.Cues[0], false);
            Assert.False(vm.IsPlaying);
            fake.IgnorePlay = false;
            await vm.PlaySelectedCueAsync(vm.Cues[0], false);
            Assert.True(vm.IsPlaying);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PlayerNotificationsCoalesceAndAutosaveKeepsPlaybackAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-play-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var library = Path.Combine(root, "library");
            await new IlpImporter(library).ImportFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"));
            var pending = new List<Action>(); var fake = new FakePlayer();
            await using var vm = new StudentViewModel(() => fake, library, new LessonProgressStore(Path.Combine(root, "progress.json")), new AppSettingsStore(Path.Combine(root, "settings.json")), pending.Add);
            await vm.RefreshAsync(); await vm.OpenLessonAsync(vm.Lessons.Single());
            await Task.Delay(5100);
            var busyChanges = new List<bool>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsBusy)) busyChanges.Add(vm.IsBusy); };
            for (var i = 0; i < 50; i++) fake.Notify();
            Assert.Single(pending);
            pending[0]();
            Assert.DoesNotContain(true, busyChanges);
            Assert.True(vm.CanPlay);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OldSentencePauseCannotStopNewPlaybackQueuedAheadOfIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "il-play-epoch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var library = Path.Combine(root, "library");
            await new IlpImporter(library).ImportFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"));
            var fake = new FakePlayer();
            await using var vm = new StudentViewModel(() => fake, library, new LessonProgressStore(Path.Combine(root, "progress.json")), new AppSettingsStore(Path.Combine(root, "settings.json")));
            await vm.RefreshAsync(); await vm.OpenLessonAsync(vm.Lessons.Single());
            await vm.PlaySelectedCueAsync(vm.Cues[0], true); await fake.SeekAsync(vm.Cues[0].Cue.End);
            var gate = (SemaphoreSlim)typeof(StudentViewModel).GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            await gate.WaitAsync(); Task next;
            try { next = vm.PlaySelectedCueAsync(vm.Cues[1], false); fake.Notify(); }
            finally { gate.Release(); }
            await next; await gate.WaitAsync(); gate.Release();
            Assert.True(fake.IsPlaying); Assert.True(vm.IsPlaying);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        public int OpenCount;public bool FailOpen; public bool IgnorePlay;public TimeSpan? LoopEnd;
        public TimeSpan Position{get;private set;}public TimeSpan Duration{get;set;}=TimeSpan.FromSeconds(5);public bool IsPlaying{get;private set;}public string? ErrorMessage=>null;public double PlaybackRate{get;set;}=1;public double Volume{get;set;}=1;
        public event EventHandler? StateChanged;
        public void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
        public Task OpenAsync(string path,CancellationToken ct=default){OpenCount++;if(FailOpen)throw new IOException("native failure");Position=TimeSpan.Zero;return Task.CompletedTask;}
        public Task PlayAsync(CancellationToken ct=default){IsPlaying=!IgnorePlay;StateChanged?.Invoke(this,EventArgs.Empty);return Task.CompletedTask;}
        public Task PauseAsync(CancellationToken ct=default){IsPlaying=false;StateChanged?.Invoke(this,EventArgs.Empty);return Task.CompletedTask;}
        public Task StopAsync(CancellationToken ct=default){IsPlaying=false;Position=TimeSpan.Zero;return Task.CompletedTask;}
        public Task SeekAsync(TimeSpan target,CancellationToken ct=default){Position=target;return Task.CompletedTask;}
        public Task SetLoopAsync(TimeSpan? start,TimeSpan? end,CancellationToken ct=default){LoopEnd=end;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
