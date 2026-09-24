import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/transcription/duplicate_dialog.dart';
import 'package:intensive_listening/transcription/duplicate_match.dart';

void main() {
  Future<void> openDialog(
    WidgetTester tester, {
    required DuplicateCase duplicateCase,
  }) async {
    await tester.pumpWidget(
      FluentApp(
        home: Builder(
          builder: (context) => FilledButton(
            onPressed: () => showDuplicateDialog(
              context: context,
              duplicateCase: duplicateCase,
              existingTitle: 'Test lesson',
            ),
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
  }

  testWidgets('an identical package only offers opening the existing one', (
    tester,
  ) async {
    await openDialog(tester, duplicateCase: DuplicateCase.identicalPackage);

    expect(find.text('课程已存在'), findsOneWidget);
    expect(find.textContaining('内容完全相同'), findsOneWidget);
    expect(find.text('打开已有'), findsOneWidget);
    expect(find.text('仍然继续'), findsNothing);
  });

  testWidgets('the same audio under another title allows continuing', (
    tester,
  ) async {
    await openDialog(tester, duplicateCase: DuplicateCase.sharedAudioLesson);

    expect(find.text('发现相同音频'), findsOneWidget);
    expect(find.textContaining('Test lesson'), findsOneWidget);
    expect(find.text('打开已有'), findsOneWidget);
    expect(find.text('仍然继续'), findsOneWidget);
  });

  testWidgets('the same audio already queued offers viewing the task', (
    tester,
  ) async {
    await openDialog(tester, duplicateCase: DuplicateCase.sharedAudioJob);

    expect(find.text('音频已在转写任务中'), findsOneWidget);
    expect(find.text('查看任务'), findsOneWidget);
    expect(find.text('仍然继续'), findsOneWidget);
  });

  testWidgets('returns the chosen action', (tester) async {
    DuplicateChoice? choice;
    await tester.pumpWidget(
      FluentApp(
        home: Builder(
          builder: (context) => FilledButton(
            onPressed: () async {
              choice = await showDuplicateDialog(
                context: context,
                duplicateCase: DuplicateCase.sharedAudioLesson,
                existingTitle: 'Test lesson',
              );
            },
            child: const Text('open'),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('仍然继续'));
    await tester.pumpAndSettle();

    expect(choice, DuplicateChoice.continueAnyway);
  });
}
