import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';
import 'package:intensive_listening/transcription/duplicate_match.dart';

import '../ilp/ilp_importer_test.dart' show createPackage;

void main() {
  late Directory temporaryDirectory;
  late Directory libraryDirectory;
  late ImportedLesson lesson;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('dup-test-');
    libraryDirectory = Directory('${temporaryDirectory.path}/library');
    final package = await createPackage(temporaryDirectory);
    lesson = await IlpImporter(libraryDirectory).importFile(package);
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  IlpManifest manifestWith({
    String? title,
    String? audioSha256,
    String? packageUuid,
    int? packageVersion,
  }) {
    return IlpManifest(
      formatVersion: lesson.manifest.formatVersion,
      title: title ?? lesson.manifest.title,
      duration: lesson.manifest.duration,
      audioPath: lesson.manifest.audioPath,
      transcriptPath: lesson.manifest.transcriptPath,
      audioSha256: audioSha256 ?? lesson.manifest.audioSha256,
      transcriptSha256: lesson.manifest.transcriptSha256,
      packageUuid: packageUuid ?? lesson.manifest.packageUuid,
      packageVersion: packageVersion ?? lesson.manifest.packageVersion,
      exercises: lesson.manifest.exercises,
    );
  }

  test('flags an identical package', () {
    final match = findLessonDuplicate([lesson], lesson.manifest);

    expect(match?.duplicateCase, DuplicateCase.identicalPackage);
    expect(match?.existingTitle, 'Test lesson');
    expect(match?.lesson?.id, lesson.id);
  });

  test('uses UUID and version even when the title changes', () {
    final match = findLessonDuplicate([
      lesson,
    ], manifestWith(title: 'Renamed lesson'));

    expect(match?.duplicateCase, DuplicateCase.identicalPackage);
    expect(match?.existingTitle, 'Test lesson');
  });

  test('returns nothing for a different UUID', () {
    final match = findLessonDuplicate([
      lesson,
    ], manifestWith(packageUuid: '33333333-3333-4333-8333-333333333333'));

    expect(match, isNull);
  });

  test('flags a different version of the same UUID as an update', () {
    final match = findLessonDuplicate([
      lesson,
    ], manifestWith(packageVersion: lesson.manifest.packageVersion + 1));

    expect(match?.duplicateCase, DuplicateCase.packageUpdate);
  });

  test('returns nothing for an empty library', () {
    expect(findLessonDuplicate(const [], lesson.manifest), isNull);
  });
}
