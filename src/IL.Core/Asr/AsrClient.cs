using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IL.Core.Ilp;
namespace IL.Core.Asr;
public sealed record AsrConfig(string BaseUrl,string Endpoint,string Model,string ApiKey,string Language="en")
{
    public const string DefaultBaseUrl="https://dashscope.aliyuncs.com";
    public const string DefaultEndpoint="/api/v1/services/aigc/multimodal-generation/generation";
    public const string DefaultModel="qwen-audio-3.0-asr-flash";
    public bool IsComplete=>new[]{BaseUrl,Endpoint,Model,ApiKey}.All(s=>!string.IsNullOrWhiteSpace(s));
    public static AsrConfig FromEnvironment()=>new(Environment.GetEnvironmentVariable("ILP_ASR_BASE_URL")??DefaultBaseUrl,Environment.GetEnvironmentVariable("ILP_ASR_ENDPOINT")??DefaultEndpoint,Environment.GetEnvironmentVariable("ILP_ASR_MODEL")??DefaultModel,Environment.GetEnvironmentVariable("ILP_ASR_API_KEY")??"");
}
public enum AsrStage { Decoding,Slicing,Uploading,Recognizing,Formatting,Merging }
public sealed record AsrProgress(AsrStage Stage,double? StageFraction=null,long BytesDone=0,long BytesTotal=0,int? SegmentIndex=null,int? SegmentTotal=null);
public sealed class AsrException(string message,Exception? inner=null):Exception(message,inner);
public sealed class AsrClient(HttpClient? httpClient=null,TimeSpan? timeout=null)
{
    public static Uri TranscriptionEndpoint(string baseUrl,string? endpoint=null)
    {
        var baseUri=new Uri(baseUrl.Trim());if(string.IsNullOrWhiteSpace(endpoint))return baseUri;
        if(Uri.TryCreate(endpoint.Trim(),UriKind.Absolute,out var absolute)&&absolute.Scheme is "http" or "https")return absolute;
        return new UriBuilder(baseUri){Path=endpoint.Trim().Split('?')[0].TrimStart('/'),Query="",Fragment=""}.Uri;
    }
    public async Task<string> TranscribeToSrtAsync(AsrConfig config,string audioFile,IProgress<AsrProgress>? progress=null,CancellationToken ct=default)
    {
        if(!config.IsComplete)throw new AsrException("请填写 ASR 配置。");if(!File.Exists(audioFile))throw new AsrException("请选择音频文件。");
        using var owned=httpClient==null?new HttpClient{Timeout=Timeout.InfiniteTimeSpan}:null;var client=httpClient??owned!;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout??TimeSpan.FromMinutes(20));
        try
        {
            var audio=await File.ReadAllBytesAsync(audioFile,deadline.Token);progress?.Report(new(AsrStage.Uploading,0,BytesTotal:audio.Length));
            using var request=new HttpRequestMessage(HttpMethod.Post,TranscriptionEndpoint(config.BaseUrl,config.Endpoint));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",config.ApiKey.Trim());
            var payload=new{model=config.Model.Trim(),input=new{messages=new[]{new{role="user",content=new[]{new{audio="data:audio/wav;base64,"+Convert.ToBase64String(audio)}}}}},parameters=new{format="wav",language=config.Language}};
            request.Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json");
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            progress?.Report(new(AsrStage.Uploading,1,audio.Length,audio.Length));progress?.Report(new(AsrStage.Recognizing));
            var body=await response.Content.ReadAsStringAsync(deadline.Token);
            if(!response.IsSuccessStatusCode)
            {
                if((int)response.StatusCode==400&&IsNoWords(body)){progress?.Report(new(AsrStage.Formatting));return "";}
                throw new AsrException(ServerError((int)response.StatusCode,body));
            }
            progress?.Report(new(AsrStage.Formatting));return SrtParser.Serialize(AsrTranscription.FromDashScopeResponseBody(body).Cues).TrimEnd();
        }
        catch(OperationCanceledException ex)when(!ct.IsCancellationRequested){throw new AsrException("ASR 请求超时。",ex);}
        catch(HttpRequestException ex){throw new AsrException("网络连接失败："+ex.Message,ex);}
        catch(JsonException ex){throw new AsrException("ASR 返回结果无法解析。",ex);}
        catch(FormatException ex){throw new AsrException(ex.Message,ex);}
    }
    private static bool IsNoWords(string body){try{using var d=JsonDocument.Parse(body);return Text(d.RootElement,"code")=="ASR_RESPONSE_HAVE_NO_WORDS"||Text(d.RootElement,"message")=="ASR_RESPONSE_HAVE_NO_WORDS";}catch(JsonException){return false;}}
    private static string ServerError(int status,string body){try{using var d=JsonDocument.Parse(body);var message=Text(d.RootElement,"message")??Text(d.RootElement,"code");if(!string.IsNullOrWhiteSpace(message))return $"ASR 请求失败（{status}）：{message.Trim()}";}catch(JsonException){}return $"ASR 请求失败，状态码 {status}。";}
    internal static string? Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
}
public sealed record AsrTranscription(IReadOnlyList<SrtCue> Cues)
{
    public string ToSrt()=>SrtParser.Serialize(Cues).TrimEnd();
    public static AsrTranscription FromDashScopeResponseBody(string body)
    {
        using var d=JsonDocument.Parse(body);var sentences=Find(d.RootElement,"sentence",0,6);
        if(!sentences.HasValue)throw new FormatException("ASR 返回结果没有包含句子时间。");
        var values=sentences.Value.ValueKind==JsonValueKind.Array?sentences.Value.EnumerateArray().ToArray():[sentences.Value];
        return new(values.Where(v=>v.ValueKind==JsonValueKind.Object).SelectMany(SentenceCues).OrderBy(c=>c.Start).ToArray());
    }
    public static AsrTranscription FromResponseBody(string body)
    {
        using var d=JsonDocument.Parse(body);var segments=FindSegments(d.RootElement,0);
        var cues=segments is {ValueKind:JsonValueKind.Array} ? segments.Value.EnumerateArray().Select(Cue).Where(c=>c!=null).Cast<SrtCue>().OrderBy(c=>c.Start).ToArray():[];
        if(cues.Length==0)throw new FormatException("ASR 返回结果没有可用字幕。");return new(cues);
    }
    private static JsonElement? FindSegments(JsonElement value,int depth)
    {
        if(depth>4)return null;
        if(value.ValueKind==JsonValueKind.Array && value.EnumerateArray().All(e=>
            e.ValueKind==JsonValueKind.Object && (AsrClient.Text(e,"text")??AsrClient.Text(e,"transcript")??AsrClient.Text(e,"sentence")) is {} text && !string.IsNullOrWhiteSpace(text)
            && (Number(e,"start")??Number(e,"begin"))!=null && (Number(e,"end")??Number(e,"finish"))!=null))return value;
        if(value.ValueKind==JsonValueKind.Object)
        {
            if(value.TryGetProperty("segments",out var direct)&&direct.ValueKind==JsonValueKind.Array)return direct;
            foreach(var child in value.EnumerateObject()){var found=FindSegments(child.Value,depth+1);if(found.HasValue)return found;}
        }
        return null;
    }
    private static JsonElement? Find(JsonElement e,string key,int depth,int max)
    {
        if(depth>max)return null;
        if(e.ValueKind==JsonValueKind.Object){if(e.TryGetProperty(key,out var direct)&&direct.ValueKind is JsonValueKind.Array or JsonValueKind.Object)return direct;foreach(var p in e.EnumerateObject()){var found=Find(p.Value,key,depth+1,max);if(found.HasValue)return found;}}
        if(e.ValueKind==JsonValueKind.Array)foreach(var child in e.EnumerateArray()){var found=Find(child,key,depth+1,max);if(found.HasValue)return found;}
        return null;
    }
    private static IEnumerable<SrtCue> SentenceCues(JsonElement sentence)
    {
        if(!sentence.TryGetProperty("words",out var words)||words.ValueKind!=JsonValueKind.Array||words.GetArrayLength()==0){var cue=Cue(sentence);return cue==null?[]:[cue];}
        var cues=new List<SrtCue>();var buffer=new StringBuilder();double? start=null;var end=0d;
        void Append(double finish){var text=buffer.ToString().Trim();if(start.HasValue&&finish>start&&text.Length>0)cues.Add(new(TimeSpan.FromMilliseconds(start.Value),TimeSpan.FromMilliseconds(finish),text));buffer.Clear();start=null;}
        foreach(var word in words.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.Object))
        {
            var text=AsrClient.Text(word,"text");if(string.IsNullOrEmpty(text))continue;var punctuation=AsrClient.Text(word,"punctuation")??"";var piece=text+punctuation;
            if(buffer.Length>0&&char.IsAsciiLetterOrDigit(buffer[^1])&&char.IsAsciiLetterOrDigit(piece[0]))buffer.Append(' ');buffer.Append(piece);
            start??=Number(word,"begin_time")??0;end=Number(word,"end_time")??start.Value;
            if((punctuation.Length>0&&".?!。？！…".Contains(punctuation[0]))||buffer.Length>=80||end-start>=8000)Append(end);
        }
        if(buffer.Length>0)Append(Number(sentence,"end_time")??end);return cues;
    }
    private static SrtCue? Cue(JsonElement e)
    {
        var text=AsrClient.Text(e,"text")??AsrClient.Text(e,"transcript")??AsrClient.Text(e,"sentence");
        var start=Number(e,"start")??Number(e,"begin");var end=Number(e,"end")??Number(e,"finish");
        if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("begin_time",out _))start=Number(e,"begin_time")/1000;
        if(e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("end_time",out _))end=Number(e,"end_time")/1000;
        if(string.IsNullOrWhiteSpace(text)||!start.HasValue||!end.HasValue||end<=start)return null;
        return new(TimeSpan.FromSeconds(start.Value),TimeSpan.FromSeconds(end.Value),text.Trim());
    }
    private static double? Number(JsonElement e,string key)
    {if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(key,out var v))return null;if(v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n))return n;if(v.ValueKind==JsonValueKind.String&&double.TryParse(v.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out n))return n;return null;}
}
