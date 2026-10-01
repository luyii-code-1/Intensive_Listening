using IL.Core.Projects;
using Xunit;
namespace IL.Core.Tests;
public sealed class DartProjectCompatibilityTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"il2-dart-project-"+Guid.NewGuid().ToString("N"));
    public DartProjectCompatibilityTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    private static string Fixture(string name)=>Path.Combine(AppContext.BaseDirectory,"fixtures",name);
    [Fact] public async Task ImportsProjectExportedByOriginalDartStore()
    {
        var project=await new CourseProjectStore(root).ImportZipAsync(Fixture("dart-project.zip"));
        Assert.Equal("Dart project 工程",project.Title);Assert.Equal("22222222-2222-4222-8222-222222222222",project.PackageUuid);Assert.Equal(4,project.PackageVersion);Assert.True(project.AutomaticQuestionPlanApplied);
        Assert.Equal(CourseProjectStep.Review,project.Step);Assert.Equal(TimeSpan.FromSeconds(1),project.AudioDuration);Assert.Equal("m",Assert.Single(project.Exercises.Materials).Id);Assert.Equal(8,Assert.Single(project.Exercises.Questions).Number);Assert.Equal(1,project.Exercises.Questions[0].AnswerIndex);Assert.Equal(new[]{1},project.Exercises.ClozeWordIndexes[0]);
    }
    [Fact] public async Task LoadsOriginalV1MetadataWithStableLegacyIdentity()
    {
        var directory=Path.Combine(root,"dart-legacy-project");Directory.CreateDirectory(directory);File.Copy(Fixture("dart-project-v1.json"),Path.Combine(directory,"project.json"));
        var project=await new CourseProjectStore(root).LoadByIdAsync("dart-legacy-project");Assert.NotNull(project);Assert.Equal("64607077-2969-4360-a96a-73267c7f6165",project.PackageUuid);Assert.Equal(0,project.PackageVersion);
    }
}
