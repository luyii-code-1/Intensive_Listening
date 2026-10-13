using IL.App.Services;
using IL.App.ViewModels;
using IL.Core.Ilp;
using IL.Core.Settings;
using IL.Core.Student;
using Xunit;

namespace IL.App.Tests;

public sealed class StudentAnswerProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "il-answer-progress-" + Guid.NewGuid().ToString("N"));
    public StudentAnswerProgressTests() => Directory.CreateDirectory(_root);
    [Fact]
    public async Task AnswersSurvivePlaybackSavesLibraryRefreshAndCourseReopening()
    {
        var library = Path.Combine(_root, "library"); var package = Path.Combine(_root, "course.ilp");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "dart-course.ilp"), package);
        var imported = await new IlpImporter(library).ImportFileAsync(package);
        var question = imported.Manifest.Exercises.Questions.First(q => q.Options.Count >= 2);
        var progress = new LessonProgressStore(Path.Combine(_root, "lesson_progress.json"));
        var player = new FakePlayer();
        await using var vm = new StudentViewModel(() => player, library, progress, new AppSettingsStore(Path.Combine(_root, "settings.json")));
        await vm.RefreshAsync(); await vm.OpenLessonAsync(vm.Lessons.Single());
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        await vm.SaveAnswerStateAsync(imported.Id, question.Id, selected: 1, revealed: true);
        await vm.PauseAsync(); await vm.RefreshAsync(preservePlayback: true);
        Assert.Equal(1, player.OpenCount);
        await vm.ReturnHomeAsync(); await vm.OpenLessonAsync(vm.Lessons.Single());
        Assert.Equal(1, vm.ProgressFor(imported.Id)!.SelectedAnswers[question.Id]);
        Assert.Contains(question.Id, vm.ProgressFor(imported.Id)!.RevealedAnswers);
        await vm.SaveAnswerStateAsync("different-course", question.Id, selected: 0);
        await vm.SaveAnswerStateAsync(imported.Id, question.Id, selected: 999);
        Assert.Equal(1, vm.ProgressFor(imported.Id)!.SelectedAnswers[question.Id]);
        var saved = (await progress.LoadAsync())[imported.Id];
        Assert.Equal(1, saved.SelectedAnswers[question.Id]); Assert.Contains(question.Id, saved.RevealedAnswers);
    }
    private sealed class FakePlayer : IAudioPlayer
    {
        public int OpenCount;
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(1);
        public bool IsPlaying { get; private set; }
        public string? ErrorMessage => null;
        public double PlaybackRate { get; set; } = 1;
        public double Volume { get; set; } = 1;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task OpenAsync(string path, CancellationToken ct = default) { OpenCount++; return Task.CompletedTask; }
        public Task PlayAsync(CancellationToken ct = default) { IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken ct = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) { Position = position; return Task.CompletedTask; }
        public Task SetLoopAsync(TimeSpan? start, TimeSpan? end, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
