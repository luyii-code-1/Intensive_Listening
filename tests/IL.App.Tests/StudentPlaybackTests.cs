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
            Assert.Equal(2.5,vm.PositionSeconds);Assert.Equal(1,vm.ActiveCue!.Index);Assert.Contains("1:3",vm.RevealedCloze);Assert.Equal(0,fake.OpenCount);
            await vm.PreviousCueCommand.ExecuteAsync(null);Assert.Equal(0,vm.ActiveCue!.Index);Assert.Equal(0,fake.OpenCount);
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
            vm.SelectedLesson=vm.Lessons[0];await vm.CurrentLoad;await vm.TogglePlaybackCommand.ExecuteAsync(null);Assert.Contains("native failure",vm.Status);Assert.False(vm.IsPlaying);Assert.False(vm.IsBusy);
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class FakePlayer : IAudioPlayer
    {
        public int OpenCount;public bool FailOpen;public TimeSpan? LoopEnd;
        public TimeSpan Position{get;private set;}public TimeSpan Duration=>TimeSpan.FromSeconds(5);public bool IsPlaying{get;private set;}public string? ErrorMessage=>null;public double PlaybackRate{get;set;}=1;public double Volume{get;set;}=1;
        public event EventHandler? StateChanged;
        public Task OpenAsync(string path,CancellationToken ct=default){OpenCount++;if(FailOpen)throw new IOException("native failure");Position=TimeSpan.Zero;return Task.CompletedTask;}
        public Task PlayAsync(CancellationToken ct=default){IsPlaying=true;StateChanged?.Invoke(this,EventArgs.Empty);return Task.CompletedTask;}
        public Task PauseAsync(CancellationToken ct=default){IsPlaying=false;StateChanged?.Invoke(this,EventArgs.Empty);return Task.CompletedTask;}
        public Task StopAsync(CancellationToken ct=default){IsPlaying=false;Position=TimeSpan.Zero;return Task.CompletedTask;}
        public Task SeekAsync(TimeSpan target,CancellationToken ct=default){Position=target;return Task.CompletedTask;}
        public Task SetLoopAsync(TimeSpan? start,TimeSpan? end,CancellationToken ct=default){LoopEnd=end;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
