import 'dart:io';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_creator.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
import 'package:intensive_listening/main.dart';
import 'package:intensive_listening/projects/course_project.dart';
import 'package:intensive_listening/settings/app_settings.dart';
import 'package:intensive_listening/student/lesson_progress_store.dart';

void main() {
  late Directory temporaryDirectory;
  late File lessonAudio;
  late File lessonSrt;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('ilp-widget-');
    debugLibraryDirectory = Directory('${temporaryDirectory.path}/library');
    debugSettingsFile = File('${temporaryDirectory.path}/settings.json');
    debugQueueFile = File('${temporaryDirectory.path}/queue.json');
    debugProjectsDirectory = Directory('${temporaryDirectory.path}/projects');
    debugLessonProgressFile = File(
      '${temporaryDirectory.path}/lesson_progress.json',
    );
    debugAudioDurationProbe = (_) async => const Duration(seconds: 2);
    // Fixtures are written here, not inside a testWidgets body: setUp runs
    // outside the FakeAsync zone, where real file I/O never completes.
    lessonAudio = File('${temporaryDirectory.path}/lesson.mp3');
    await lessonAudio.writeAsBytes([1, 2, 3], flush: true);
    lessonSrt = File('${temporaryDirectory.path}/lesson.srt');
    await lessonSrt.writeAsString(
      '1\n00:00:00,000 --> 00:00:02,000\nHello.\n',
      flush: true,
    );
  });

  tearDown(() async {
    debugLibraryDirectory = null;
    debugSettingsFile = null;
    debugQueueFile = null;
    debugProjectsDirectory = null;
    debugLessonProgressFile = null;
    debugAudioDurationProbe = null;
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  testWidgets('shows the student workspace empty state', (tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('打开音频或精听包'));

    expect(find.text('Intensive Listening'), findsNothing);
    expect(find.text('学生端'), findsWidgets);
    expect(find.text('打开音频'), findsAtLeastNWidgets(1));
    expect(find.text('导入精听包'), findsAtLeastNWidgets(1));
    expect(find.byType(Slider), findsNothing);
  });

  testWidgets('file-open launch shows only the playback workspace', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(
      IntensiveListeningApp(
        initialPackagePath: '${temporaryDirectory.path}/missing.ilp',
      ),
    );
    await pumpUntilFound(tester, find.text('无法导入'));

    expect(find.byType(NavigationView), findsNothing);
    expect(find.text('教师端'), findsNothing);
    expect(find.text('设置'), findsNothing);
    expect(find.text('打开音频或精听包'), findsNothing);

    await tester.tap(find.text('完成'));
    await pumpUntilFound(tester, find.text('未能打开精听包'));
  });

  testWidgets('removes a listening project from its independent action', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    late ImportedLesson lesson;
    await tester.runAsync(() async {
      final package = File('${temporaryDirectory.path}/delete-me.ilp');
      await const IlpCreator().create(
        title: 'Lesson for deletion',
        audioFile: lessonAudio,
        transcriptFile: lessonSrt,
        outputFile: package,
        packageUuid: '12345678-1234-4234-8234-123456789abc',
        packageVersion: 1,
      );
      lesson = await IlpImporter(debugLibraryDirectory!).importFile(package);
    });

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.byTooltip('移除课程'));
    expect(find.text('Lesson for deletion'), findsOneWidget);
    await tester.tap(find.byTooltip('移除课程'));
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('移除课程？'), findsOneWidget);
    await tester.tap(find.text('永久删除'));
    await pumpUntilAbsent(tester, find.text('Lesson for deletion'));

    expect(find.text('Lesson for deletion'), findsNothing);
    final exists = await tester.runAsync(
      () => Directory(lesson.directoryPath).exists(),
    );
    expect(exists, isFalse);
    await tester.pump(const Duration(seconds: 5));
    await tester.pump(const Duration(milliseconds: 300));
  });

  testWidgets('shows teacher package creation controls', (tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('教师端').first);
    await pumpUntilFound(tester, find.text('还没有课程项目'));

    expect(find.text('课程项目'), findsOneWidget);
    expect(find.text('暂无项目'), findsOneWidget);
    expect(find.text('新建项目'), findsOneWidget);
    expect(find.text('项目'), findsNothing);
    expect(find.text('导出精听包'), findsNothing);
    expect(find.byType(TabView), findsNothing);
  });

  testWidgets('shows transcription settings page', (tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('设置').first);
    await tester.pump(const Duration(milliseconds: 200));

    expect(find.text('关联文件格式'), findsOneWidget);
    expect(find.text('MCP'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('API 设置'),
      220,
      scrollable: find
          .ancestor(of: find.text('MCP'), matching: find.byType(Scrollable))
          .first,
    );
    expect(find.text('API 设置'), findsOneWidget);
    final apiSettingsExpander = find.ancestor(
      of: find.text('API 设置'),
      matching: find.byType(Expander),
    );
    expect(
      tester.widget<Expander>(apiSettingsExpander).initiallyExpanded,
      isFalse,
    );
    expect(find.text('本地模型'), findsNothing);

    await tester.tap(find.text('API 设置'));
    await tester.pump(const Duration(milliseconds: 300));

    expect(find.text('API Endpoint'), findsOneWidget);
    expect(find.text('Path'), findsOneWidget);
    expect(find.text('Name'), findsOneWidget);
    expect(find.text('Key'), findsOneWidget);
    expect(find.text('分段并发'), findsOneWidget);
    expect(find.text('qwen-audio-3.0-asr-flash'), findsOneWidget);
    expect(find.byKey(saveSettingsButtonKey), findsOneWidget);

    await tester.tap(find.byKey(settingsBackButtonKey));
    await pumpUntilFound(tester, find.text('打开音频或精听包'));
    expect(find.text('打开音频或精听包'), findsOneWidget);
  });

  testWidgets('navigation pane menu toggles compact and expanded modes', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));

    expect(find.text('设置'), findsOneWidget);

    await tester.tap(find.byKey(paneToggleButtonKey));
    await tester.pump(const Duration(milliseconds: 160));

    expect(find.text('设置'), findsNothing);

    await tester.tap(find.byKey(paneToggleButtonKey));
    await tester.pump(const Duration(milliseconds: 160));

    expect(find.text('设置'), findsOneWidget);
  });

  testWidgets('restored audio project exposes the transcription step', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    debugPickFile = ({
      required List<String> allowedExtensions,
      required String dialogTitle,
    }) async => null;
    addTearDown(() => debugPickFile = null);

    await tester.runAsync(
      () => const CourseProjectStore().create(audio: lessonAudio),
    );

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('教师端').first);
    await pumpUntilFound(tester, find.text('lesson'));
    await tester.tap(find.text('lesson').first);
    await pumpUntilFound(tester, find.text('开始转写'));
    expect(find.text('lesson'), findsAtLeastNWidgets(2));
    expect(find.text('开始转写'), findsOneWidget);
    expect(find.text('导出精听包'), findsNothing);
    await tester.tap(find.byIcon(FluentIcons.play).first);
    await pumpUntilFound(tester, find.text('打开音频或精听包'));
    await tester.tap(find.byIcon(FluentIcons.education).first);
    await pumpUntilFound(tester, find.text('还没有课程项目'));
  });

  testWidgets('dragging between review handles selects the full cue range', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.runAsync(() async {
      final project = await const CourseProjectStore().create(
        audio: lessonAudio,
      );
      final reviewProject = project.copyWith(
        audioDuration: const Duration(seconds: 8),
        step: CourseProjectStep.review,
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\nFirst sentence.\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nSecond sentence.\n\n'
            '3\n00:00:04,000 --> 00:00:06,000\nThird sentence.\n',
      );
      await const CourseProjectStore().save(reviewProject);
    });

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('教师端').first);
    await pumpUntilFound(tester, find.text('lesson'));
    await tester.tap(find.text('lesson').first);
    final first = find.byKey(reviewCueSelectionHandleKey(0));
    final third = find.byKey(reviewCueSelectionHandleKey(2));
    await pumpUntilFound(tester, third);

    final gesture = await tester.startGesture(tester.getCenter(first));
    await tester.pump();
    await gesture.moveTo(tester.getCenter(third));
    await tester.pump();
    await gesture.up();
    await tester.pump();

    expect(find.text('新建题目 (3)'), findsOneWidget);
    expect(
      tester.getSize(find.byKey(teacherWorkspaceDividerKey)).height,
      greaterThan(500),
    );
  });

  testWidgets('review transcript builds only visible cue rows', (tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.runAsync(() async {
      final project = await const CourseProjectStore().create(
        audio: lessonAudio,
      );
      await const CourseProjectStore().save(
        project.copyWith(
          audioDuration: const Duration(minutes: 7),
          step: CourseProjectStep.review,
          automaticQuestionPlanApplied: true,
          transcript: serializeSrt([
            for (var index = 0; index < 180; index++)
              SrtCue(
                start: Duration(seconds: index * 2),
                end: Duration(seconds: index * 2 + 1),
                text: 'Sentence $index.',
              ),
          ]),
        ),
      );
    });

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('教师端').first);
    await pumpUntilFound(tester, find.text('lesson'));
    await tester.tap(find.text('lesson').first);
    await pumpUntilFound(tester, find.byKey(reviewCueSelectionHandleKey(0)));

    expect(find.byKey(reviewCueSelectionHandleKey(179)), findsNothing);
  });

  testWidgets('recognizes SRT sections and opens the question overview', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.runAsync(() async {
      final project = await const CourseProjectStore().create(
        audio: lessonAudio,
      );
      await const CourseProjectStore().save(
        project.copyWith(
          audioDuration: const Duration(seconds: 8),
          step: CourseProjectStep.review,
          transcript:
              '1\n00:00:00,000 --> 00:00:01,000\nText 1\n\n'
              '2\n00:00:01,000 --> 00:00:03,000\nFirst sentence.\n\n'
              '3\n00:00:03,000 --> 00:00:05,000\nSecond sentence.\n',
          exercises: const LessonExercises(
            questions: [
              LessonQuestion(
                id: 'question-1',
                title: 'What did the speaker say?',
                cueIndexes: [1, 2],
              ),
            ],
          ),
        ),
      );
    });

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('学生端'));
    await tester.tap(find.text('教师端').first);
    await pumpUntilFound(tester, find.text('lesson'));
    await tester.tap(find.text('lesson').first);
    await pumpUntilFound(tester, find.text('Text 1'));

    expect(find.byKey(reviewCueSelectionHandleKey(0)), findsNothing);
    expect(find.byKey(reviewCueSelectionHandleKey(1)), findsOneWidget);
    await tester.tap(find.byKey(reviewQuestionOverviewButtonKey));
    await tester.pump(const Duration(milliseconds: 200));

    expect(find.text('What did the speaker say?'), findsOneWidget);
    expect(find.textContaining('00:01 – 00:05'), findsWidgets);
    expect(find.byTooltip('删除题目'), findsOneWidget);
  });

  testWidgets('opens the global transcription task center', (tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(const IntensiveListeningApp());
    await pumpUntilFound(tester, find.text('转写任务'));
    await tester.tap(find.text('转写任务'));
    await tester.pump(const Duration(milliseconds: 200));

    expect(find.text('暂无转写任务'), findsOneWidget);
  });
}

Future<void> pumpUntilFound(WidgetTester tester, Finder finder) async {
  for (var i = 0; i < 100; i++) {
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 10)),
    );
    await tester.pump(const Duration(milliseconds: 100));
    if (finder.evaluate().isNotEmpty) return;
  }
}

Future<void> pumpUntilAbsent(WidgetTester tester, Finder finder) async {
  for (var i = 0; i < 100; i++) {
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 10)),
    );
    await tester.pump(const Duration(milliseconds: 100));
    if (finder.evaluate().isEmpty) return;
  }
}
