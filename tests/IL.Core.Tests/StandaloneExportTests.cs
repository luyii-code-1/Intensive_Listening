using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using IL.Core.Ilp;
using Xunit;
namespace IL.Core.Tests;
public sealed class StandaloneExportTests
{
    [Fact]
    public async Task FooterMatchesNativeLauncherAndPayloadContainsIsolatedRuntime()
    {
        var root=Path.Combine(Path.GetTempPath(),"il2-standalone-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var runtime=Path.Combine(root,"runtime");Directory.CreateDirectory(runtime);await File.WriteAllTextAsync(Path.Combine(runtime,"IL.App.exe"),"runtime");Directory.CreateDirectory(Path.Combine(runtime,"data"));await File.WriteAllTextAsync(Path.Combine(runtime,"data","secret"),"user data");var launcher=Path.Combine(root,"launcher.exe");await File.WriteAllBytesAsync(launcher,"fake launcher"u8.ToArray());
            var output=Path.Combine(root,"lesson.exe");await new StandaloneLessonExporter(runtime,launcher).ExportAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","dart-course.ilp"),output);
            var bytes=await File.ReadAllBytesAsync(output);var footer=bytes.AsSpan(bytes.Length-80).ToArray();Assert.Equal(Encoding.ASCII.GetBytes("ILPPLAYERPACKV1!"),footer[..16]);var offset=(int)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(16));var length=(int)BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(24));Assert.Equal(bytes.Length-80,offset+length);
            using var payload=new MemoryStream(bytes,offset,length);using var zip=new ZipArchive(payload);Assert.NotNull(zip.GetEntry("IL.App.exe"));Assert.NotNull(zip.GetEntry("lesson.ilp"));Assert.NotNull(zip.GetEntry("lesson.json"));Assert.Null(zip.GetEntry("data/secret"));Assert.StartsWith("11111111-",Encoding.ASCII.GetString(footer,32,48).TrimEnd('\0'));
        }
        finally{Directory.Delete(root,true);}
    }
}
