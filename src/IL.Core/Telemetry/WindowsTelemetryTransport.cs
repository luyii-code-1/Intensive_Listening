using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace IL.Core.Telemetry;
public interface ITelemetryTransport
{
    Task<bool> StartAsync(string version,string cachePath); Task StopAsync(string cachePath);
    Task<bool> EventAsync(string name,IReadOnlyDictionary<string,string> fields); Task<bool> ErrorLogAsync(string text);
    Task<string?> InstallCycleAsync(); Task<string?> InstallUuidAsync(); Task<IReadOnlyDictionary<string,string>> SystemProfileAsync(); Task<bool> CanReachCollectorAsync();
}
public sealed class WindowsTelemetryTransport : ITelemetryTransport, IDisposable
{
    private readonly object _gate = new();
    private nint _library,_options; private bool _running;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint New();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetString(nint value,[MarshalAs(UnmanagedType.LPUTF8Str)]string text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetInt(nint value,int number);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(nint options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NewNamed([MarshalAs(UnmanagedType.LPUTF8Str)]string type,[MarshalAs(UnmanagedType.LPUTF8Str)]string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Extra(nint value,[MarshalAs(UnmanagedType.LPUTF8Str)]string key,[MarshalAs(UnmanagedType.LPUTF8Str)]string text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Log(nint value,int level,[MarshalAs(UnmanagedType.LPUTF8Str)]string text);
    private T Function<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library,"alibabacloud_rum_"+name));
    public Task<bool> StartAsync(string version,string cachePath)
    {
        lock(_gate)
        {
        if(_running)return Task.FromResult(true); if(!OperatingSystem.IsWindows())return Task.FromResult(false);
        try
        {
            _library=NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"alibabacloud_rum.dll"));
            _ = Function<Free>("options_free"); _ = Function<Close>("close"); _ = Function<NewNamed>("custom_event_new"); _ = Function<Extra>("custom_event_add_extra"); _ = Function<Free>("custom_event_report"); _ = Function<NewNamed>("custom_log_new"); _ = Function<Log>("custom_log_set_log"); _ = Function<Free>("custom_log_report");
            _options=Function<New>("options_new")(); if(_options==0)throw new InvalidOperationException("ARMS options initialization failed");
            Function<SetString>("options_set_config_address")(_options,"https://hm3xyft6jd-default-cn.rum.aliyuncs.com"); Function<SetString>("options_set_app_id")(_options,"hm3xyft6jd@76922d8db672517"); Function<SetString>("options_set_app_name")(_options,"Intensive Listening"); Function<SetString>("options_set_app_version")(_options,version); Function<SetString>("options_set_cache_path")(_options,cachePath);
            Function<SetInt>("options_set_auto_curl_tracking")(_options,1); Function<SetInt>("options_set_auto_cef_tracking")(_options,0); Function<SetInt>("options_set_auto_crash_tracking")(_options,1); _running=Function<Init>("init")(_options)==0; if(!_running)Dispose();return Task.FromResult(_running);
        }catch(Exception e)when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException){Dispose();return Task.FromResult(false);}
        }
    }
    public Task StopAsync(string cachePath) { lock(_gate) { if(_running && Function<Close>("close")()!=0) { _running=false; throw new InvalidOperationException("ARMS telemetry close failed"); } _running=false; Dispose(); if(Directory.Exists(cachePath))Directory.Delete(cachePath,true); return Task.CompletedTask; } }
    public Task<bool> EventAsync(string name,IReadOnlyDictionary<string,string> fields) { lock(_gate) { if(!_running)return Task.FromResult(false); var ev=Function<NewNamed>("custom_event_new")("intensive_listening",name);if(ev==0)return Task.FromResult(false); foreach(var field in fields)Function<Extra>("custom_event_add_extra")(ev,field.Key,field.Value);Function<Free>("custom_event_report")(ev);return Task.FromResult(true); } }
    public Task<bool> ErrorLogAsync(string text) { lock(_gate) { if(!_running)return Task.FromResult(false);var log=Function<NewNamed>("custom_log_new")("app_error","error_notice");if(log==0)return Task.FromResult(false);Function<Log>("custom_log_set_log")(log,4,text);Function<Free>("custom_log_report")(log);return Task.FromResult(true); } }
    private static string? RegistryValue(string key,string name) { if(!OperatingSystem.IsWindows())return null;using var registry=Registry.LocalMachine.OpenSubKey(key);return registry?.GetValue(name) as string; }
    public Task<string?> InstallCycleAsync()=>Task.FromResult(RegistryValue(@"Software\Intensive Listening","InstallCycle"));
    public Task<string?> InstallUuidAsync()=>Task.FromResult(RegistryValue(@"Software\Intensive Listening","InstallUuid"));
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct DisplayDevice {public int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string DeviceName;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceString;public uint StateFlags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceId;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceKey;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device,uint index,ref DisplayDevice output,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public Task<IReadOnlyDictionary<string,string>> SystemProfileAsync()
    {
        var fields=new Dictionary<string,string>{{"windows_version",Environment.OSVersion.Version.ToString()},{"cpu_arch",RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()},{"logical_cores",Environment.ProcessorCount.ToString()},{"cpu_model",RegistryValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString")??"unknown"},{"gpu_model","unknown"},{"memory_mib","unknown"}};
        if(OperatingSystem.IsWindows()){for(uint i=0;;i++){var d=new DisplayDevice{Size=Marshal.SizeOf<DisplayDevice>()};if(!EnumDisplayDevices(null,i,ref d,0))break;if((d.StateFlags&4)!=0){fields["gpu_model"]=d.DeviceString;break;}}var m=new MemoryStatus{Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(GlobalMemoryStatusEx(ref m))fields["memory_mib"]=(m.TotalPhysical/1024/1024).ToString();}
        return Task.FromResult<IReadOnlyDictionary<string,string>>(fields);
    }
    public async Task<bool> CanReachCollectorAsync() {try{using var socket=new TcpClient();using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(3));await socket.ConnectAsync("hm3xyft6jd-default-cn.rum.aliyuncs.com",443,cancellation.Token);return true;}catch(Exception e)when(e is SocketException or OperationCanceledException){return false;}}
    public void Dispose() {lock(_gate) {if(_library==0)return; try{if(_running)Function<Close>("close")();if(_options!=0)Function<Free>("options_free")(_options);}finally{_options=0;_running=false;NativeLibrary.Free(_library);_library=0;}}}
}
