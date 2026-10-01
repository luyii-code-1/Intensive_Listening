using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace IL.Core.Ilp;

public sealed class StandaloneLessonExporter(string runtimeDirectory, string launcherPath)
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ILPPLAYERPACKV1!");
    public const int FooterLength = 80;
    public async Task<string> ExportAsync(string packagePath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(launcherPath)) throw new FileNotFoundException("缺少独立播放器启动组件",launcherPath);
        var manifest = await new IlpImporter(Path.Combine(Path.GetTempPath(), "il2-manifest-reader")).ReadManifestAsync(packagePath);
        var fullOutput = Path.GetFullPath(outputPath);var temp=fullOutput+".tmp";var payloadFile=temp+".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        try
        {
            await using var output = File.Create(temp);
            await using(var launcher=File.OpenRead(launcherPath))await launcher.CopyToAsync(output,ct);
            var payloadOffset=output.Position;
            using(var payload=File.Create(payloadFile))
            using(var archive = new ZipArchive(payload,ZipArchiveMode.Create,true))
            {
                foreach(var file in Directory.EnumerateFiles(runtimeDirectory,"*",SearchOption.AllDirectories))
                {
                    var path=Path.GetFullPath(file);
                    if(path==fullOutput||path==Path.GetFullPath(temp)||path==Path.GetFullPath(payloadFile)||path==Path.GetFullPath(launcherPath))continue;
                    var name=Path.GetRelativePath(runtimeDirectory,file).Replace('\\','/');
                    if(name is "lesson.ilp" or "lesson.json" || name.StartsWith("data/",StringComparison.OrdinalIgnoreCase))continue;
                    await using var input=File.OpenRead(file);await using var entry=archive.CreateEntry(name,CompressionLevel.Fastest).Open();await input.CopyToAsync(entry,ct);
                }
                await using(var input=File.OpenRead(packagePath))await using(var entry=archive.CreateEntry("lesson.ilp").Open())await input.CopyToAsync(entry,ct);
                await using var descriptor=archive.CreateEntry("lesson.json").Open();
                await JsonSerializer.SerializeAsync(descriptor,new{format="intensive-listening-standalone-lesson",version=1,title=manifest.Title,packageUuid=manifest.PackageUuid,packageVersion=manifest.PackageVersion},cancellationToken:ct);
            }
            await using(var payload=File.OpenRead(payloadFile))await payload.CopyToAsync(output,ct);
            File.Delete(payloadFile);
            var footer=new byte[FooterLength];Magic.CopyTo(footer,0);BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(16),(ulong)payloadOffset);BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(24),(ulong)(output.Position-payloadOffset));
            var id=Encoding.ASCII.GetBytes($"{manifest.PackageUuid}_v{manifest.PackageVersion}");if(id.Length>47)throw new InvalidOperationException("课程标识过长");id.CopyTo(footer,32);
            await output.WriteAsync(footer,ct);await output.FlushAsync(ct);output.Close();File.Move(temp,fullOutput,true);return fullOutput;
        }
        catch {if(File.Exists(temp))File.Delete(temp);if(File.Exists(payloadFile))File.Delete(payloadFile);throw;}
    }
}
