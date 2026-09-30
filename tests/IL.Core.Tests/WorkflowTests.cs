using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using IL.Core.Asr;
using IL.Core.Audio;
using IL.Core.Documents;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Playback;
using IL.Core.Projects;
using IL.Core.Transcription;
using Xunit;
namespace IL.Core.Tests;
public sealed class WorkflowTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"il2-workflows-"+Guid.NewGuid().ToString("N"));
    public WorkflowTests()=>Directory.CreateDirectory(root);
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private static SrtCue Cue(string text,int start,int duration=2)=>new(TimeSpan.FromSeconds(start),TimeSpan.FromSeconds(start+duration),text);
    [Fact] public void ParsesSilencesAndChoosesBoundaries()
    {
        const string log="Duration: 00:04:10.000\nsilence_start: 99.8\nsilence_end: 100.6\nsilence_start: 199.7\nsilence_end: 200.5";
        var duration=VadSlicer.ParseDuration(log)!.Value;var silences=VadSlicer.ParseSilenceRanges(log,duration);
        Assert.Equal(TimeSpan.FromSeconds(250),duration);Assert.Equal(TimeSpan.FromMilliseconds(100200),silences[0].Midpoint);
        Assert.Equal(new[]{TimeSpan.FromMilliseconds(100200),TimeSpan.FromMilliseconds(200100)},VadSlicer.ChooseCutPoints(duration,silences));
        Assert.Throws<NoSilenceCutException>(()=>VadSlicer.ChooseCutPoints(TimeSpan.FromSeconds(121),[]));
        Assert.Equal(TimeSpan.FromSeconds(120),Assert.Single(VadSlicer.ChooseCutPoints(TimeSpan.FromSeconds(121),[],forceCutOnNoSilence:true)));
        Assert.Equal(TimeSpan.FromSeconds(120),Assert.Single(VadSlicer.ChooseCutPoints(TimeSpan.FromSeconds(121),[new(TimeSpan.FromMilliseconds(119800),TimeSpan.FromMilliseconds(120400))])));
    }
    [Fact] public void PlannerKeepsNarrationAndGroupsSharedQuestions()
    {
        var cues=new[]{Cue("听下面的录音，回答第6和第7小题。",0),Cue("TextXXX",2),Cue("TextXXX Where are they going?",4),Cue("They are going to school.",6)};
        var plan=SrtQuestionPlanner.Plan(cues);var material=Assert.Single(plan.Materials);
        Assert.Equal(new[]{6,7},plan.Questions.Select(q=>q.Number));Assert.Equal(new[]{2,3},material.CueIndexes);Assert.Equal(new[]{1},material.LeadInCueIndexes);
        Assert.Null(plan.MaterialForCue(0));Assert.Equal(material,plan.MaterialForCue(1));
    }
    [Theory]
    [InlineData("听下面的录音，回答第1 2 - 1 3题。",new[]{12,13})]
    [InlineData("听下面的录音，回答第１４至１５题。",new[]{14,15})]
    [InlineData("听下面的录音，回答第1 4~1 7小题。",new[]{14,15,16,17})]
    [InlineData("听下面的录音，回答第十六和第十七小题。",new[]{16,17})]
    public void PlannerNormalizesQuestionNumbers(string text,int[] expected)=>Assert.Equal(expected,SrtQuestionPlanner.QuestionNumbers(text));
    [Fact] public void PlannerDetectsRepeatedReadingAndPreservesCloze()
    {
        var cues=new[]{Cue("We are going to school today.",0),Cue("We will meet at the bus stop.",2),Cue("We are going to school today.",5),Cue("We will meet at the bus stop.",7)};
        var cloze=new Dictionary<int,int[]>{{0,[3]}};var plan=SrtQuestionPlanner.Plan(cues,cloze);
        Assert.Equal(new[]{2,3},Assert.Single(plan.Materials).RepeatedCueIndexes);Assert.Same(cloze,plan.ClozeWordIndexes);
    }
    [Fact] public void ClozeMirrorsLcsAlignmentInLegacyQuestion()
    {
        var cues=new[]{Cue("The same way when I first read it, the more I thought.",1,1),Cue("I felt the same way when I first read it, the more I thought.",3,1)};
        var exercise=new LessonExercises([],[new("q","Question",[0,1],[1],"",1,[],null)],new Dictionary<int,int[]>());
        var selected=ClozeSync.Toggle(new Dictionary<int,int[]>(),cues,exercise,0,11);Assert.Equal(new[]{11},selected[0]);Assert.Equal(new[]{13},selected[1]);
        Assert.Empty(ClozeSync.Toggle(selected,cues,exercise,1,13));
    }
    [Fact] public void SectionsAndTokenizerRetainOriginalIndices()
    {
        var sections=SrtTranscriptStructure.FromCues([Cue("Opening",0),Cue("Text1",2),Cue("Don't re-read now!",4),Cue("Text 2:",6),Cue("Another",8)]);
        Assert.Equal(new[]{"题前原文","Text 1","Text 2"},sections.Sections.Select(s=>s.Label));Assert.Equal(new[]{1,3},sections.MarkerCueIndexes.Order());Assert.Equal(1,sections.SectionIndexForCue(2));
        var parts=LessonTextTokenizer.Tokenize("Don't re-read now!");Assert.Equal("Don't re-read now!",string.Concat(parts.Select(p=>p.Text)));Assert.Equal(new[]{"Don't","re-read","now"},parts.Where(p=>p.IsWord).Select(p=>p.Text));
    }
    [Fact] public void PlaybackRetainsFinishedSentenceThroughGap()
    {
        var cues=new[]{Cue("First",1,1),Cue("Second",3,1)};
        Assert.Equal(0,PlaybackNavigation.ActiveCueIndexForPosition(cues,TimeSpan.FromMilliseconds(2500)));Assert.Equal(1,PlaybackNavigation.ActiveCueIndexForPosition(cues,TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromMilliseconds(1050),PlaybackNavigation.CueSeekPosition(cues[0]));Assert.Equal(TimeSpan.FromMilliseconds(800),PlaybackNavigation.CueNavigationPosition(cues[0]));
        Assert.Equal(-1,PlaybackNavigation.ActiveCueIndexForPosition([],TimeSpan.Zero));
    }
    [Fact] public void DashScopeFormatsWordsAndHandlesEmptyResponse()
    {
        var body="""{"output":{"output":{"sentence":[{"end_time":2200,"words":[{"text":"Good","begin_time":0,"end_time":500},{"text":"morning","begin_time":500,"end_time":1100,"punctuation":"."},{"text":"Listen","begin_time":1200,"end_time":1700},{"text":"carefully","begin_time":1700,"end_time":2200,"punctuation":"!"}]}]}}}""";
        var result=AsrTranscription.FromDashScopeResponseBody(body);Assert.Equal(new[]{"Good morning.","Listen carefully!"},result.Cues.Select(c=>c.Text));Assert.Equal(TimeSpan.FromMilliseconds(1200),result.Cues[1].Start);
        Assert.Equal("",AsrTranscription.FromDashScopeResponseBody("{\"output\":{\"sentence\":[]}}").ToSrt());
        Assert.Throws<FormatException>(()=>AsrTranscription.FromResponseBody("{\"text\":\"Hello\"}"));
        Assert.Equal("Nested",Assert.Single(AsrTranscription.FromResponseBody("{\"output\":{\"items\":[{\"start\":0,\"end\":1,\"text\":\"Nested\"}]}}").Cues).Text);
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> callback):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>callback(request,token);}
    [Fact] public async Task AsrSendsNativePayloadAndNoWordsCompletes()
    {
        var audio=Path.Combine(root,"chunk.wav");await File.WriteAllBytesAsync(audio,"RIFF"u8.ToArray());
        using var http=new HttpClient(new Handler(async(request,ct)=>
        {
            Assert.Equal("/api/asr",request.RequestUri!.AbsolutePath);Assert.Equal("Bearer key",request.Headers.Authorization!.ToString());
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var json=doc.RootElement;
            Assert.Equal("en",json.GetProperty("parameters").GetProperty("language").GetString());
            Assert.Equal("data:audio/wav;base64,UklGRg==",json.GetProperty("input").GetProperty("messages")[0].GetProperty("content")[0].GetProperty("audio").GetString());
            return new(HttpStatusCode.BadRequest){Content=new StringContent("{\"code\":\"ASR_RESPONSE_HAVE_NO_WORDS\"}")};
        }));
        Assert.Equal("",await new AsrClient(http).TranscribeToSrtAsync(new("https://example.test/base","/api/asr","model","key"),audio));
    }
    [Fact] public async Task AsrDeadlineCoversResponseBodyAndPreservesCancellation()
    {
        var audio=Path.Combine(root,"chunk.wav");await File.WriteAllTextAsync(audio,"audio");
        using var http=new HttpClient(new Handler(async(_,ct)=>{await Task.Delay(Timeout.Infinite,ct);return new(HttpStatusCode.OK);}));
        var config=new AsrConfig("https://example.test","/asr","model","key");
        var error=await Assert.ThrowsAsync<AsrException>(()=>new AsrClient(http,TimeSpan.FromMilliseconds(30)).TranscribeToSrtAsync(config,audio));Assert.Contains("超时",error.Message);
        using var cancellation=new CancellationTokenSource(30);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new AsrClient(http).TranscribeToSrtAsync(config,audio,ct:cancellation.Token));
    }
    private static byte[] Docx()
    {
        using var bytes=new MemoryStream();using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        {
            void Add(string name,string content){using var text=new StreamWriter(zip.CreateEntry(name).Open());text.Write(content);}
            Add("word/document.xml","""<w:document xmlns:w="urn:word"><w:body><w:p><w:r><w:t>Listening Test</w:t></w:r></w:p><w:p><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>First</w:t></w:r></w:p><w:p><w:pPr><w:pStyle w:val="Derived"/></w:pPr><w:r><w:t>Second</w:t></w:r><w:del><w:r><w:t>Deleted</w:t></w:r></w:del></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Option A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Option B</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:r><w:t>Line one</w:t><w:br/><w:t>Line two</w:t><w:tab/><w:t>Answer</w:t></w:r></w:p></w:body></w:document>""");
            Add("word/numbering.xml","""<w:numbering xmlns:w="urn:word"><w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>""");
            Add("word/styles.xml","""<w:styles xmlns:w="urn:word"><w:style w:styleId="Base"><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr></w:style><w:style w:styleId="Derived"><w:basedOn w:val="Base"/></w:style></w:styles>""");
        }return bytes.ToArray();
    }
    [Fact] public void DocxPreservesNumberingTablesBreaksAndVisibleText()
    {
        var result=new DocxTextExtractor().ExtractBytes(Docx());Assert.Equal("Listening Test\n1. First\n2. Second\nOption A\tOption B\nLine one\nLine two\tAnswer",result.Text);Assert.Equal(6,result.ParagraphCount);Assert.Equal(1,result.TableCount);
        Assert.Throws<DocxTextExtractionException>(()=>new DocxTextExtractor().ExtractBytes([1,2,3]));
    }
    [Fact] public async Task ProjectRoundTripPreservesIdentityExercisesAndDocx()
    {
        var audio=Path.Combine(root,"lesson.wav");await File.WriteAllTextAsync(audio,"audio");var source=new CourseProjectStore(Path.Combine(root,"projects"));var project=await source.CreateAsync(audio);
        project=project with{Transcript="1\n00:00:00,000 --> 00:00:01,000\nHello.\n",Step=CourseProjectStep.Review,Exercises=new([],[],new Dictionary<int,int[]>{{0,[0]}}),PackageVersion=3,TranscriptionJobId="job",LastExportPath="old.ilp"};await source.SaveAsync(project);
        var docx=Path.Combine(root,"exam.docx");await File.WriteAllBytesAsync(docx,Docx());var exam=await source.ImportExamDocumentAsync(project,docx);project=exam.Project;
        var archive=Path.Combine(root,"project.zip");await source.ExportZipToFileAsync(project,archive);var target=new CourseProjectStore(Path.Combine(root,"imported"));var imported=await target.ImportZipAsync(archive);
        Assert.NotEqual(project.Id,imported.Id);Assert.Equal(project.PackageUuid,imported.PackageUuid);Assert.Equal(project.Transcript,imported.Transcript);Assert.Equal(3,imported.PackageVersion);Assert.Null(imported.TranscriptionJobId);Assert.Null(imported.LastExportPath);Assert.True(File.Exists(imported.AudioPath));Assert.Equal(new[]{0},imported.Exercises.ClozeWordIndexes[0]);
        Assert.Equal(exam.Text,(await target.ReadExamDocumentAsync(imported))!.Text);Assert.Equal(imported.Id,Assert.Single(await target.LoadAllAsync()).Id);
        var result=await new ProjectDelivery().AddToLibraryAsync(imported,Path.Combine(root,"library"));Assert.Equal(4,result.PackageVersion);Assert.Equal(project.PackageUuid,result.Lesson.Id);
    }
    [Fact] public async Task ProjectRejectsZipTraversalAndCleansPartialImport()
    {
        var store=new CourseProjectStore(Path.Combine(root,"projects"));var p=await store.CreateAsync();var data=await store.ExportZipAsync(p);var archive=Path.Combine(root,"attack.zip");await File.WriteAllBytesAsync(archive,data);
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Update)){using var writer=new StreamWriter(zip.CreateEntry("project/../escaped.txt").Open());writer.Write("bad");}
        var target=new CourseProjectStore(Path.Combine(root,"imported"));await Assert.ThrowsAsync<CourseProjectArchiveException>(()=>target.ImportZipAsync(archive));Assert.Empty(await target.LoadAllAsync());Assert.False(File.Exists(Path.Combine(root,"escaped.txt")));
    }
}
