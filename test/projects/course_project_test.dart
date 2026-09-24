import 'dart:io';

import 'package:archive/archive.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
import 'package:intensive_listening/projects/course_project.dart';

void main() {
  late Directory temporaryDirectory;
  late CourseProjectStore store;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp(
      'course-project-test-',
    );
    debugProjectsDirectory = Directory('${temporaryDirectory.path}/projects');
    store = const CourseProjectStore();
  });

  tearDown(() async {
    debugProjectsDirectory = null;
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('persists a project and its bound audio for later recovery', () async {
    final source = File('${temporaryDirectory.path}/lesson.mp3');
    await source.writeAsBytes([1, 2, 3], flush: true);

    final project = await store.create(audio: source);
    final restored = await store.loadAll();

    expect(restored, hasLength(1));
    expect(restored.single.id, project.id);
    expect(restored.single.title, 'lesson');
    expect(restored.single.step, CourseProjectStep.transcription);
    expect(restored.single.audioPath, isNot(source.path));
    expect(await File(restored.single.audioPath!).readAsBytes(), [1, 2, 3]);
  });

  test('clears an earlier transcript when audio is replaced', () async {
    final firstAudio = File('${temporaryDirectory.path}/first.mp3');
    final secondAudio = File('${temporaryDirectory.path}/second.wav');
    await firstAudio.writeAsBytes([1], flush: true);
    await secondAudio.writeAsBytes([2], flush: true);

    var project = await store.create(audio: firstAudio);
    project = project.copyWith(
      transcript: '1\n00:00:00,000 --> 00:00:01,000\nHello.\n',
      step: CourseProjectStep.review,
      autoQuestionPlanDeferred: true,
    );
    await store.save(project);
    expect(
      (await store.loadById(project.id))!.autoQuestionPlanDeferred,
      isTrue,
    );
    await store.bindAudio(project, secondAudio);

    final restored = (await store.loadAll()).single;
    expect(restored.transcript, isEmpty);
    expect(restored.step, CourseProjectStep.transcription);
    expect(restored.autoQuestionPlanDeferred, isFalse);
  });

  test(
    'exports and imports every project asset through a zip archive',
    () async {
      final source = File('${temporaryDirectory.path}/lesson.mp3');
      await source.writeAsBytes([1, 2, 3, 4], flush: true);
      var project = await store.create(audio: source);
      project = project.copyWith(
        transcript: '1\n00:00:00,000 --> 00:00:01,000\nHello world.\n',
        step: CourseProjectStep.completed,
        exercises: const LessonExercises(
          questions: [
            LessonQuestion(id: 'q1', title: 'Question one', cueIndexes: [0]),
          ],
          clozeWordIndexes: {
            0: {1},
          },
        ),
      );
      await store.save(project);
      final extra = File(
        '${debugProjectsDirectory!.path}/${project.id}/review-notes.json',
      );
      await extra.writeAsString('{"ready":true}', flush: true);

      final archiveFile = File('${temporaryDirectory.path}/project.zip');
      await archiveFile.writeAsBytes(
        await store.exportZip(project),
        flush: true,
      );
      final imported = await store.importZip(archiveFile);

      expect(imported.id, isNot(project.id));
      expect(imported.packageUuid, project.packageUuid);
      expect(imported.transcript, project.transcript);
      expect(imported.exercises.questions.single.title, 'Question one');
      expect(await File(imported.audioPath!).readAsBytes(), [1, 2, 3, 4]);
      expect(
        await File(
          '${debugProjectsDirectory!.path}/${imported.id}/review-notes.json',
        ).readAsString(),
        '{"ready":true}',
      );
    },
  );

  test('stores DOCX and extracted UTF-8 text with the project', () async {
    final project = await store.create();
    final source = File('${temporaryDirectory.path}/paper.docx');
    final docx = Archive()
      ..addFile(
        ArchiveFile.string(
          'word/document.xml',
          '''<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Question one</w:t></w:r></w:p></w:body></w:document>''',
        ),
      );
    await source.writeAsBytes(ZipEncoder().encodeBytes(docx), flush: true);

    final imported = await store.importExamDocument(project, source);
    expect(imported.text, 'Question one');
    expect(imported.sourceName, 'paper.docx');
    expect(imported.sha256, hasLength(64));
    expect(await File(imported.documentPath).exists(), isTrue);
    expect(await File(imported.textPath).readAsString(), 'Question one');

    final restored = await store.readExamDocument(imported.project);
    expect(restored?.text, 'Question one');
    expect(restored?.sourceName, 'paper.docx');
  });
}
