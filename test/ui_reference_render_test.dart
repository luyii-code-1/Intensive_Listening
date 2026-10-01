// Offscreen reference images for the C# UI port. No desktop UI is launched.
import 'dart:io';
import 'dart:ui' as ui;
import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/main.dart';
import 'package:intensive_listening/settings/app_settings.dart';
import 'package:intensive_listening/projects/course_project.dart';
import 'package:intensive_listening/student/lesson_progress_store.dart';
import 'package:intensive_listening/widgets/first_run_wizard.dart';

void main() {
  late Directory isolated;
  setUpAll(() async {
    isolated = await Directory.systemTemp.createTemp('il-ui-reference-');
    debugLibraryDirectory = Directory('${isolated.path}/library');
    debugSettingsFile = File('${isolated.path}/settings.json');
    debugQueueFile = File('${isolated.path}/queue.json');
    debugProjectsDirectory = Directory('${isolated.path}/projects');
    debugLessonProgressFile = File('${isolated.path}/progress.json');
    debugAudioDurationProbe = (_) async => const Duration(minutes: 3);
    await Directory('artifacts/ui-reference/dart').create(recursive: true);
    for (final entry in {'SourceHanSansSC': ['assets/fonts/SourceHanSansCN-Regular.otf', 'assets/fonts/SourceHanSansCN-Medium.otf', 'assets/fonts/SourceHanSansCN-Bold.otf'], 'packages/fluent_ui/FluentIcons': ['packages/fluent_ui/fonts/FluentIcons.ttf']}.entries) {
      final loader = FontLoader(entry.key);
      for (final path in entry.value) { loader.addFont(rootBundle.load(path)); }
      await loader.load();
    }
  });
  tearDownAll(() async {
    debugLibraryDirectory = null; debugSettingsFile = null; debugQueueFile = null;
    debugProjectsDirectory = null; debugLessonProgressFile = null; debugAudioDurationProbe = null;
    await isolated.delete(recursive: true);
  });
  testWidgets('render original Dart workspaces and OOBE', (tester) async {
    tester.view.physicalSize = const Size(1440, 900); tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize); addTearDown(tester.view.resetDevicePixelRatio);
    final key = GlobalKey();
    Future<void> capture(String name) async {
      await tester.pump(const Duration(seconds: 1));
      final boundary = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      await tester.runAsync(() async {
        final image = await boundary.toImage(pixelRatio: 1);
        final bytes = await image.toByteData(format: ui.ImageByteFormat.png);
        await File('artifacts/ui-reference/dart/$name.png').writeAsBytes(bytes!.buffer.asUint8List()); image.dispose();
      });
    }
    Future<void> waitFor(Finder finder) async {
      for (var i = 0; i < 100; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 10)));
        await tester.pump(const Duration(milliseconds: 100)); if (finder.evaluate().isNotEmpty) return;
      }
      expect(finder, findsWidgets);
    }
    await tester.pumpWidget(RepaintBoundary(key: key, child: const IntensiveListeningApp()));
    await waitFor(find.text('学生端')); await capture('student-home');
    await tester.tap(find.text('制作').first); await waitFor(find.text('还没有课程项目')); await capture('teacher-empty');
    await tester.tap(find.text('设置').first); await waitFor(find.text('应用与集成')); await capture('settings');
    await tester.tap(find.text('转写任务').first); await waitFor(find.text('暂无转写任务')); await capture('queue-empty');
    await tester.tap(find.text('关闭').last); await tester.pump(const Duration(seconds: 1));
    await tester.runAsync(() async {
      final audio = File('${isolated.path}/reference.mp3'); await audio.writeAsBytes([1, 2, 3]);
      final project = await const CourseProjectStore().create(audio: audio);
      await const CourseProjectStore().save(project.copyWith(title: '示例听力课程', step: CourseProjectStep.review,
        transcript: '1\n00:00:00,000 --> 00:00:02,000\n听下面的录音，回答第1小题。\n\n2\n00:00:02,000 --> 00:00:05,000\nHello, Emma. How are you today?\n\n3\n00:00:05,000 --> 00:00:08,000\nI am fine. Thank you.\n'));
    });
    // Remount to refresh the source workspace's project list from isolated data.
    await tester.pumpWidget(const SizedBox()); await tester.pumpWidget(RepaintBoundary(key: key, child: const IntensiveListeningApp()));
    await waitFor(find.text('学生端')); await tester.tap(find.text('制作').first); await waitFor(find.text('示例听力课程'));
    await tester.tap(find.text('示例听力课程').first); await waitFor(find.text('审阅与练习')); await capture('teacher-review');
    final referenceTheme = FluentTheme.of(tester.element(find.byType(TeacherPage)));
    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(RepaintBoundary(key: key, child: FluentApp(theme: referenceTheme, home: FirstRunWizard(initial: AppSettings.defaults(), agreement: '用户协议', privacy: '隐私说明'))));
    await capture('oobe-welcome');
    await tester.tap(find.byTooltip('下一步')); await tester.pumpAndSettle(); await capture('oobe-agreement');
    await tester.tap(find.text('我已阅读并同意用户协议')); await tester.pumpAndSettle(); await tester.tap(find.text('我已阅读并同意隐私说明')); await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('下一步')); await tester.pumpAndSettle(); await capture('oobe-basics');
    await tester.tap(find.byTooltip('下一步')); await tester.pumpAndSettle(); await capture('oobe-appearance');
    await tester.tap(find.byTooltip('下一步')); await tester.pumpAndSettle(); await capture('oobe-api');
    await tester.tap(find.byTooltip('下一步')); await tester.pumpAndSettle(); await capture('oobe-done');
    await tester.pumpWidget(const SizedBox());
  });
}
