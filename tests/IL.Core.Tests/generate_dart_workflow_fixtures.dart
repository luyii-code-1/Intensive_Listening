// Regenerate with: flutter test tests/IL.Core.Tests/generate_dart_workflow_fixtures.dart
import 'dart:convert';
import 'dart:io';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/projects/course_project.dart';
import 'package:intensive_listening/transcription/queue_store.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
void main() {
  test('exports current Dart project and queue contracts', () async {
    final temporary = await Directory.systemTemp.createTemp('il2-dart-workflow-');
    final fixtures = Directory('tests/fixtures');
    await fixtures.create(recursive: true);
    debugProjectsDirectory = Directory('${temporary.path}/projects');
    try {
      final audio = File('${temporary.path}/source.wav');
      await audio.writeAsBytes([0x52,0x49,0x46,0x46]);
      const store = CourseProjectStore();
      var project = CourseProject(
        id: 'dart-legacy-project', title: 'Dart project 工程',
        createdAt: DateTime.utc(2026, 9, 30), updatedAt: DateTime.utc(2026, 10, 1),
        step: CourseProjectStep.review, packageUuid: '22222222-2222-4222-8222-222222222222', packageVersion: 4,
        transcript: '1\n00:00:00,000 --> 00:00:01,000\nHello from Dart.\n',
        exercises: const LessonExercises(materials: [LessonMaterial(id: 'm', prompt: 'Prompt', cueIndexes: [0], questionIds: ['q'])],
          questions: [LessonQuestion(id: 'q', title: 'Question', cueIndexes: [0], materialId: 'm', number: 8, options: ['A', 'B'], answerIndex: 1)], clozeWordIndexes: {0: {1}}),
        automaticQuestionPlanApplied: true,
      );
      await store.save(project);
      project = await store.bindAudio(project, audio, duration: const Duration(seconds: 1));
      project = project.copyWith(step: CourseProjectStep.review, transcript: '1\n00:00:00,000 --> 00:00:01,000\nHello from Dart.\n',
        exercises: const LessonExercises(materials: [LessonMaterial(id: 'm', prompt: 'Prompt', cueIndexes: [0], questionIds: ['q'])],
          questions: [LessonQuestion(id: 'q', title: 'Question', cueIndexes: [0], materialId: 'm', number: 8, options: ['A', 'B'], answerIndex: 1)], clozeWordIndexes: {0: {1}}),
        automaticQuestionPlanApplied: true);
      await store.save(project);
      await store.exportZipToFile(project, File('${fixtures.path}/dart-project.zip'));
      final legacy = {...project.toJson(), 'version': 1}..remove('packageUuid')..remove('packageVersion');
      await File('${fixtures.path}/dart-project-v1.json').writeAsString(jsonEncode(legacy));
      final queue = QueueStore(resolveFile: () async => File('${fixtures.path}/dart-queue.json'));
      await queue.save([
        StoredJob(id: 'dart-running', title: 'Running', audioPath: 'portable.wav', status: 'running', stage: 'recognizing', message: '服务器正在识别。', enqueuedAt: DateTime.utc(2026, 10, 1), projectId: project.id),
        StoredJob(id: 'dart-completed', title: 'Completed', audioPath: 'portable.wav', status: 'completed', stage: 'formatting', message: '字幕已写入 SRT。', enqueuedAt: DateTime.utc(2026, 9, 30),
          srt: project.transcript, audioDurationMs: 1000, sha256: 'a' * 64, audioMd5: 'b' * 32, cacheProfile: '{"model":"qwen"}'),
      ]);
      print('Dart workflow fixtures exported from lib/');
    } finally { debugProjectsDirectory = null; await temporary.delete(recursive: true); }
  });
}
