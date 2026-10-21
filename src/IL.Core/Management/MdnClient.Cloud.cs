using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace IL.Core.Management;

public sealed partial class MdnClient
{
    private readonly SemaphoreSlim _cloudGate = new(1, 1);
    private sealed record CloudState(long Sequence, JsonElement? Snapshot, Dictionary<string, ManagedCloudReceipt> Receipts, string? Fingerprint = null);
    private string CloudStatePath => Path.Combine(_root, "cloud.bin");
    private CloudState ReadCloudState() => File.Exists(CloudStatePath)
        ? JsonSerializer.Deserialize<CloudState>(ManagedSecretStore.Load(CloudStatePath), ManagedPolicy.Json) ?? new(0, null, [])
        : new(0, null, []);
    private void SaveCloudState(CloudState state) => ManagedSecretStore.Save(CloudStatePath, JsonSerializer.Serialize(state, ManagedPolicy.Json));

    public async Task ReportCloudAsync(ManagedCloudSnapshot snapshot)
    {
        if (Configuration is null) return;
        await _cloudGate.WaitAsync();
        try
        {
            var state = ReadCloudState();
            var fingerprint = JsonSerializer.Serialize(snapshot, ManagedPolicy.Json);
            if (state.Fingerprint == fingerprint) return;
            var sequence = checked(state.Sequence + 1);
            var body = JsonSerializer.SerializeToElement(new
            {
                sequence, snapshot.PolicyRevision, snapshot.Enabled, snapshot.Authentication, snapshot.Connection,
                snapshot.Account, snapshot.SessionId, snapshot.ErrorCode
            }, ManagedPolicy.Json);
            SaveCloudState(state with { Sequence = sequence, Snapshot = body, Fingerprint = fingerprint });
        }
        finally { _cloudGate.Release(); }
        WakeCloud();
    }

    public async Task ReportCloudReceiptAsync(string id, ManagedCloudReceipt receipt)
    {
        if (Configuration is null) return;
        await _cloudGate.WaitAsync();
        try { var state = ReadCloudState(); state.Receipts[Guid.Parse(id).ToString("D")] = receipt; SaveCloudState(state); }
        finally { _cloudGate.Release(); }
        WakeCloud();
    }

    private void WakeCloud() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private async Task FlushCloudAsync(CancellationToken ct)
    {
        if (_token is null || Configuration is null) return;
        await _cloudGate.WaitAsync(ct);
        try
        {
            var state = ReadCloudState();
            async Task<bool> Send(string route, object payload)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Configuration.Server, route));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                request.Headers.Add("X-IL-Capabilities", "cloudServiceV1");
                request.Content = JsonContent.Create(payload, options: ManagedPolicy.Json);
                using var response = await _http.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            if (state.Snapshot is { } snapshot && await Send("api/cloud/status", snapshot))
            { state = state with { Snapshot = null }; SaveCloudState(state); }
            foreach (var pair in state.Receipts.ToArray())
            {
                if (!await Send("api/cloud/commands/" + pair.Key + "/receipt", pair.Value)) break;
                state.Receipts.Remove(pair.Key); SaveCloudState(state);
            }
        }
        finally { _cloudGate.Release(); }
    }
}
