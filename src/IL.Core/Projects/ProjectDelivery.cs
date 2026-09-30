using IL.Core.Ilp;
namespace IL.Core.Projects;
public sealed record ProjectDeliveryResult(int PackageVersion,ImportedLesson Lesson);
public sealed class ProjectDelivery
{
    public async Task<string> CreateIlpAsync(CourseProject project,string outputFile,int? packageVersion=null,CancellationToken ct=default)
    {
        if(!project.HasAudio||!project.HasTranscript)throw new IlpException(IlpError.MissingFile,"课程需要音频和字幕才能生成精听包");
        var temp=Path.Combine(Path.GetTempPath(),"intensive-listening-delivery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{var transcript=Path.Combine(temp,"transcript.srt");await File.WriteAllTextAsync(transcript,project.Transcript,ct);await new IlpCreator().CreateAsync(project.Title,project.AudioPath!,transcript,outputFile,project.PackageUuid,packageVersion??project.PackageVersion+1,project.AudioDuration,project.Exercises,ct);return outputFile;}
        finally{Directory.Delete(temp,true);}
    }
    public async Task<ProjectDeliveryResult> AddToLibraryAsync(CourseProject project,string libraryDirectory,CancellationToken ct=default)
    {
        var temp=Path.Combine(Path.GetTempPath(),"intensive-listening-publish-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{var version=project.PackageVersion+1;var package=await CreateIlpAsync(project,Path.Combine(temp,"lesson.ilp"),version,ct);return new(version,await new IlpImporter(libraryDirectory).ImportFileAsync(package,true,ct:ct));}
        finally{Directory.Delete(temp,true);}
    }
}
