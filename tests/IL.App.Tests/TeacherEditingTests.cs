using IL.App.ViewModels;
using IL.Core.Models;
using IL.Core.Projects;
using IL.Core.Transcription;
using Xunit;

namespace IL.App.Tests;
public sealed class TeacherEditingTests
{
    [Fact]
    public async Task ReviewSaveRevealsDeliveryAndEditingRequiresAnotherSaveAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-review-save-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CourseProjectStore(root); var vm = new TeacherWorkspaceViewModel(store);
            var project = (await store.CreateAsync()) with
            {
                AudioPath = "fixture.wav", Transcript = "1\n00:00:00,000 --> 00:00:01,000\nHello.\n",
                Step = CourseProjectStep.Review, AutomaticQuestionPlanApplied = true
            };
            await store.SaveAsync(project); await vm.OpenAsync(project.Id);
            Assert.False(vm.CanDeliver);
            await vm.SaveReviewAsync(); Assert.True(vm.CanDeliver);
            await vm.SaveEditingAsync(vm.Project! with { Title = "Edited title" });
            Assert.False(vm.CanDeliver);
            await vm.OpenAsync(project.Id); Assert.False(vm.CanDeliver);
            await vm.SaveReviewAsync(); Assert.True(vm.CanDeliver);
            var exercises = TeacherWorkspaceViewModel.CreateMaterial(vm.Project!.Exercises, [0]);
            await vm.SaveEditingAsync(vm.Project with { Exercises = exercises }); Assert.False(vm.CanDeliver);
            await vm.SaveReviewAsync(); Assert.True(vm.CanDeliver);
            await vm.SaveEditingAsync(vm.Project! with { Transcript = "invalid subtitles" });
            await Assert.ThrowsAsync<IL.Core.Ilp.IlpException>(vm.SaveReviewAsync);
            Assert.False(vm.CanDeliver);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task BackgroundCompletionAndTitleAutosaveKeepBothResultsWhenConcurrent()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-review-concurrent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CourseProjectStore(Path.Combine(root, "projects"));
            await using var queue = new TranscriptionQueue((_, _, _, _) => throw new InvalidOperationException("No ASR calls"),
                _ => Task.FromResult<DuplicateMatch?>(null), new QueueStore(Path.Combine(root, "queue.json")), new SrtRecognitionCache(Path.Combine(root, "cache")), () => "test");
            await using var binding = new ProjectTranscriptionBinding(store, queue);
            for (var i = 0; i < 12; i++)
            {
                var pending = (await store.CreateAsync()) with { Step = CourseProjectStep.Transcription, TranscriptionJobId = "queued-" + i };
                await store.SaveAsync(pending); var vm = new TeacherWorkspaceViewModel(store); await vm.OpenAsync(pending.Id);
                var job = new TranscriptionJob(pending.TranscriptionJobId!, "Recognition", "fixture.wav", TranscriptionJobStatus.Completed,
                    TranscriptionStage.Formatting, "done", DateTimeOffset.Now, ProjectId: pending.Id, Srt: "1\n00:00:00,000 --> 00:00:01,000\nHello.\n");
                var tasks = new Func<Task>[] { async () => { await binding.BindAsync(job); }, () => vm.SaveAsync(pending with { Title = "Edited title" }) };
                await Task.WhenAll((i % 2 == 0 ? tasks : tasks.Reverse()).Select(action => Task.Run(action)));
                var saved = await store.LoadByIdAsync(pending.Id);
                Assert.Equal("Edited title", saved!.Title); Assert.Equal(job.Srt, saved.Transcript);
                Assert.Equal(CourseProjectStep.Review, saved.Step); Assert.Null(saved.TranscriptionJobId);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ValidImportedSubtitlesAdvanceStaleStepAndCompletedProjectsRemainCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-review-step-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CourseProjectStore(root); var vm = new TeacherWorkspaceViewModel(store);
            var project = (await store.CreateAsync()) with { Transcript = "1\n00:00:00,000 --> 00:00:01,000\nHello.\n", Step = CourseProjectStep.Transcription };
            await store.SaveAsync(project); await vm.OpenAsync(project.Id);
            Assert.Equal(CourseProjectStep.Review, vm.Project!.Step); Assert.Single(vm.Cues);
            Assert.Equal(CourseProjectStep.Review, (await store.LoadByIdAsync(project.Id))!.Step);
            await store.SaveAsync(vm.Project with { Step = CourseProjectStep.Completed }); await vm.OpenAsync(project.Id);
            Assert.Equal(CourseProjectStep.Completed, vm.Project!.Step);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task TitleSaveFromPendingViewPreservesBackgroundBoundSubtitles()
    {
        var root = Path.Combine(Path.GetTempPath(), "il2-review-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CourseProjectStore(root); var vm = new TeacherWorkspaceViewModel(store);
            var pending = (await store.CreateAsync()) with { Step = CourseProjectStep.Transcription, TranscriptionJobId = "queued" };
            await store.SaveAsync(pending); await vm.OpenAsync(pending.Id);
            var completed = pending with { Transcript = "1\n00:00:00,000 --> 00:00:01,000\nHello.\n", TranscriptionJobId = null, Step = CourseProjectStep.Review };
            await store.SaveAsync(completed); await vm.SaveAsync(pending with { Title = "Edited while transcribing" });
            var saved = await store.LoadByIdAsync(pending.Id);
            Assert.Equal("Edited while transcribing", saved!.Title); Assert.Equal(completed.Transcript, saved.Transcript);
            Assert.Equal(CourseProjectStep.Review, saved.Step); Assert.Null(saved.TranscriptionJobId);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void CreatingMaterialsKeepsExistingAssignmentAndRejectsOccupiedSelection()
    {
        var first = TeacherWorkspaceViewModel.CreateMaterial(LessonExercises.Empty, [0, 1]);
        var second = TeacherWorkspaceViewModel.CreateMaterial(first, [1, 2]);
        Assert.Equal(new[] { 2 }, second.Materials[1].CueIndexes);
        Assert.Throws<InvalidOperationException>(() => TeacherWorkspaceViewModel.CreateMaterial(first, [0, 1]));
    }
    [Fact]
    public void RenumberingSwapsNumbersAndLastQuestionRemovalReleasesMaterial()
    {
        var source = TeacherWorkspaceViewModel.CreateMaterial(LessonExercises.Empty, [0]);
        source = TeacherWorkspaceViewModel.AddQuestion(source, source.Materials[0].Id);
        var result = TeacherWorkspaceViewModel.UpdateQuestion(source, source.Questions[0] with { Number = 2 });
        Assert.Equal(source.Questions[1].Id, result.Questions[0].Id);
        result = TeacherWorkspaceViewModel.RemoveQuestion(result, result.Questions[0].Id);
        Assert.Single(result.Materials);
        result = TeacherWorkspaceViewModel.RemoveQuestion(result, result.Questions[0].Id);
        Assert.Empty(result.Materials);
    }
    [Fact]
    public void MaterialEditingProtectsCueOwnershipAndLeadInOrder()
    {
        var source = TeacherWorkspaceViewModel.CreateMaterial(LessonExercises.Empty, [2, 3]);
        source = TeacherWorkspaceViewModel.CreateMaterial(source, [5]);
        Assert.Throws<InvalidOperationException>(() => TeacherWorkspaceViewModel.UpdateMaterial(source, source.Materials[0] with { CueIndexes = [5] }, 8));
        Assert.Throws<InvalidOperationException>(() => TeacherWorkspaceViewModel.UpdateMaterial(source, source.Materials[0] with { LeadInCueIndexes = [4] }, 8));
        Assert.Throws<InvalidOperationException>(() => TeacherWorkspaceViewModel.UpdateMaterial(source, source.Materials[0] with { RepeatedCueIndexes = [7] }, 8));
        var changed = TeacherWorkspaceViewModel.UpdateMaterial(source, source.Materials[0] with { CueIndexes = [2, 3, 4], RepeatedCueIndexes = [4], LeadInCueIndexes = [1] }, 8);
        Assert.Equal(new[] { 4 }, changed.Questions[0].RepeatedCueIndexes);
    }
    [Fact]
    public void AnswerMustReferenceAnExistingOption()
    {
        var source = TeacherWorkspaceViewModel.CreateMaterial(LessonExercises.Empty, [0]);
        Assert.Throws<InvalidOperationException>(() => TeacherWorkspaceViewModel.UpdateQuestion(source, source.Questions[0] with { Options = ["A"], AnswerIndex = 1 }));
    }
}
