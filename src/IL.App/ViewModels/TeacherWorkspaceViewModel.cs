using CommunityToolkit.Mvvm.ComponentModel;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Projects;

namespace IL.App.ViewModels;

public sealed class TeacherWorkspaceViewModel(CourseProjectStore store) : ObservableObject
{
    private CourseProject? _project;
    public CourseProject? Project { get => _project; private set => SetProperty(ref _project, value); }
    public IReadOnlyList<CourseProject> Projects { get; private set; } = [];
    public IReadOnlyList<SrtCue> Cues { get; private set; } = [];
    public async Task ReloadAsync() { Projects = await store.LoadAllAsync(); OnPropertyChanged(nameof(Projects)); }
    public async Task OpenAsync(string id)
    {
        Project = await store.LoadByIdAsync(id);
        ParseCues();
        if (Project is { HasTranscript: true } && Cues.Count > 0 && Project.Step < CourseProjectStep.Review)
            await SaveAsync(Project with { Step = CourseProjectStep.Review, ReviewPhase = ReviewPhase.Grouping });
        if (Project != null) UpdateProjects(Project);
        if (Project is { HasTranscript: true, AutomaticQuestionPlanApplied: false, AutoQuestionPlanDeferred: false } && Cues.Count > 0)
        {
            var exercises = Project.Exercises.Questions.Count == 0 ? SrtQuestionPlanner.Plan(Cues, Project.Exercises.ClozeWordIndexes) : Project.Exercises;
            await SaveAsync(Project with { Exercises = exercises, AutomaticQuestionPlanApplied = true });
        }
    }
    public async Task SaveAsync(CourseProject project)
    {
        if (project.TranscriptionJobId is not null && Project?.TranscriptionJobId == project.TranscriptionJobId &&
            await store.LoadByIdAsync(project.Id) is { HasTranscript: true, TranscriptionJobId: null } bound && bound.AudioPath == project.AudioPath)
            project = project with { Transcript = bound.Transcript, Step = bound.Step, ReviewPhase = bound.ReviewPhase,
                TranscriptionJobId = null, Exercises = bound.Exercises, AutomaticQuestionPlanApplied = bound.AutomaticQuestionPlanApplied, AutoQuestionPlanDeferred = bound.AutoQuestionPlanDeferred };
        project = project with { UpdatedAt = DateTimeOffset.Now };
        var parseCues = Project?.Id != project.Id || Project?.Transcript != project.Transcript || Project?.AudioDuration != project.AudioDuration;
        await store.SaveAsync(project);
        Project = project;
        if (parseCues) ParseCues();
        UpdateProjects(project);
    }
    private void UpdateProjects(CourseProject project)
    {
        Projects = Projects.Where(p => p.Id != project.Id).Append(project).OrderByDescending(p => p.UpdatedAt).ToArray();
        OnPropertyChanged(nameof(Projects));
    }
    private void ParseCues()
    {
        try { Cues = Project is { HasTranscript: true } ? SrtParser.Parse(System.Text.Encoding.UTF8.GetBytes(Project.Transcript), Project.AudioDuration ?? TimeSpan.FromDays(7)) : []; }
        catch (IlpException) { Cues = []; }
        OnPropertyChanged(nameof(Cues));
    }
    public static LessonExercises CreateMaterial(LessonExercises exercises, IEnumerable<int> selected)
    {
        var occupied = exercises.Materials.SelectMany(m => m.CueIndexes.Concat(m.LeadInCueIndexes)).ToHashSet();
        var cues = selected.Where(i => i >= 0 && !occupied.Contains(i)).Distinct().Order().ToArray();
        if (cues.Length == 0) throw new InvalidOperationException("请选择尚未归属其他材料的句子。");
        var id = Guid.NewGuid().ToString("N");
        var number = exercises.Questions.Select(q => q.Number).DefaultIfEmpty(0).Max() + 1;
        var question = new LessonQuestion("q-" + id, $"第 {number} 题", cues, [], "m-" + id, number, [], null);
        var material = new LessonMaterial(question.MaterialId, "", cues, [], [], [question.Id]);
        return exercises with { Materials = exercises.Materials.Append(material).OrderBy(m => m.CueIndexes.First()).ToArray(), Questions = exercises.Questions.Append(question).ToArray() };
    }
    public static LessonExercises AddQuestion(LessonExercises exercises, string materialId)
    {
        var material = exercises.Materials.First(m => m.Id == materialId);
        var number = exercises.Questions.Select(q => q.Number).DefaultIfEmpty(0).Max() + 1;
        var question = new LessonQuestion(Guid.NewGuid().ToString("N"), $"第 {number} 题", material.CueIndexes, material.RepeatedCueIndexes, materialId, number, [], null);
        return exercises with { Questions = exercises.Questions.Append(question).ToArray(), Materials = exercises.Materials.Select(m => m.Id == materialId ? m with { QuestionIds = m.QuestionIds.Append(question.Id).ToArray() } : m).ToArray() };
    }
    public static LessonExercises RemoveQuestion(LessonExercises exercises, string id) => exercises with
    { Questions = exercises.Questions.Where(q => q.Id != id).ToArray(), Materials = exercises.Materials.Select(m => m with { QuestionIds = m.QuestionIds.Where(q => q != id).ToArray() }).Where(m => m.QuestionIds.Count > 0).ToArray() };
    public static LessonExercises UpdateQuestion(LessonExercises exercises, LessonQuestion replacement)
    {
        replacement = replacement with { Title = string.IsNullOrWhiteSpace(replacement.Title) ? "未命名题目" : replacement.Title.Trim() };
        var old = exercises.Questions.First(q => q.Id == replacement.Id);
        if (replacement.Number < 1) throw new InvalidOperationException("题号必须大于零。");
        if (replacement.AnswerIndex is int answer && (answer < 0 || answer >= replacement.Options.Count)) throw new InvalidOperationException("答案必须对应一个选项。");
        return exercises with { Questions = exercises.Questions.Select(q => q.Id == replacement.Id ? replacement : q.Number == replacement.Number ? q with { Number = old.Number } : q).OrderBy(q => q.Number).ToArray() };
    }
    public static LessonExercises UpdateMaterial(LessonExercises exercises, LessonMaterial replacement, int cueCount)
    {
        var occupied = exercises.Materials.Where(m => m.Id != replacement.Id).SelectMany(m => m.CueIndexes.Concat(m.LeadInCueIndexes)).ToHashSet();
        if (replacement.CueIndexes.Count == 0 || replacement.CueIndexes.Concat(replacement.LeadInCueIndexes).Any(i => i < 0 || i >= cueCount || occupied.Contains(i)))
            throw new InvalidOperationException("材料必须包含有效且未归属其他材料的句子。");
        if (replacement.LeadInCueIndexes.Any(i => i >= replacement.CueIndexes.Min()) || replacement.RepeatedCueIndexes.Any(i => !replacement.CueIndexes.Contains(i)))
            throw new InvalidOperationException("题前提示应位于材料前，重复朗读应属于该材料。");
        return exercises with { Materials = exercises.Materials.Select(m => m.Id == replacement.Id ? replacement : m).ToArray(),
            Questions = exercises.Questions.Select(q => q.MaterialId == replacement.Id ? q with { CueIndexes = replacement.CueIndexes, RepeatedCueIndexes = replacement.RepeatedCueIndexes } : q).ToArray() };
    }
}
