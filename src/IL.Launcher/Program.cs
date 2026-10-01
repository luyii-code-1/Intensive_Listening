using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

try
{
    var executable = Environment.ProcessPath ?? throw new IOException("无法定位播放器");
    using var input = File.OpenRead(executable);
    if(input.Length<80)throw new InvalidDataException("独立课程数据缺失");
    input.Position=input.Length-80;var footer=new byte[80];input.ReadExactly(footer);
    if(!footer.AsSpan(0,16).SequenceEqual(Encoding.ASCII.GetBytes("ILPPLAYERPACKV1!")))throw new InvalidDataException("独立课程格式无效");
    var offset=BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(16));var length=BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(24));
    if(offset>(ulong)input.Length-80||length!=(ulong)input.Length-80-offset)throw new InvalidDataException("课程数据边界无效");
    var id=Encoding.ASCII.GetString(footer,32,48).TrimEnd('\0');if(id.Length==0||id.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_'))throw new InvalidDataException("课程标识无效");
    var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Intensive Listening","standalone",id);
    var runtime=Path.Combine(root,"runtime");var app=Path.Combine(runtime,"IL.App.exe");
    if(!File.Exists(app)||!File.Exists(Path.Combine(runtime,"lesson.ilp")))
    {
        Directory.CreateDirectory(root);var zip=Path.Combine(root,"payload.zip");
        input.Position=(long)offset;using(var target=File.Create(zip)){var buffer=new byte[1024*1024];var remaining=(long)length;while(remaining>0){var n=input.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(n==0)throw new EndOfStreamException();target.Write(buffer,0,n);remaining-=n;}}
        ZipFile.ExtractToDirectory(zip,runtime,true);File.Delete(zip);
    }
    var info=new ProcessStartInfo(app){UseShellExecute=false,WorkingDirectory=runtime};info.ArgumentList.Add("--standalone");info.ArgumentList.Add(Path.Combine(runtime,"lesson.ilp"));
    info.Environment["ILP_STANDALONE_DATA"]=Path.Combine(root,"data");
    if(Environment.GetEnvironmentVariable("IL2_LAUNCHER_VERIFY")=="1") { info.ArgumentList.Add("--verify-runtime");info.ArgumentList.Add(Path.Combine(root,"verification.json")); }
    using var process=Process.Start(info)??throw new IOException("播放器启动失败");if(Environment.GetEnvironmentVariable("IL2_LAUNCHER_VERIFY")=="1"){process.WaitForExit();return process.ExitCode;}return 0;
}
catch(Exception ex){File.WriteAllText(Path.Combine(Path.GetTempPath(),"il2-standalone-error.txt"),ex.ToString());return 1;}
