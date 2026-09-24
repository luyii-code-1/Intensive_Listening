import 'package:fluent_ui/fluent_ui.dart';

import '../widgets/spring_motion.dart';

enum PackageNameConflictChoice { replace, rename, cancel }

Future<bool> confirmPackageUpdate({
  required BuildContext context,
  required int localVersion,
  required int incomingVersion,
}) async {
  final result = await showSpringDialog<bool>(
    context: context,
    builder: (context) => ContentDialog(
      title: const Text('发现课程的其他版本'),
      content: Text('本地版本 $localVersion，文件版本 $incomingVersion。是否更新？'),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context, false),
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, true),
          child: const Text('更新课程'),
        ),
      ],
    ),
  );
  return result ?? false;
}

Future<PackageNameConflictChoice> resolvePackageNameConflict({
  required BuildContext context,
  required String title,
}) async {
  final result = await showSpringDialog<PackageNameConflictChoice>(
    context: context,
    builder: (context) => ContentDialog(
      title: const Text('课程名称已存在'),
      content: Text('主页已经有名为「$title」的课程。'),
      actions: [
        Button(
          onPressed: () =>
              Navigator.pop(context, PackageNameConflictChoice.cancel),
          child: const Text('取消'),
        ),
        Button(
          onPressed: () =>
              Navigator.pop(context, PackageNameConflictChoice.rename),
          child: const Text('重命名导入'),
        ),
        FilledButton(
          onPressed: () =>
              Navigator.pop(context, PackageNameConflictChoice.replace),
          child: const Text('覆盖本地课程'),
        ),
      ],
    ),
  );
  return result ?? PackageNameConflictChoice.cancel;
}

Future<String?> requestRenamedPackageTitle({
  required BuildContext context,
  required String initialTitle,
}) async {
  final controller = TextEditingController(text: initialTitle);
  final result = await showSpringDialog<String>(
    context: context,
    builder: (context) => ContentDialog(
      title: const Text('重命名课程'),
      content: InfoLabel(
        label: '课程名称',
        child: TextBox(controller: controller, autofocus: true),
      ),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context),
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: () {
            final title = controller.text.trim();
            if (title.isNotEmpty) Navigator.pop(context, title);
          },
          child: const Text('导入'),
        ),
      ],
    ),
  );
  controller.dispose();
  return result;
}
