using IL.Core.Settings;
using Xunit;
namespace IL.Core.Tests;
public sealed class UnicodeCacheCompatibilityTests
{
    [Fact]
    public void ProfileUsesDartLiteralUnicodeAndStableFieldOrder()
    {
        var settings=new AppSettings{SelectedLocalModel=@"C:\模型\英文"};
        Assert.Equal("{\"provider\":\"cloud\",\"baseUrl\":\"https://dashscope.aliyuncs.com\",\"endpoint\":\"/api/v1/services/aigc/multimodal-generation/generation\",\"model\":\"qwen-audio-3.0-asr-flash\",\"language\":\"en\",\"translateChineseToEnglish\":true,\"localModel\":\"C:\\\\模型\\\\英文\",\"segmentMaxSeconds\":120}",settings.AsrCacheProfile);
    }
}
