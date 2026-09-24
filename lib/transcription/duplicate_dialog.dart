import 'package:fluent_ui/fluent_ui.dart';

import 'duplicate_match.dart';
import '../widgets/spring_motion.dart';

enum DuplicateChoice { openExisting, continueAnyway, cancel }

Future<DuplicateChoice> showDuplicateDialog({
  required BuildContext context,
  required DuplicateCase duplicateCase,
  required String existingTitle,
}) async {
  final copy = switch (duplicateCase) {
    DuplicateCase.identicalPackage => (
      title: '课程已存在',
      body: '精听包「$existingTitle」已经导入，内容完全相同。',
      primary: '打开已有',
    ),
    DuplicateCase.sharedAudioLesson => (
      title: '发现相同音频',
      body: '学生端已有「$existingTitle」使用同一段音频。继续会新增一节课。',
      primary: '打开已有',
    ),
    DuplicateCase.packageUpdate => (
      title: '发现课程的其他版本',
      body: '「$existingTitle」已经导入，可以更新本地课程。',
      primary: '打开已有',
    ),
    DuplicateCase.sharedAudioJob => (
      title: '音频已在转写任务中',
      body: '「$existingTitle」正在或已经使用同一段音频转写。',
      primary: '查看任务',
    ),
  };

  final choice = await showSpringDialog<DuplicateChoice>(
    context: context,
    builder: (context) => ContentDialog(
      title: Text(copy.title),
      content: Text(copy.body),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context, DuplicateChoice.cancel),
          child: const Text('取消'),
        ),
        if (duplicateCase != DuplicateCase.identicalPackage &&
            duplicateCase != DuplicateCase.packageUpdate)
          Button(
            onPressed: () =>
                Navigator.pop(context, DuplicateChoice.continueAnyway),
            child: const Text('仍然继续'),
          ),
        FilledButton(
          onPressed: () => Navigator.pop(context, DuplicateChoice.openExisting),
          child: Text(copy.primary),
        ),
      ],
    ),
  );
  return choice ?? DuplicateChoice.cancel;
}
