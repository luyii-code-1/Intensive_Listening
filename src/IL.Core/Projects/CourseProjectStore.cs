using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IL.Core.Documents;
using IL.Core.Infrastructure;
using IL.Core.Models;
namespace IL.Core.Projects;
public sealed class CourseProjectStore(string? root=null)
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    private readonly SemaphoreSlim _io = new(1, 1);
    public string RootDirectory=>root??Path.Combine(AppDirectories.DataDirectory(),"projects");
    private string DirectoryFor(string id)
    {
        if(string.IsNullOrWhiteSpace(id)||id is "." or ".."||id!=Path.GetFileName(id)||id.IndexOfAny(['/', '\\', ':'])>=0)throw new ArgumentException("项目 ID 无效",nameof(id));
        return Path.Combine(RootDirectory,id);
    }
    private static string NewId()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()+"-"+Guid.NewGuid().ToString("N")[..8];
    public async Task<IReadOnlyList<CourseProject>> LoadAllAsync(CancellationToken ct=default)
    {
        await _io.WaitAsync(ct); try
        {
        if(!Directory.Exists(RootDirectory))return [];
        var projects=new List<CourseProject>();foreach(var directory in Directory.GetDirectories(RootDirectory)){var project=await LoadDirectoryAsync(directory,ct);if(project!=null)projects.Add(project);}return projects.OrderByDescending(p=>p.UpdatedAt).ToArray();
        }
        finally { _io.Release(); }
    }
    public async Task<CourseProject?> LoadByIdAsync(string id,CancellationToken ct=default)
    {
        await _io.WaitAsync(ct); try { return await LoadDirectoryAsync(DirectoryFor(id),ct); }
        catch(ArgumentException) { return null; }
        finally { _io.Release(); }
    }
    public async Task<CourseProject> CreateAsync(string? audioPath=null,CancellationToken ct=default)
    {
        var now=DateTimeOffset.Now;var project=new CourseProject(NewId(),audioPath==null?"未命名项目":Path.GetFileNameWithoutExtension(audioPath),now,now,CourseProjectStep.Audio,Guid.NewGuid().ToString());
        await SaveAsync(project,ct);return audioPath==null?project:await BindAudioAsync(project,audioPath,ct:ct);
    }
    public async Task<CourseProject> BindAudioAsync(CourseProject project,string source,TimeSpan? duration=null,CancellationToken ct=default)
    {
        var directory=DirectoryFor(project.Id);Directory.CreateDirectory(directory);var destination=Path.Combine(directory,"audio"+Path.GetExtension(source).ToLowerInvariant());
        if(Path.GetFullPath(source)!=Path.GetFullPath(destination)){await using var input=File.OpenRead(source);await using var output=File.Create(destination);await input.CopyToAsync(output,ct);}
        var updated=project with{Title=project.Title=="未命名项目"?Path.GetFileNameWithoutExtension(source):project.Title,AudioPath=destination,AudioDuration=duration,Step=CourseProjectStep.Transcription,TranscriptionJobId=null,Transcript="",ReviewPhase=ReviewPhase.Grouping,Exercises=LessonExercises.Empty,AutomaticQuestionPlanApplied=false,AutoQuestionPlanDeferred=false,UpdatedAt=DateTimeOffset.Now};
        await SaveAsync(updated,ct);return updated;
    }
    public async Task SaveAsync(CourseProject project,CancellationToken ct=default)
    {
        await _io.WaitAsync(ct); try { await SaveCoreAsync(project, ct); } finally { _io.Release(); }
    }
    public async Task<CourseProject?> UpdateAsync(string id, Func<CourseProject?, CourseProject?> update, CancellationToken ct=default)
    {
        await _io.WaitAsync(ct);
        try
        {
            var project = update(await LoadDirectoryAsync(DirectoryFor(id), ct));
            if (project != null) await SaveCoreAsync(project, ct);
            return project;
        }
        finally { _io.Release(); }
    }
    private async Task SaveCoreAsync(CourseProject project,CancellationToken ct)
    {
        var directory=DirectoryFor(project.Id);Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"transcript.srt"),project.Transcript,new UTF8Encoding(false),ct);
        await File.WriteAllTextAsync(Path.Combine(directory,"project.json"),JsonSerializer.Serialize(project.ToMetadata(),JsonOptions),ct);
    }
    private static async Task<CourseProject?> LoadDirectoryAsync(string directory,CancellationToken ct)
    {
        try
        {
            var metadata=Path.Combine(directory,"project.json");if(!File.Exists(metadata))return null;
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(metadata,ct));var value=json.RootElement;
            if(!value.TryGetProperty("version",out var version)||version.GetInt32() is not (1 or 2))return null;
            var transcript=Path.Combine(directory,"transcript.srt");return CourseProject.FromMetadata(value,File.Exists(transcript)?await File.ReadAllTextAsync(transcript,ct):"");
        }
        catch(Exception ex)when(ex is IOException or JsonException or FormatException or InvalidOperationException or ArgumentException){return null;}
    }
    public Task DeleteAsync(string id,CancellationToken ct=default){ct.ThrowIfCancellationRequested();var directory=DirectoryFor(id);if(Directory.Exists(directory))Directory.Delete(directory,true);return Task.CompletedTask;}
    public async Task<byte[]> ExportZipAsync(CourseProject project,CancellationToken ct=default)
    {
        using var output=new MemoryStream();await WriteZipAsync(project,output,ct);return output.ToArray();
    }
    public async Task<string> ExportZipToFileAsync(CourseProject project,string output,CancellationToken ct=default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);await using var file=File.Create(output);await WriteZipAsync(project,file,ct);return output;
    }
    private async Task WriteZipAsync(CourseProject project,Stream output,CancellationToken ct)
    {
        var directory=DirectoryFor(project.Id);if(!Directory.Exists(directory))throw new CourseProjectArchiveException("工程目录不存在");
        using var zip=new ZipArchive(output,ZipArchiveMode.Create,leaveOpen:true);var metadata=project.ToMetadata();metadata["audioPath"]=project.AudioPath==null?null:Path.GetFileName(project.AudioPath);
        async Task AddText(string name,string text){await using var entry=zip.CreateEntry(name).Open();await entry.WriteAsync(Encoding.UTF8.GetBytes(text),ct);}
        await AddText("project-export.json",JsonSerializer.Serialize(new{format="intensive-listening-project",version=1,exportedAt=DateTimeOffset.UtcNow},JsonOptions));
        await AddText("project/project.json",JsonSerializer.Serialize(metadata,JsonOptions));
        await AddText("project/transcript.srt",project.Transcript);
        foreach(var file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))
        {
            var relative=Path.GetRelativePath(directory,file).Replace('\\','/');if(relative is "project.json" or "transcript.srt")continue;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)continue;
            await using var source=File.OpenRead(file);await using var entry=zip.CreateEntry("project/"+relative).Open();await source.CopyToAsync(entry,ct);
        }
    }
    public async Task<CourseProject> ImportZipAsync(string source,CancellationToken ct=default)
    {
        var destination=DirectoryFor(NewId());
        try
        {
            using var zip=ZipFile.OpenRead(source);var descriptor=zip.GetEntry("project-export.json")??throw new CourseProjectArchiveException("ZIP 中缺少工程描述文件");
            var metadataEntry=zip.GetEntry("project/project.json")??throw new CourseProjectArchiveException("ZIP 中缺少工程描述文件");
            using var descriptorStream=descriptor.Open();using var descriptorJson=await JsonDocument.ParseAsync(descriptorStream,cancellationToken:ct);
            var desc=descriptorJson.RootElement;if(!desc.TryGetProperty("format",out var format)||format.GetString()!="intensive-listening-project"||!desc.TryGetProperty("version",out var version)||version.GetInt32()!=1)throw new CourseProjectArchiveException("工程 ZIP 版本不受支持");
            using var metadataStream=metadataEntry.Open();using var metadata=await JsonDocument.ParseAsync(metadataStream,cancellationToken:ct);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);Directory.CreateDirectory(destination);
            foreach(var entry in zip.Entries)
            {
                if(!entry.FullName.StartsWith("project/",StringComparison.Ordinal)||entry.FullName=="project/project.json")continue;
                var relative=entry.FullName[8..];if(relative.Length==0)continue;
                if(relative.Contains('\\')||relative.Contains(':')||relative.StartsWith('/')||relative.Split('/').Any(p=>p is ".." or ".")||((entry.ExternalAttributes>>16)&0xf000)==0xa000)throw new CourseProjectArchiveException("工程 ZIP 包含不安全路径");
                var output=Path.GetFullPath(Path.Combine(destination,relative));if(!output.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!seen.Add(output))throw new CourseProjectArchiveException("工程 ZIP 包含不安全路径");
                if(entry.Name.Length==0){Directory.CreateDirectory(output);continue;}Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input=entry.Open();await using var file=File.Create(output);await input.CopyToAsync(file,ct);
            }
            var transcriptFile=Path.Combine(destination,"transcript.srt");var project=CourseProject.FromMetadata(metadata.RootElement,File.Exists(transcriptFile)?await File.ReadAllTextAsync(transcriptFile,ct):"");
            var now=DateTimeOffset.Now;string? audio=null;if(project.HasAudio){audio=Path.Combine(destination,Path.GetFileName(project.AudioPath!));if(!File.Exists(audio))throw new CourseProjectArchiveException("工程 ZIP 缺少绑定的音频文件");}
            project=project with{Id=Path.GetFileName(destination),CreatedAt=now,UpdatedAt=now,AudioPath=audio,TranscriptionJobId=null,LastExportPath=null};await SaveAsync(project,ct);return project;
        }
        catch(Exception ex)
        {
            if(Directory.Exists(destination))Directory.Delete(destination,true);
            if(ex is CourseProjectArchiveException or OperationCanceledException)throw;
            throw new CourseProjectArchiveException("工程 ZIP 已损坏或格式无效",ex);
        }
    }
    public async Task<CourseProjectExamDocument> ImportExamDocumentAsync(CourseProject project,string source,CancellationToken ct=default)
    {
        if(!string.Equals(Path.GetExtension(source),".docx",StringComparison.OrdinalIgnoreCase))throw new CourseProjectDocumentException("请选择 DOCX 试卷文件");
        var bytes=await File.ReadAllBytesAsync(source,ct);DocxTextResult result;
        try{result=await Task.Run(()=>new DocxTextExtractor().ExtractBytes(bytes),ct);}catch(DocxTextExtractionException ex){throw new CourseProjectDocumentException(ex.Message,ex);}
        var directory=DirectoryFor(project.Id);Directory.CreateDirectory(directory);var document=Path.Combine(directory,"exam.docx");var text=Path.Combine(directory,"exam.txt");
        var sha=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();var now=DateTimeOffset.Now;
        await File.WriteAllBytesAsync(document,bytes,ct);await File.WriteAllTextAsync(text,result.Text,ct);
        await File.WriteAllTextAsync(Path.Combine(directory,"exam.json"),JsonSerializer.Serialize(new{version=1,sourceName=Path.GetFileName(source),sha256=sha,importedAt=now.ToUniversalTime(),paragraphCount=result.ParagraphCount,tableCount=result.TableCount},JsonOptions),ct);
        var updated=project with{UpdatedAt=now};await SaveAsync(updated,ct);return new(updated,Path.GetFileName(source),document,text,result.Text,sha,result.ParagraphCount,result.TableCount);
    }
    public async Task<CourseProjectExamDocument?> ReadExamDocumentAsync(CourseProject project,CancellationToken ct=default)
    {
        var directory=DirectoryFor(project.Id);var document=Path.Combine(directory,"exam.docx");var text=Path.Combine(directory,"exam.txt");if(!File.Exists(document)||!File.Exists(text))return null;
        var source="exam.docx";var sha="";var paragraphs=0;var tables=0;
        try{using var metadata=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"exam.json"),ct));var m=metadata.RootElement;source=m.GetProperty("sourceName").GetString()??source;sha=m.GetProperty("sha256").GetString()??"";paragraphs=m.GetProperty("paragraphCount").GetInt32();tables=m.GetProperty("tableCount").GetInt32();}catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException){}
        return new(project,source,document,text,await File.ReadAllTextAsync(text,ct),sha,paragraphs,tables);
    }
}
