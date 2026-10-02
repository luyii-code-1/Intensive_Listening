using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IL.Core.Infrastructure;
namespace IL.Core.Mcp;
public enum AgentApprovalDecision { Approve, Refuse, DisableMcp }
public sealed record RegisteredAgentInfo(string Uuid, string Name, bool Approved, bool Active, bool Pending);
public sealed class AppPrivateApiException(string code,string message) : Exception(message) { public string Code {get;}=code; }
public sealed class AppPrivateApiServer(Func<string,JsonObject,Task<object?>> dispatch,string? discoveryPath=null,int port=17683) : IAsyncDisposable
{
    private sealed record RegisteredAgent(string Name,bool Approved);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,RegisteredAgent> _agents=new();
    private readonly SemaphoreSlim _agentSaveGate=new(1,1);
    private bool _agentsLoaded;
    private HttpListener? _listener; private string? _token,_activeUuid,_pendingUuid,_activeName; private string _state="User"; private int _generation;
    private string DiscoveryPath => discoveryPath ?? Path.Combine(AppDirectories.DataDirectory(),"mcp","app-private-api.json");
    private string Folder => Path.GetDirectoryName(Path.GetFullPath(DiscoveryPath))!;
    public Func<string,Task<AgentApprovalDecision>>? ApprovalRequested {get;set;}
    public event Action<bool>? AgentStateChanged; public event Action? AgentAccessDenied;
    public bool IsRunning => _listener != null; public bool AgentActive => _state=="Agent";
    public bool ApprovalPending => _state == "PendingApproval";
    public int Port {get;private set;}
    public string? ActiveAgentName => AgentActive ? _activeName : null;
    public string BootstrapPath => Path.Combine(Folder, "MCP.md");
    public string McpUrl => IsRunning ? $"http://127.0.0.1:{Port}/mcp?event=Agent&token={_token}" : throw new InvalidOperationException("请先启用 MCP。");
    public event Action? AgentsChanged;
    public AgentWorkflowProgress WorkflowProgress { get; } = new();
    public async Task<IReadOnlyList<RegisteredAgentInfo>> GetRegisteredAgentsAsync()
    {
        if (!IsRunning) await LoadAgentsAsync();
        return _agents.Select(a => new RegisteredAgentInfo(a.Key, a.Value.Name, a.Value.Approved, a.Key == _activeUuid, a.Key == _pendingUuid)).OrderBy(a => a.Name).ToArray();
    }
    public async Task RevokeAgentAsync(string uuid, bool remove = false)
    {
        if (!IsRunning) await LoadAgentsAsync();
        if (!_agents.TryGetValue(uuid, out var agent)) return;
        if (uuid == _activeUuid || uuid == _pendingUuid) DisconnectAgent();
        if (remove) _agents.TryRemove(uuid, out _); else _agents[uuid] = agent with { Approved = false };
        await SaveAgentsAsync();
    }
    private async Task LoadAgentsAsync()
    {
        await _agentSaveGate.WaitAsync();
        try
        {
            if (_agentsLoaded) return;
            _agents.Clear();
            var registry = Path.Combine(Folder, "agents.json");
            if (!File.Exists(registry)) { _agentsLoaded = true; return; }
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(registry)) is JsonObject json)
                    foreach (var pair in json)
                        if (pair.Value is JsonObject a && a["name"] is JsonValue v && v.TryGetValue<string>(out var name))
                            _agents[pair.Key] = new(name, a["approved"]?.ToString() == "true");
            }
            catch (JsonException) { throw new AppPrivateApiException("agent_registry_invalid", "智能体注册表损坏"); }
            _agentsLoaded = true;
        }
        finally { _agentSaveGate.Release(); }
    }
    public void DisconnectAgent() { var active=AgentActive; _generation++; _state="User"; _activeUuid=null; _pendingUuid=null; _activeName=null; if(active)AgentStateChanged?.Invoke(false); AgentsChanged?.Invoke(); }
    public async Task StartAsync()
    {
        if(IsRunning)return; Directory.CreateDirectory(Folder); await LoadAgentsAsync();
        var tokenPath=Path.Combine(Folder,"access-token"); _token=File.Exists(tokenPath)?(await File.ReadAllTextAsync(tokenPath)).Trim():null;
        if(_token is null || !Regex.IsMatch(_token,"^[A-Za-z0-9_-]{40,}$")) { _token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_'); await File.WriteAllTextAsync(tokenPath,_token); }
        Port=port;
        if(Port==0) { var probe=new TcpListener(IPAddress.Loopback,0); probe.Start(); Port=((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        var listener=new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{Port}/"); listener.Start(); _listener=listener;
        var mcpUrl=$"http://127.0.0.1:{Port}/mcp?event=Agent&token={_token}";
        await File.WriteAllTextAsync(Path.Combine(Folder,"SKILL.md"),AgentHelp.Markdown); await File.WriteAllTextAsync(Path.Combine(Folder,"MCP.md"),AgentHelp.Bootstrap(mcpUrl));
        await File.WriteAllTextAsync(DiscoveryPath,JsonSerializer.Serialize(new {version=1,pid=Environment.ProcessId,host="127.0.0.1",port=Port,token=_token,helpPath=Path.Combine(Folder,"SKILL.md"),bootstrapPath=Path.Combine(Folder,"MCP.md"),mcpUrl,toolCallUrl=$"http://127.0.0.1:{Port}/v1/tools/call"})); _=ServeAsync(listener);
    }
    public async Task StopAsync() { DisconnectAgent(); _listener?.Close(); _listener=null; if(File.Exists(Path.Combine(Folder,"MCP.md")))File.Delete(Path.Combine(Folder,"MCP.md")); if(File.Exists(DiscoveryPath)) { try { if(JsonNode.Parse(await File.ReadAllTextAsync(DiscoveryPath))?["token"]?.ToString()==_token)File.Delete(DiscoveryPath); }catch(JsonException){} } _token=null; }
    public ValueTask DisposeAsync()=>new(StopAsync());
    private async Task ServeAsync(HttpListener listener) { while(listener.IsListening) { try { var context=await listener.GetContextAsync(); _=HandleAsync(context); } catch(Exception e) when(e is HttpListenerException or ObjectDisposedException) { break; } } }
    private async Task SaveAgentsAsync() { await _agentSaveGate.WaitAsync(); try { Directory.CreateDirectory(Folder); var path=Path.Combine(Folder,"agents.json"); var snapshot=new JsonObject(_agents.Select(a=>new KeyValuePair<string,JsonNode?>(a.Key,new JsonObject { ["name"]=a.Value.Name,["approved"]=a.Value.Approved }))); await File.WriteAllTextAsync(path+".tmp",snapshot.ToJsonString()); File.Move(path+".tmp",path,true); AgentsChanged?.Invoke(); } finally { _agentSaveGate.Release(); } }
    private async Task ApproveAsync(string uuid) { if(_agents.TryGetValue(uuid,out var a)) { _agents[uuid]=a with {Approved=true}; await SaveAgentsAsync(); } }
    private void BeginApproval(string? name,string? uuid)
    {
        if(_state=="Agent" || _state=="PendingApproval") { if((_state=="Agent"?_activeUuid:_pendingUuid)!=uuid)throw new AppPrivateApiException("agent_busy","已有智能体正在接管或等待审批"); return; }
        if(uuid!=null&&!_agents.ContainsKey(uuid))throw new AppPrivateApiException("agent_not_registered","智能体 UUID 未注册");
        _activeName = (uuid is not null ? _agents[uuid].Name : name)?.Trim();
        if (string.IsNullOrWhiteSpace(_activeName)) _activeName = "未命名智能体";
        if(uuid!=null&&_agents[uuid].Approved) { _activeUuid=uuid; _state="Agent"; WorkflowProgress.BeginSession(); AgentStateChanged?.Invoke(true); return; }
        if(ApprovalRequested==null) { _activeUuid=uuid; _state="Agent"; WorkflowProgress.BeginSession(); AgentStateChanged?.Invoke(true); if(uuid!=null)_=ApproveAsync(uuid); return; }
        _state="PendingApproval"; _pendingUuid=uuid; AgentsChanged?.Invoke(); var generation=++_generation; _=CompleteApprovalAsync(uuid,name,generation);
    }
    private async Task CompleteApprovalAsync(string? uuid,string? name,int generation) { try { var decision=await ApprovalRequested!((uuid is not null?_agents[uuid].Name:name)?.Trim()??"未命名智能体"); if(generation!=_generation||!IsRunning)return; if(decision==AgentApprovalDecision.Approve) { if(uuid!=null)await ApproveAsync(uuid); if(generation!=_generation||!IsRunning)return; _activeUuid=uuid; _pendingUuid=null; _state="Agent"; WorkflowProgress.BeginSession(); AgentStateChanged?.Invoke(true); } else { _state="UserRefused"; _pendingUuid=null; } }catch { if(generation==_generation){_state="UserRefused";_pendingUuid=null;} } }
    private JsonObject Session() => new() { ["event"]=_state,["connected"]=AgentActive,["agentName"]=ActiveAgentName,["helpPath"]=Path.Combine(Folder,"SKILL.md"),["instructions"]=AgentActive?$"连接已获批准。先读取 SKILL.md：{Path.Combine(Folder,"SKILL.md")}; 随后调用 intensive_listening_status。":_state=="PendingApproval"?"等待应用内接管审批；批准后重新调用 intensive_listening_status。":"首次连接先调用 register_agent 获取专属 MCP URL；已有 UUID 时调用 change_event 请求接管。" };
    private void RequireAgent(string? uuid) { if(AgentActive) { if(_activeUuid!=null&&uuid!=_activeUuid)throw new AppPrivateApiException("agent_uuid_required","请使用已批准的智能体 MCP URL"); return; } if(_state=="PendingApproval")throw new AppPrivateApiException("pending_approval","Pending Approval"); if(_state=="UserRefused")throw new AppPrivateApiException("user_refused","User Refused"); AgentAccessDenied?.Invoke(); throw new AppPrivateApiException("agent_required","当前 event=User，请先以 event=Agent 建立智能体会话。"); }
    private async Task<object?> CallAsync(string method,JsonObject args,string? uuid)
    {
        if(method=="agent.register") { var name=args["agentName"]?.GetValue<string>(); if(string.IsNullOrWhiteSpace(name)||name.Length>80)throw new AppPrivateApiException("invalid_agent_name","智能体名称须为 1 至 80 个字符"); var id=Guid.NewGuid().ToString(); _agents[id]=new(name.Trim(),false); await SaveAgentsAsync(); return new {agentUuid=id,mcpUrl=$"http://127.0.0.1:{Port}/mcp?event=Agent&token={_token}&agentUuid={id}",@event="User"}; }
        if(method=="agent.changeEvent") { var ev=args["event"]?.ToString(); if(ev=="User") { var owner=_activeUuid??_pendingUuid; if(owner!=null&&owner!=uuid)throw new AppPrivateApiException("agent_uuid_required","请使用已注册的智能体 MCP URL"); DisconnectAgent(); return new {@event="User",connected=false}; } if(ev!="Agent")throw new AppPrivateApiException("invalid_event","event 须为 Agent 或 User"); var id=args["agentUuid"]?.ToString()??uuid; if(id==null||!_agents.ContainsKey(id))throw new AppPrivateApiException("agent_not_registered","请先调用 register_agent"); if(uuid!=null&&uuid!=id)throw new AppPrivateApiException("agent_uuid_mismatch","MCP URL 与智能体 UUID 不一致"); BeginApproval(_agents[id].Name,id); return Session(); }
        if(method=="agent.connect") { BeginApproval(args["agentName"]?.ToString(),null); return Session(); }
        if(method=="agent.disconnect") { RequireAgent(uuid); DisconnectAgent(); return new {@event="User",connected=false}; }
        if(method!="app.status")RequireAgent(uuid);
        if(method=="agent.reportStep")
        {
            if(args["step"] is not JsonValue stepValue || !stepValue.TryGetValue<int>(out var step))
                throw new AppPrivateApiException("invalid_step", "步骤编号须为 1 至 11 的整数");
            if(args["status"] is not JsonValue statusValue || !statusValue.TryGetValue<string>(out var status))
                throw new AppPrivateApiException("invalid_step_status", "请提交步骤状态");
            string? detail = null;
            if(args["detail"] is {} detailNode && (detailNode is not JsonValue detailValue || !detailValue.TryGetValue<string>(out detail)))
                throw new AppPrivateApiException("invalid_step_detail", "步骤详情须为文字");
            return WorkflowProgress.Report(step,status,detail).ToJson();
        }
        var result=await dispatch(method,args); if(method=="app.status") { var j=JsonSerializer.SerializeToNode(result)?.AsObject()??new JsonObject(); foreach(var p in Session())j[p.Key]=p.Value?.DeepClone(); j["authoringProgress"]=WorkflowProgress.Snapshot.ToJson(); return j; } return result;
    }
    private static object Error(string code,string message)=>new {ok=false,error=new {code,message}};
    private async Task HandleAsync(HttpListenerContext context)
    {
        var r=context.Request; var response=context.Response; response.ContentType="application/json"; object? reply=null; var path=r.Url!.AbsolutePath; JsonNode? id=null; var mcp=path=="/mcp"; var legacy=path=="/v1/rpc";
        try
        {
            var origin=r.Headers["Origin"]; if(origin!=null&&!new[] {"http://127.0.0.1","http://localhost",$"http://127.0.0.1:{Port}",$"http://localhost:{Port}"}.Contains(origin)) {response.StatusCode=403;reply=Error("invalid_origin","请求来源不受信任");}
            else if(r.HttpMethod=="GET"&&path=="/test")reply=new {status="ok",version="2.0.0"};
            else if(r.Headers["Authorization"]!=$"Bearer {_token}"&&r.QueryString["token"]!=_token) {response.StatusCode=401;reply=Error("unauthorized","访问令牌无效");}
            else if(r.HttpMethod=="GET"&&path=="/v1/health")reply=new {ok=true,version=1,pid=Environment.ProcessId};
            else if(r.HttpMethod=="GET"&&path=="/v1/tools")reply=new {tools=McpToolCatalog.PublicTools};
            else if(r.HttpMethod=="GET"&&mcp) {response.StatusCode=405;reply=Error("method_not_allowed","MCP 端点仅接受 POST");}
            else if(r.HttpMethod!="POST"||!new[]{"/mcp","/v1/tools/call","/v1/rpc","/v1/agent/connect","/v1/agent/disconnect"}.Contains(path)){response.StatusCode=404;reply=Error("not_found","接口不存在");}
            else
            {
                if(r.ContentLength64>8*1024*1024) {response.StatusCode=413;throw new AppPrivateApiException("request_too_large","请求超过 8 MB");}
                using var memory=new MemoryStream(); var buffer=new byte[8192]; int count; while((count=await r.InputStream.ReadAsync(buffer))>0) {memory.Write(buffer,0,count); if(memory.Length>8*1024*1024) {response.StatusCode=413;throw new AppPrivateApiException("request_too_large","请求超过 8 MB");}}
                JsonObject body; try { body=JsonNode.Parse(memory.ToArray()) as JsonObject??throw new AppPrivateApiException("invalid_request","请求必须是 JSON 对象"); }catch(JsonException){throw new AppPrivateApiException("invalid_json","请求体不是有效 JSON");}
                id=body["id"]?.DeepClone(); var uuid=r.QueryString["agentUuid"];
                if(mcp) reply=await HandleMcpAsync(body,r.QueryString["event"],uuid,response);
                else if(path=="/v1/tools/call") { var tool=McpToolCatalog.Find(body["name"]?.ToString()??"")??throw new AppPrivateApiException("tool_not_found","未知工具"); reply=new {ok=true,result=await CallAsync(tool["method"]!.ToString(),ObjectArgs(body,"arguments"),uuid)}; }
                else if(path.StartsWith("/v1/agent/")) { if(path.EndsWith("/connect")) {if(body["event"]?.ToString()!="Agent")throw new AppPrivateApiException("invalid_event","启动智能体须设置 event=Agent"); BeginApproval(body["agentName"]?.ToString(),body["agentUuid"]?.ToString()??uuid); }else{RequireAgent(uuid);DisconnectAgent();} var session=Session();session["ok"]=true;reply=session; }
                else { if(body["version"]?.ToString()!="1")throw new AppPrivateApiException("unsupported_version","不支持的 App 私有协议版本"); var method=body["method"]?.ToString()??throw new AppPrivateApiException("invalid_request","method 或 params 无效"); var args=ObjectArgs(body,"params"); if(method=="agent.connect"){if(body["event"]?.ToString()!="Agent")throw new AppPrivateApiException("invalid_event","启动智能体须设置 event=Agent");args["agentName"]=body["agentName"]?.DeepClone();} reply=new {version=1,id,ok=true,result=await CallAsync(method,args,uuid)}; }
            }
        }
        catch(AppPrivateApiException e) { if(mcp)reply=new {jsonrpc="2.0",id,error=new {code=-32600,message=e.Message,data=new {code=e.Code}}}; else if(legacy)reply=new {version=1,id,ok=false,error=new {code=e.Code,message=e.Message}}; else {if(response.StatusCode!=413)response.StatusCode=new[]{"agent_required","pending_approval","user_refused"}.Contains(e.Code)?403:400;reply=Error(e.Code,e.Message);} }
        catch(Exception e) { if(mcp)reply=new {jsonrpc="2.0",id,error=new {code=-32603,message=e.Message}}; else {response.StatusCode=500;reply=Error("internal_error",e.Message);} }
        try {if(reply!=null){var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));await response.OutputStream.WriteAsync(bytes);}response.Close();}catch(Exception e)when(e is HttpListenerException or ObjectDisposedException){}
    }
    private static JsonObject ObjectArgs(JsonObject body,string key)=>body[key] switch {null=>new(),JsonObject j=>(JsonObject)j.DeepClone(),_=>throw new AppPrivateApiException("invalid_arguments",$"{key} 必须是对象")};
    private async Task<object?> HandleMcpAsync(JsonObject body,string? queryEvent,string? uuid,HttpListenerResponse response)
    {
        if(body["jsonrpc"]?.ToString()!="2.0"||body["method"] is not JsonValue)throw new AppPrivateApiException("invalid_request","MCP 请求必须是 JSON-RPC 2.0"); var method=body["method"]!.ToString(); var args=ObjectArgs(body,"params"); var id=body["id"]?.DeepClone(); if(id==null){response.StatusCode=202;return null;} object? result;
        switch(method)
        {
            case "initialize": if((args["event"]?.ToString()??queryEvent)!="Agent")throw new AppPrivateApiException("invalid_event","MCP 启动须设置 event=Agent");if(uuid!=null&&!_agents.ContainsKey(uuid))throw new AppPrivateApiException("agent_not_registered","智能体 UUID 未注册");result=new {protocolVersion="2025-11-25",capabilities=new {tools=new {listChanged=false}},serverInfo=new {name="Intensive Listening",version="2.0.0"},instructions=Session()["instructions"]?.ToString()};break;
            case "ping":result=new {};break;
            case "tools/list":result=new {tools=McpToolCatalog.PublicTools};break;
            case "tools/call": var tool=McpToolCatalog.Find(args["name"]?.ToString()??"")??throw new AppPrivateApiException("tool_not_found","未知工具");var arguments=ObjectArgs(args,"arguments");try {var value=await CallAsync(tool["method"]!.ToString(),arguments,uuid);result=new {content=new[]{new {type="text",text=JsonSerializer.Serialize(value)}},isError=false};}catch(AppPrivateApiException e){result=new {content=new[]{new {type="text",text=$"{e.Code}: {e.Message}"}},isError=true};}break;
            default:throw new AppPrivateApiException("method_not_found","MCP 方法不存在");
        }
        return new {jsonrpc="2.0",id,result};
    }
}
