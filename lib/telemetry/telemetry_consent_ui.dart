import 'package:fluent_ui/fluent_ui.dart';

import '../widgets/spring_motion.dart';

const telemetryDisclosure =
    '开启后，应用会向阿里云 ARMS 发送匿名设备 ID、应用版本、启动日期、ASR 模型、API 主机名、耗时与缓存命中状态、'
    '系统和硬件信息，以及应用内红色错误提示的原文。SDK 默认自动采集支持的网络请求信息和原生崩溃诊断信息。'
    '网络请求信息可能包含 URL 及参数；错误和崩溃信息可能包含文件名、服务响应或调用堆栈；服务还可能记录时间戳和网络 IP。';

const telemetryRetention =
    '关闭后会停止后续上报并清理待发送缓存。已送达的数据不会立即撤回；阿里云文档所述默认保留期为 60 天，实际以工作区配置为准。';

Future<bool> confirmTelemetryDisable(BuildContext context) async {
  final confirmed = await showSpringDialog<bool>(
    context: context,
    builder: (dialogContext) => ContentDialog(
      title: const Text('关闭匿名数据分析？'),
      constraints: const BoxConstraints(maxWidth: 560),
      content: const Text('$telemetryRetention\n\n关闭后可以随时在设置中重新开启。'),
      actions: [
        Button(
          onPressed: () => Navigator.pop(dialogContext, false),
          child: const Text('保持开启'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(dialogContext, true),
          child: const Text('确认关闭'),
        ),
      ],
    ),
  );
  return confirmed == true;
}
