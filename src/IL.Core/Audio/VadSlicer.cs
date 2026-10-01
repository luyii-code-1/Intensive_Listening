using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
namespace IL.Core.Audio;
public sealed record SilenceRange(TimeSpan Start,TimeSpan End) { public TimeSpan Midpoint => TimeSpan.FromTicks((Start.Ticks+End.Ticks)/2); }
public sealed record VadSlice(string File,TimeSpan Start,TimeSpan End);
public sealed record VadSliceResult(IReadOnlyList<VadSlice> Slices,TimeSpan Duration);
public sealed class NoSilenceCutException() : Exception("120 秒范围内未检测到静音，可选择强制切片继续转写。");
public sealed class VadSlicer(string? ffmpegPath=null)
{
    public static string LocateFfmpeg()=>Environment.GetEnvironmentVariable("ILP_FFMPEG_PATH")?.Trim() is {Length:>0} path ? path : Path.Combine(AppContext.BaseDirectory,OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
    public async Task<VadSliceResult> SliceAsync(string input,string outputDirectory,TimeSpan? maxSegmentDuration=null,bool forceCutOnNoSilence=false,CancellationToken ct=default)
    {
        var executable=ffmpegPath??LocateFfmpeg();if(!File.Exists(executable))throw new FileNotFoundException("缺少 FFmpeg 运行组件",executable);
        Directory.CreateDirectory(outputDirectory);
        var analysis=await RunFfmpegAsync(executable,["-hide_banner","-nostats","-i",input,"-af","silencedetect=noise=-35dB:d=0.25","-f","null","-"],ct);
        var duration=ParseDuration(analysis);if(duration==null||duration<=TimeSpan.Zero)throw new FormatException("无法读取音频时长。");
        var cuts=ChooseCutPoints(duration.Value,ParseSilenceRanges(analysis,duration.Value),maxSegmentDuration,forceCutOnNoSilence:forceCutOnNoSilence);
        var arguments=new List<string>{"-hide_banner","-nostats","-loglevel","error","-y","-i",input};
        if(cuts.Count>0)arguments.AddRange(["-f","segment","-segment_times",string.Join(',',cuts.Select(t=>t.TotalSeconds.ToString("F3",CultureInfo.InvariantCulture))),"-reset_timestamps","1"]);
        arguments.AddRange(["-ac","1","-ar","16000","-c:a","pcm_s16le",Path.Combine(outputDirectory,cuts.Count>0?"chunk_%03d.wav":"chunk_000.wav")]);
        await RunFfmpegAsync(executable,arguments,ct);
        var files=Directory.GetFiles(outputDirectory,"chunk_*.wav").Order(StringComparer.Ordinal).ToArray();
        var boundaries=new[]{TimeSpan.Zero}.Concat(cuts).Append(duration.Value).ToArray();
        if(files.Length!=boundaries.Length-1)throw new FormatException("音频切片数量与时间边界不一致。");
        return new(files.Select((file,i)=>new VadSlice(file,boundaries[i],boundaries[i+1])).ToArray(),duration.Value);
    }
    public static IReadOnlyList<TimeSpan> ChooseCutPoints(TimeSpan duration,IReadOnlyList<SilenceRange> silences,TimeSpan? maxSegmentDuration=null,TimeSpan? minSegmentDuration=null,bool forceCutOnNoSilence=false)
    {
        var max=maxSegmentDuration??TimeSpan.FromSeconds(120);var min=minSegmentDuration??TimeSpan.FromSeconds(15);
        if(max<=TimeSpan.Zero||min<=TimeSpan.Zero||min>max)throw new ArgumentOutOfRangeException(nameof(maxSegmentDuration));
        var cuts=new List<TimeSpan>();var cursor=TimeSpan.Zero;
        while(duration-cursor>max)
        {
            TimeSpan? selected=null;var minimum=cursor+min;var maximum=cursor+max;
            foreach(var silence in silences.OrderBy(s=>s.Start))
            {
                if(silence.End<minimum)continue;if(silence.Start>maximum)break;
                var candidate=silence.Midpoint>maximum?maximum:silence.Midpoint<minimum?minimum:silence.Midpoint;
                if(candidate>=silence.Start&&candidate<=silence.End)selected=candidate;
            }
            if(selected==null){if(!forceCutOnNoSilence)throw new NoSilenceCutException();selected=maximum;}
            cuts.Add(selected.Value);cursor=selected.Value;
        }
        return cuts;
    }
    public static TimeSpan? ParseDuration(string output)
    {
        var m=Regex.Match(output,@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        return m.Success?TimeSpan.FromMilliseconds(Math.Round((double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*3600+double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)*60+double.Parse(m.Groups[3].Value,CultureInfo.InvariantCulture))*1000,MidpointRounding.AwayFromZero)):null;
    }
    public static IReadOnlyList<SilenceRange> ParseSilenceRanges(string output,TimeSpan duration)
    {
        var result=new List<SilenceRange>();TimeSpan? start=null;
        foreach(Match m in Regex.Matches(output,@"silence_(start|end):\s*(-?\d+(?:\.\d+)?)"))
        {
            var value=TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture));
            if(m.Groups[1].Value=="start")start=value<TimeSpan.Zero?TimeSpan.Zero:value;
            else if(start.HasValue&&value>start){result.Add(new(start.Value,value>duration?duration:value));start=null;}
        }
        if(start.HasValue&&start<duration)result.Add(new(start.Value,duration));return result;
    }
    public static async Task<string> RunFfmpegAsync(string executable,IEnumerable<string> arguments,CancellationToken ct=default)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true};foreach(var a in arguments)info.ArgumentList.Add(a);
        using var process=Process.Start(info)??throw new IOException("FFmpeg 无法启动");
        using var registration=ct.Register(()=>{try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}});
        var stderr=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);var output=await stderr;await stdout;ct.ThrowIfCancellationRequested();
        if(process.ExitCode!=0)throw new FormatException(output.Trim().Split('\n').LastOrDefault()?.Trim() is {Length:>0} last?last:"FFmpeg 执行失败。");
        return output;
    }
    public static async Task<TimeSpan?> ProbeDurationAsync(string path,TimeSpan? timeout=null,CancellationToken ct=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout??TimeSpan.FromSeconds(10));
        try{return ParseDuration(await RunFfmpegAsync(LocateFfmpeg(),["-hide_banner","-i",path,"-f","null","-"],deadline.Token));}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return null;}catch(IOException){return null;}catch(FormatException){return null;}
    }
}
