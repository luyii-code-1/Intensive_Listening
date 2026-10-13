using Avalonia.Controls;
namespace IL.App.Services;

internal sealed class WindowsApprovalNotification(Window window) : IDisposable
{
    private readonly WindowsSystemNotification _notification = new(window, 0x494C, 0x8421);
    public void Show(string agentName, Action clicked) => _notification.Show("智能体请求接管", $"{agentName} 请求接管 Intensive Listening，点击打开应用并审批", clicked);
    public void Close() => _notification.Close();
    public void Dispose() => _notification.Dispose();
}
