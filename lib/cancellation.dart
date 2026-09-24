class OperationCanceled implements Exception {
  const OperationCanceled([this.message = '操作已取消。']);

  final String message;

  @override
  String toString() => message;
}
