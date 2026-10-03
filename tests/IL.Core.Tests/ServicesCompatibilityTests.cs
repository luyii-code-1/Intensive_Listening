using System.Net;
using System.Text.Json.Nodes;
using ICSharpCode.SharpZipLib.Zip;
using IL.Core.Settings;
using IL.Core.Mcp;
using IL.Core.Telemetry;
using IL.Core.Projects;
using Xunit;
namespace IL.Core.Tests;
public sealed class ServicesCompatibilityTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"il-services-test-"+Guid.NewGuid().ToString("N"));
    public ServicesCompatibilityTests()=>Directory.CreateDirectory(_root);
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    private static JsonObject Object(string json)=>JsonNode.Parse(json)!.AsObject();
    [Fact] public async Task SettingsPreserveReleasedLoadAndSaveSemantics()
    {
        var path=Path.Combine(_root,"settings.json");await File.WriteAllTextAsync(path,"""{"asrProvider":"local","cloudLanguage":"zh","cloudTimeoutSeconds":"240","cloudConcurrency":20,"transcriptFontSize":40,"telemetryEnabled":true,"telemetryPrompted":true,"eulaAcceptedVersion":"1.0.1","detectedLocalModels":["foo",12]}""");
        var store=new AppSettingsStore(path);var settings=await store.LoadAsync();Assert.Equal(AsrProviderKind.Cloud,settings.AsrProvider);Assert.Equal("en",settings.CloudLanguage);Assert.Equal(240,settings.CloudTimeoutSeconds);Assert.Equal(10,settings.CloudConcurrency);Assert.Equal(28,settings.TranscriptFontSize);Assert.True(settings.TelemetryEnabled);Assert.True(settings.TelemetryPrompted);Assert.Equal("1.0.1",settings.EulaAcceptedVersion);Assert.Equal(["foo"],settings.DetectedLocalModels);
        Assert.Empty(settings.PlayerKeyBindings);
        await store.SaveAsync(settings with{CloudApiKey="secret",McpEnabled=true,ApplicationFontFamily="Segoe UI",ApplicationFontWeight=700,
            PlayerKeyBindings=new Dictionary<PlayerShortcutAction,string>{{PlayerShortcutAction.TogglePlayback,"PageUp"},{PlayerShortcutAction.NextCue,"PageDown"}}});
        var saved=await store.LoadAsync();Assert.Equal("PageUp",saved.PlayerKeyBindings[PlayerShortcutAction.TogglePlayback]);Assert.Equal("PageDown",saved.PlayerKeyBindings[PlayerShortcutAction.NextCue]);
        Assert.Equal("Segoe UI",saved.ApplicationFontFamily);Assert.Equal(700,saved.ApplicationFontWeight);var json=Object(await File.ReadAllTextAsync(path));Assert.Equal("secret",json["cloudApiKey"]!.ToString());Assert.False(json.ContainsKey("cloudReady"));Assert.Equal("",json["completedInstallationId"]!.ToString());json.Remove("completedInstallationId");Assert.Equal(25,json.Count);await File.WriteAllTextAsync(path,"broken");Assert.False((await store.LoadAsync()).TelemetryEnabled);
    }
    [Fact] public void ConfigurationArchiveUsesLegacyAesAndRetainsNonApiPreferences()
    {
        var archive=new ApiConfigurationArchive();var source=AppSettings.Defaults() with{CloudApiKey="private-key",CloudConcurrency=3,AsrProvider=AsrProviderKind.Local,CloudLanguage="zh"};var bytes=archive.Export(source,"passphrase");using(var input=new MemoryStream(bytes))using(var zip=new ZipFile(input)){var entry=zip.GetEntry("api-config.json");Assert.NotNull(entry);Assert.True(entry!.IsCrypted);Assert.Equal(256,entry.AESKeySize);}
        var current=AppSettings.Defaults() with{TelemetryEnabled=true,EulaAcceptedVersion="1.0.1",ThemeMode="dark"};var imported=archive.Import(bytes,"passphrase",current);Assert.Equal(source.CloudApiKey,imported.CloudApiKey);Assert.Equal(3,imported.CloudConcurrency);Assert.Equal(AsrProviderKind.Local,imported.AsrProvider);Assert.Equal("zh",imported.CloudLanguage);Assert.True(imported.TelemetryEnabled);Assert.Equal("dark",imported.ThemeMode);Assert.Equal("1.0.1",imported.EulaAcceptedVersion);Assert.Throws<ApiConfigurationArchiveException>(()=>archive.Import(bytes,"wrong",current));Assert.Throws<ApiConfigurationArchiveException>(()=>archive.Export(source,""));
    }
    [Fact] public void ImportsReleasedDartAesWithUnicodePassword()
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","dart-api-config.zip"));
        var imported=new ApiConfigurationArchive().Import(bytes,"迁移密码",AppSettings.Defaults());
        Assert.Equal("https://dart.example",imported.CloudBaseUrl);Assert.Equal("dart-fixture-key",imported.CloudApiKey);Assert.Equal(240,imported.CloudTimeoutSeconds);Assert.Equal(4,imported.CloudConcurrency);Assert.False(imported.TranslateChineseToEnglish);
    }
    [Fact] public void ImportsTraditionalZipCryptoConfiguration()
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","zipcrypto-api-config.zip"));
        var imported=new ApiConfigurationArchive().Import(bytes,"zipcrypto-password",AppSettings.Defaults());
        Assert.Equal("traditional-key",imported.CloudApiKey);Assert.Equal(3,imported.CloudConcurrency);
    }
    [Fact] public async Task McpCatalogAndSessionsPreserveAuthorizationAndRestart()
    {
        Assert.Equal(31,McpToolCatalog.PublicTools.Count);Assert.DoesNotContain(McpToolCatalog.PublicTools.OfType<JsonObject>(),x=>x.ContainsKey("method"));
        var path=Path.Combine(_root,"mcp","app-private-api.json");var pending=new TaskCompletionSource<AgentApprovalDecision>();var dispatchCount=0;await using var server=new AppPrivateApiServer((method,args)=>{dispatchCount++;return Task.FromResult<object?>(new{ready=true});},path,0){ApprovalRequested=_=>pending.Task};await server.StartAsync();var discovery=Object(await File.ReadAllTextAsync(path));var token=discovery["token"]!.ToString();using var client=new HttpClient();client.DefaultRequestHeaders.Authorization=new("Bearer",token);var baseUrl=$"http://127.0.0.1:{server.Port}";
        async Task<JsonObject> Call(string name,JsonObject? args=null,string? uuid=null){using var response=await client.PostAsync(baseUrl+"/v1/tools/call"+(uuid==null?"":"?agentUuid="+uuid),new StringContent(new JsonObject{["name"]=name,["arguments"]=args??new JsonObject()}.ToJsonString()));return Object(await response.Content.ReadAsStringAsync());}
        var refusedWrite=await Call("list_course_projects");Assert.Equal("agent_required",refusedWrite["error"]!["code"]!.ToString());Assert.Equal(0,dispatchCount);
        Assert.Equal("agent_required",(await Call("report_authoring_step",new(){["step"]=2,["status"]="completed"}))["error"]!["code"]!.ToString());
        var registered=await Call("register_agent",new(){["agentName"]="Test Agent"});var uuid=registered["result"]!["agentUuid"]!.ToString();var connection=await Call("change_event",new(){["event"]="Agent"},uuid);Assert.Equal("PendingApproval",connection["result"]!["event"]!.ToString());Assert.Equal("pending_approval",(await Call("list_course_projects",null,uuid))["error"]!["code"]!.ToString());pending.SetResult(AgentApprovalDecision.Approve);for(var i=0;i<100&&!server.AgentActive;i++)await Task.Delay(10);Assert.True(server.AgentActive);Assert.Equal("agent_uuid_required",(await Call("list_course_projects"))["error"]!["code"]!.ToString());Assert.True((await Call("list_course_projects",null,uuid))["ok"]!.GetValue<bool>());
        await Call("report_authoring_step",new(){["step"]=4,["status"]="running",["detail"]="等待转写"},uuid);
        await Call("report_authoring_step",new(){["step"]=5,["status"]="completed"},uuid);
        await Call("report_authoring_step",new(){["step"]=2,["status"]="skipped",["detail"]="已确认输入"},uuid);
        var progress=(await Call("report_authoring_step",new(){["step"]=6,["status"]="failed",["detail"]="字幕待修正"},uuid))["result"]!;
        Assert.Equal(27,progress["percent"]!.GetValue<int>());Assert.Equal("running",progress["steps"]![3]!["status"]!.ToString());
        Assert.Equal(27,(await Call("intensive_listening_status"))["result"]!["authoringProgress"]!["percent"]!.GetValue<int>());
        Assert.Equal("invalid_step",(await Call("report_authoring_step",new(){["step"]=12,["status"]="completed"},uuid))["error"]!["code"]!.ToString());
        using(var foreign=new HttpRequestMessage(HttpMethod.Get,baseUrl+"/test")){foreign.Headers.Add("Origin","https://external.invalid");using var response=await client.SendAsync(foreign);Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);}
        await server.StopAsync();await server.StartAsync();Assert.Equal(token,Object(await File.ReadAllTextAsync(path))["token"]!.ToString());baseUrl=$"http://127.0.0.1:{server.Port}";var restored=await Call("change_event",new(){["event"]="Agent"},uuid);Assert.Equal("Agent",restored["result"]!["event"]!.ToString());
        Assert.Equal(9,server.WorkflowProgress.Snapshot.Percent);Assert.All(server.WorkflowProgress.Snapshot.Steps.Skip(1),step=>Assert.Equal("pending",step.Status));
        using var rpc=await client.PostAsync(baseUrl+"/mcp?event=Agent&agentUuid="+uuid,new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));Assert.Equal(31,Object(await rpc.Content.ReadAsStringAsync())["result"]!["tools"]!.AsArray().Count);
        using var notification=await client.PostAsync(baseUrl+"/mcp",new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));Assert.Equal(HttpStatusCode.Accepted,notification.StatusCode);
    }
    [Fact]
    public async Task McpRevocationDisconnectsActiveAgentCancelsPendingApprovalAndPersistsRemoval()
    {
        var path = Path.Combine(_root, "mcp", "server.json");
        var approval = new TaskCompletionSource<AgentApprovalDecision>();
        var approvalReturned = new TaskCompletionSource();
        await using var server = new AppPrivateApiServer((_, _) => Task.FromResult<object?>(new { ready = true }), path, 0)
        {
            ApprovalRequested = async name =>
            {
                Assert.Equal("WorkBuddy 教师助手", name);
                var decision = await approval.Task; approvalReturned.TrySetResult(); return decision;
            }
        };
        await server.StartAsync();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Object(await File.ReadAllTextAsync(path))["token"]!.ToString());
        async Task<JsonObject> Call(string tool, JsonObject? args = null, string? uuid = null)
        {
            using var response = await client.PostAsync($"http://127.0.0.1:{server.Port}/v1/tools/call" + (uuid is null ? "" : "?agentUuid=" + uuid),
                new StringContent(new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() }.ToJsonString()));
            return Object(await response.Content.ReadAsStringAsync());
        }
        var registered = await Call("register_agent", new() { ["agentName"] = "WorkBuddy 教师助手" });
        var uuid = registered["result"]!["agentUuid"]!.ToString();
        await Call("change_event", new() { ["event"] = "Agent" }, uuid);
        approval.SetResult(AgentApprovalDecision.Approve);
        for (var i = 0; i < 100 && !server.AgentActive; i++) await Task.Delay(10);
        Assert.True(server.AgentActive); Assert.Equal("WorkBuddy 教师助手", server.ActiveAgentName);
        Assert.Equal("WorkBuddy 教师助手", (await Call("intensive_listening_status"))["result"]!["agentName"]!.ToString());
        var entry = Assert.Single(await server.GetRegisteredAgentsAsync());
        Assert.True(entry.Approved); Assert.True(entry.Active);
        await server.RevokeAgentAsync(uuid);
        Assert.False(server.AgentActive); Assert.Null(server.ActiveAgentName);
        Assert.False(Assert.Single(await server.GetRegisteredAgentsAsync()).Approved);
        Assert.Equal("agent_required", (await Call("list_course_projects", uuid: uuid))["error"]!["code"]!.ToString());
        approval = new(); approvalReturned = new();
        Assert.Equal("PendingApproval", (await Call("change_event", new() { ["event"] = "Agent" }, uuid))["result"]!["event"]!.ToString());
        await server.RevokeAgentAsync(uuid);
        approval.SetResult(AgentApprovalDecision.Approve); await approvalReturned.Task;
        Assert.False(server.AgentActive); Assert.False(Assert.Single(await server.GetRegisteredAgentsAsync()).Approved);
        await server.StopAsync(); await server.RevokeAgentAsync(uuid, remove: true);
        Assert.Empty(await server.GetRegisteredAgentsAsync());
        await using var restoredRegistry = new AppPrivateApiServer((_, _) => Task.FromResult<object?>(null), path, 0);
        Assert.Empty(await restoredRegistry.GetRegisteredAgentsAsync());
        await server.StartAsync();
        Assert.Equal("agent_not_registered", (await Call("change_event", new() { ["event"] = "Agent" }, uuid))["error"]!["code"]!.ToString());
    }
    [Fact] public async Task CourseQuestionAndClozePlansAreAtomicAndPaginated()
    {
        var store=new CourseProjectStore(Path.Combine(_root,"projects"));var p=await store.CreateAsync();p=p with{AudioPath=Path.Combine(_root,"audio.wav"),AudioDuration=TimeSpan.FromSeconds(15),Transcript="1\n00:00:00,000 --> 00:00:01,000\nText 1\n\n2\n00:00:01,000 --> 00:00:03,000\nHello world.\n\n3\n00:00:03,000 --> 00:00:05,000\nHello world.\n"};await store.SaveAsync(p);var service=new AppPrivateApiService(store);
        JsonObject A(string json){var a=Object(json);a["projectId"]=p.Id;return a;}
        await service.DispatchAsync("projects.applyQuestionPlan",A("""{"materials":[{"id":"m1","cueIndexes":[1,2],"repeatedCueIndexes":[2],"questions":[{"id":"q1","number":1,"title":"First","options":["A","B"],"answerIndex":1},{"id":"q2","number":2,"title":"Second"}]}]}"""));var saved=await store.LoadByIdAsync(p.Id);Assert.Equal(2,saved!.Exercises.Questions.Count);Assert.Single(saved.Exercises.Materials);Assert.Equal([2],saved.Exercises.Materials[0].RepeatedCueIndexes);
        var error=await Assert.ThrowsAsync<AppPrivateApiException>(()=>service.DispatchAsync("projects.applyQuestionPlan",A("""{"materials":[{"cueIndexes":[1],"questions":[{"number":1}]},{"cueIndexes":[1],"questions":[{"number":2}]}]}""")));Assert.Equal("cue_already_assigned",error.Code);Assert.Equal(2,(await store.LoadByIdAsync(p.Id))!.Exercises.Questions.Count);
        await service.DispatchAsync("projects.applyClozePlan",A("""{"items":[{"cueIndex":1,"wordIndexes":[1]}]}"""));await Assert.ThrowsAsync<AppPrivateApiException>(()=>service.DispatchAsync("projects.applyClozePlan",A("""{"items":[{"cueIndex":1,"wordIndexes":[0]},{"cueIndex":2,"wordIndexes":[20]}]}""")));var clozeAfterFailure=(await store.LoadByIdAsync(p.Id))!.Exercises.ClozeWordIndexes[1];Assert.Equal(new[]{1},clozeAfterFailure);
        var read=(JsonObject)(await service.DispatchAsync("projects.readSrt",A("""{"offset":1,"limit":1,"includeSrt":true}""")))!;Assert.Single(read["cues"]!.AsArray());Assert.True(read["cuePage"]!["hasMore"]!.GetValue<bool>());Assert.Equal("Hello",read["cues"]![0]!["words"]![0]!["text"]!.ToString());
        await service.DispatchAsync("projects.setCueText",A("""{"cueIndex":1,"text":"Hello"}"""));Assert.False((await store.LoadByIdAsync(p.Id))!.Exercises.ClozeWordIndexes.ContainsKey(1));Assert.Equal(2,(await store.LoadByIdAsync(p.Id))!.Exercises.Questions.Count);
    }
    [Fact] public async Task TelemetryConsentQueuesOfflineActiveAndRevocationDeletesPending()
    {
        var transport=new RecordingTransport{Reachable=false};var telemetry=new AppTelemetry("2.0.0",transport,_root,true);await telemetry.ApplyConsentAsync(false,true);Assert.Equal(0,transport.Starts);await telemetry.ApplyConsentAsync(true,true);Assert.True(telemetry.Enabled);Assert.Single(transport.Events);Assert.Equal("app_launch",transport.Events[0].Name);var pending=Path.Combine(_root,"telemetry","collect.json");Assert.True(File.Exists(pending));await telemetry.ApplyConsentAsync(false,true);Assert.False(telemetry.Enabled);Assert.False(File.Exists(pending));await telemetry.ErrorNoticeAsync("test","private");Assert.Equal(0,transport.Errors);
        transport.Reachable=true;await telemetry.ApplyConsentAsync(true,true);Assert.Contains(transport.Events,e=>e.Name=="app_active");Assert.Contains(transport.Events,e=>e.Name=="system_profile");await telemetry.AsrCompletedAsync("""{"provider":"cloud","model":"qwen","baseUrl":"https://example.com/secret?key=value"}""",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,true);var asr=transport.Events.Last();Assert.Equal("example.com",asr.Fields["api_host"]);Assert.Equal("qwen",asr.Fields["model"]);Assert.DoesNotContain(asr.Fields.Values,v=>v.Contains("secret"));
    }
    private sealed class RecordingTransport:ITelemetryTransport
    {
        public int Starts,Stops,Errors;public bool Reachable=true;public List<(string Name,IReadOnlyDictionary<string,string> Fields)> Events=[];
        public Task<bool> StartAsync(string version,string cachePath){Starts++;return Task.FromResult(true);}public Task StopAsync(string cachePath){Stops++;return Task.CompletedTask;}public Task<bool> EventAsync(string name,IReadOnlyDictionary<string,string> fields){Events.Add((name,fields));return Task.FromResult(true);}public Task<bool> ErrorLogAsync(string text){Errors++;return Task.FromResult(true);}public Task<string?> InstallCycleAsync()=>Task.FromResult<string?>("cycle1");public Task<string?> InstallUuidAsync()=>Task.FromResult<string?>("uuid1");public Task<IReadOnlyDictionary<string,string>> SystemProfileAsync()=>Task.FromResult<IReadOnlyDictionary<string,string>>(new Dictionary<string,string>{{"cpu_arch","x64"}});public Task<bool> CanReachCollectorAsync()=>Task.FromResult(Reachable);
    }
}
