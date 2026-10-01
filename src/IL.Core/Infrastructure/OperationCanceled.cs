namespace IL.Core.Infrastructure;
public sealed class OperationCanceled(string message = "操作已取消。") : OperationCanceledException(message);
