import 'package:fluent_ui/fluent_ui.dart';

import 'spring_motion.dart';

Future<bool> confirmPermanentDelete({
  required BuildContext context,
  required String title,
  required String message,
}) async {
  final confirmed = await showSpringDialog<bool>(
    context: context,
    builder: (context) => ContentDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context, false),
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, true),
          child: const Text('永久删除'),
        ),
      ],
    ),
  );
  return confirmed ?? false;
}
