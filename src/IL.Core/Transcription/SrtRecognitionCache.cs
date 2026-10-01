using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IL.Core.Infrastructure;
namespace IL.Core.Transcription;
public sealed class SrtRecognitionCache(string? root=null)
{
    private (string Srt,string Metadata,string ProfileMd5) Files(string audioMd5,string profile)
    {
        var md5=audioMd5.ToLowerInvariant();if(!Regex.IsMatch(md5,"^[a-f0-9]{32}$"))throw new FormatException("Invalid audio MD5");
        var profileMd5=Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(profile))).ToLowerInvariant();var directory=Path.Combine(root??Path.Combine(AppDirectories.DataDirectory(),"cache","asr-srt"),md5);
        return (Path.Combine(directory,profileMd5+".srt"),Path.Combine(directory,profileMd5+".json"),profileMd5);
    }
    public async Task<string?> ReadAsync(string audioMd5,string profile,CancellationToken ct=default)
    {
        try
        {
            var files=Files(audioMd5,profile);if(!File.Exists(files.Srt)||!File.Exists(files.Metadata))return null;
            using var json=JsonDocument.Parse(await File.ReadAllTextAsync(files.Metadata,ct));var m=json.RootElement;
            if(m.GetProperty("version").GetInt32()!=1||m.GetProperty("audioMd5").GetString()!=audioMd5.ToLowerInvariant()||m.GetProperty("profileMd5").GetString()!=files.ProfileMd5)return null;
            return await File.ReadAllTextAsync(files.Srt,ct);
        }
        catch(Exception ex)when(ex is IOException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException){return null;}
    }
    public async Task WriteAsync(string audioMd5,string profile,string srt,CancellationToken ct=default)
    {
        try
        {
            var files=Files(audioMd5,profile);Directory.CreateDirectory(Path.GetDirectoryName(files.Srt)!);await File.WriteAllTextAsync(files.Srt,srt,ct);
            await File.WriteAllTextAsync(files.Metadata,JsonSerializer.Serialize(new{version=1,audioMd5=audioMd5.ToLowerInvariant(),profileMd5=files.ProfileMd5,createdAt=DateTimeOffset.UtcNow}),ct);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or FormatException){}
    }
}
