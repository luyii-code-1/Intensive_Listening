using System.Text.RegularExpressions;
namespace IL.Core.Infrastructure;
public static class AppLog
{
    private static readonly SemaphoreSlim Gate = new(1,1);
    public static string? DirectoryOverride { get; set; }
    public static bool DebugEnabled { get; set; }
    public static string DirectoryPath { get { var path = DirectoryOverride ?? Path.Combine(AppDirectories.DataDirectory(),"logs"); Directory.CreateDirectory(path); return path; } }
    public static void Warning(string message, Exception? exception = null) => _ = WriteAsync("WARN",message,exception);
    public static void Error(string message, Exception? exception = null) => _ = WriteAsync("ERROR",message,exception);
    public static void Debug(string message) { if (DebugEnabled) _ = WriteAsync("DEBUG",message,null); }
    public static void Notice(string title,string message,bool isWarning=false,bool isError=false) { if (isError || Regex.IsMatch(title,"失败|无法|错误|未完成|未更新")) Error($"{title}: {message}"); else if (isWarning || Regex.IsMatch(title,"警告|未绑定")) Warning($"{title}: {message}"); }
    public static async Task WriteAsync(string level,string message,Exception? exception=null) { var now=DateTime.Now; await Gate.WaitAsync(); try { await File.AppendAllTextAsync(Path.Combine(DirectoryPath,$"{now:yyyy-MM-dd}.log"),$"{now:O} [{level}] {message}{(exception is null ? "" : "\n"+exception)}\n"); } catch (Exception e) when(e is IOException or UnauthorizedAccessException) { } finally { Gate.Release(); } }
    public static async Task<long> ClearAsync() { await Gate.WaitAsync(); try { long size=0; foreach(var path in Directory.EnumerateFiles(DirectoryPath,"*.log")) { size+=new FileInfo(path).Length; File.Delete(path); } return size; } finally { Gate.Release(); } }
    public static void Install() { AppDomain.CurrentDomain.UnhandledException += (_,e)=>Error(e.ExceptionObject.ToString()??"Unhandled exception",e.ExceptionObject as Exception); TaskScheduler.UnobservedTaskException += (_,e)=>Error(e.Exception.Message,e.Exception); }
}
