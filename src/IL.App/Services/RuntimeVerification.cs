using System.Text.Json;
using IL.Core.Audio;
using IL.Core.Ilp;
using IL.Core.Models;
using IL.Core.Projects;

namespace IL.App.Services;

public static class RuntimeVerification
{
    public static async Task<int> RunAsync(string output)
    {
        var root=Path.Combine(Path.GetTempPath(),"il2-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var checks=new List<string>();Exception? error=null;var engine="";var stage="initialization";File.WriteAllText(output+".stage",stage);
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            stage="generate audio";File.WriteAllText(output+".stage",stage);var audio=Path.Combine(root,"test.wav");
            using(var stream=File.Create(audio))using(var writer=new BinaryWriter(stream)){var length=16000*2*8;writer.Write("RIFF"u8);writer.Write(length+36);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(length);for(var i=0;i<length/2;i++)writer.Write((short)(Math.Sin(i*440d*2*Math.PI/16000)*1200));}
            stage="LibVLC parse";File.WriteAllText(output+".stage",stage);await using var player=new LibVlcAudioPlayer(message=>File.AppendAllText(output+".audio",message+"\n"));engine=player.EngineVersion;player.Volume=0.02;await player.OpenAsync(audio);Check(player.Duration.TotalSeconds>7.5,"native audio parsed");
            stage="pre-play seek";File.WriteAllText(output+".stage",stage);await player.SeekAsync(TimeSpan.FromSeconds(3));await player.PlayAsync();await Task.Delay(800);Check(player.IsPlaying&&player.Position.TotalSeconds>=2.8,"seek before first playback retained");
            stage="pause";File.WriteAllText(output+".stage",stage);await player.PauseAsync();var paused=player.Position;await Task.Delay(400);Check(!player.IsPlaying&&Math.Abs((player.Position-paused).TotalSeconds)<0.4,"pause retains position");
            stage="paused seek and rate";File.WriteAllText(output+".stage",stage);player.PlaybackRate=1.25;player.Volume=0.02;await player.SeekAsync(TimeSpan.FromSeconds(1));await player.PlayAsync();await Task.Delay(700);Check(player.Position.TotalSeconds is >=0.8 and <3,"paused seek and resumed playback");
            stage="sentence loop";File.WriteAllText(output+".stage",stage);await player.SetLoopAsync(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2));await Task.Delay(1800);Check(player.IsPlaying&&player.Position.TotalSeconds is >=0.8 and <2.5,"sentence loop remains in range");
            stage="stop";File.WriteAllText(output+".stage",stage);await player.SetLoopAsync(null,null);await player.StopAsync();Check(!player.IsPlaying&&player.Position==TimeSpan.Zero,"stop clears position");
            stage="end";File.WriteAllText(output+".stage",stage);await player.SeekAsync(TimeSpan.FromSeconds(7));await player.PlayAsync();await Task.Delay(1800);Check(!player.IsPlaying&&player.Position.TotalSeconds>=7.5,"end of media retained");
            stage="invalid audio";File.WriteAllText(output+".stage",stage);var bad=Path.Combine(root,"bad.wav");await File.WriteAllTextAsync(bad,"invalid");var rejected=false;try{await player.OpenAsync(bad);}catch(IOException){rejected=true;}Check(rejected,"invalid audio rejected");await player.OpenAsync(audio);await player.PlayAsync();await Task.Delay(500);Check(player.ErrorMessage is null&&player.IsPlaying,"player reusable after invalid source");await player.StopAsync();
            stage="FFmpeg";File.WriteAllText(output+".stage",stage);var sliced=await new VadSlicer().SliceAsync(audio,Path.Combine(root,"slices"));Check(sliced.Slices.Count==1&&sliced.Duration.TotalSeconds>7.5,"FFmpeg decode and slice");
            stage="ILP";File.WriteAllText(output+".stage",stage);var srt=Path.Combine(root,"transcript.srt");await File.WriteAllTextAsync(srt,"1\n00:00:00,000 --> 00:00:04,000\nRuntime check.\n\n2\n00:00:04,000 --> 00:00:08,000\nAudio playback.\n");
            var package=Path.Combine(root,"lesson.ilp");await new IlpCreator().CreateAsync("Runtime verification",audio,srt,package,Guid.NewGuid().ToString(),1);var lesson=await new IlpImporter(Path.Combine(root,"library")).ImportFileAsync(package);Check(lesson.Cues.Count==2,"ILP create import checksums");
            stage="project delivery";File.WriteAllText(output+".stage",stage);var projects=new CourseProjectStore(Path.Combine(root,"projects"));var project=await projects.CreateAsync(audio);project=project with{Transcript=await File.ReadAllTextAsync(srt),AudioDuration=TimeSpan.FromSeconds(8)};await projects.SaveAsync(project);var zip=await projects.ExportZipToFileAsync(project,Path.Combine(root,"project.zip"));var restored=await projects.ImportZipAsync(zip);Check(restored.HasAudio&&restored.HasTranscript,"project ZIP transfer");
            if(OperatingSystem.IsWindows())
            {
            stage="telemetry native library";File.WriteAllText(output+".stage",stage);
            var telemetry=System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"alibabacloud_rum.dll"));
            try{foreach(var name in new[]{"options_new","options_free","init","close","custom_event_new","custom_event_add_extra","custom_event_report","custom_log_new","custom_log_set_log","custom_log_report"})Check(System.Runtime.InteropServices.NativeLibrary.TryGetExport(telemetry,"alibabacloud_rum_"+name,out _),"ARMS export: "+name);}
            finally{System.Runtime.InteropServices.NativeLibrary.Free(telemetry);}
            }
            if(Program.Arguments.Contains("--standalone"))
            {
                stage="embedded lesson";File.WriteAllText(output+".stage",stage);var index=Array.IndexOf(Program.Arguments,"--standalone");
                var embedded=await new IlpImporter(Path.Combine(root,"embedded-library")).ImportFileAsync(Program.Arguments[index+1]);Check(embedded.Cues.Count>0,"embedded standalone lesson imported");
            }
            if(OperatingSystem.IsWindows() && !Program.Arguments.Contains("--standalone"))
            {
                stage="standalone export";File.WriteAllText(output+".stage",stage);var exe=await new StandaloneLessonExporter(AppContext.BaseDirectory,Path.Combine(AppContext.BaseDirectory,"tools","lesson_player_launcher.exe")).ExportAsync(package,Path.Combine(root,"lesson.exe"));
                Check(new FileInfo(exe).Length>new FileInfo(package).Length,"standalone executable packaged");
                var standaloneOutput=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"standalone-verification.exe");File.Copy(exe,standaloneOutput,true);
            }
        }
        catch(Exception ex){error=ex;}
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{success=error is null,platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),runtime=Environment.Version.ToString(),engine,stage,checks,error=error?.ToString(),dataDirectory=root},new JsonSerializerOptions{WriteIndented=true}));
        return error is null?0:1;
    }
}
