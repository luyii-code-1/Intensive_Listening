namespace IL.App.Services;

public static class UiActionRunner
{
    // Convert awaited failures to UI results before returning to an async-void event.
    public static async Task<Exception?> RunAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return error; }
    }
}
