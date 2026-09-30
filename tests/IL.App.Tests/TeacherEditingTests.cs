using IL.App.ViewModels;
using IL.Core.Models;
using Xunit;

namespace IL.App.Tests;
public sealed class TeacherEditingTests
{
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
