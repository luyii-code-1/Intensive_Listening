using System.Text.Json;

using System.Text;
using System.Text.Json.Nodes;
using ICSharpCode.SharpZipLib.Zip;
namespace IL.Core.Settings;
public sealed class ApiConfigurationArchiveException(string message) : Exception(message);
public sealed class ApiConfigurationArchive
{
    public const string FileName = "api-config.json";
    public byte[] Export(AppSettings settings, string password)
    {
        if (password.Length == 0) throw new ApiConfigurationArchiveException("必须设置导出密码");
        var payload = new { format = "intensive-listening-api-config", version = 1, exportedAt = DateTime.UtcNow.ToString("O"), api = new { endpoint = settings.CloudBaseUrl, key = settings.CloudApiKey, path = settings.CloudEndpoint, name = settings.CloudModel, provider = settings.AsrProvider.ToString().ToLowerInvariant(), timeoutSeconds = settings.CloudTimeoutSeconds, concurrency = settings.CloudConcurrency, language = settings.CloudLanguage, translateChineseToEnglish = settings.TranslateChineseToEnglish, localModelsDirectory = settings.LocalModelsDirectory, selectedLocalModel = settings.SelectedLocalModel, detectedLocalModels = settings.DetectedLocalModels } };
        var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload, AppSettings.JsonOptions));
        return LegacyConfigurationZip.Export(FileName, bytes, password);
    }
    public AppSettings Import(byte[] bytes, string password, AppSettings current)
    {
        if (password.Length == 0) throw new ApiConfigurationArchiveException("请输入配置包密码");
        try
        {
            using var input = new MemoryStream(bytes); using var zip = new ZipFile(input) { ZipCryptoEncoding = Encoding.Latin1, Password = LegacyConfigurationZip.ZipCryptoPassword(password) };
            var entry = zip.GetEntry(FileName); if (entry is null || !entry.IsFile) throw new ApiConfigurationArchiveException("配置包缺少 api-config.json");
            byte[] raw;
            if (entry.AESKeySize > 0) raw = LegacyConfigurationZip.ReadAes(bytes, entry, password);
            else { using var stream = zip.GetInputStream(entry); using var content = new MemoryStream(); stream.CopyTo(content); raw = content.ToArray(); } var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32(); crc.Update(raw);
            if (crc.Value != entry.Crc && (entry.AESKeySize == 0 || entry.Crc != 0)) throw new ApiConfigurationArchiveException("密码错误或配置包已经损坏");
            var j = JsonNode.Parse(new UTF8Encoding(false, true).GetString(raw)) as JsonObject;
            if (j is null || j["format"]?.ToString() != "intensive-listening-api-config" || j["version"]?.ToString() != "1" || j["api"] is not JsonObject api) throw new ApiConfigurationArchiveException("配置包格式无效");
            return current with { AsrProvider = AppSettings.String(api,"provider", "cloud") == "local" ? AsrProviderKind.Local : AsrProviderKind.Cloud, CloudBaseUrl = AppSettings.String(api,"endpoint",current.CloudBaseUrl), CloudEndpoint = AppSettings.String(api,"path",current.CloudEndpoint), CloudModel = AppSettings.String(api,"name",current.CloudModel), CloudApiKey = AppSettings.String(api,"key",current.CloudApiKey), CloudTimeoutSeconds = Math.Clamp(AppSettings.Integer(api,"timeoutSeconds",current.CloudTimeoutSeconds),30,1800), CloudConcurrency = Math.Clamp(AppSettings.Integer(api,"concurrency",current.CloudConcurrency),1,10), CloudLanguage = AppSettings.String(api,"language","en"), TranslateChineseToEnglish = AppSettings.Bool(api,"translateChineseToEnglish",current.TranslateChineseToEnglish), LocalModelsDirectory = AppSettings.String(api,"localModelsDirectory",current.LocalModelsDirectory), SelectedLocalModel = AppSettings.String(api,"selectedLocalModel",current.SelectedLocalModel), DetectedLocalModels = AppSettings.Strings(api["detectedLocalModels"]) };
        }
        catch (ApiConfigurationArchiveException) { throw; }
        catch (Exception e) when (e is ICSharpCode.SharpZipLib.SharpZipBaseException or IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or JsonException or DecoderFallbackException) { throw new ApiConfigurationArchiveException("密码错误或配置包已经损坏"); }
    }
}
