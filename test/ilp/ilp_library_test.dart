import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_library.dart';

import 'ilp_importer_test.dart' show audioBytes, createPackage;

void main() {
  late Directory temporaryDirectory;
  late Directory libraryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('library-test-');
    libraryDirectory = Directory('${temporaryDirectory.path}/library');
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test(
    'restores an imported lesson after a new library instance starts',
    () async {
      final package = await createPackage(temporaryDirectory);
      final imported = await IlpImporter(libraryDirectory).importFile(package);

      final result = await IlpLibrary(libraryDirectory).load();

      expect(result.issues, isEmpty);
      expect(result.lessons, hasLength(1));
      expect(result.lessons.single.id, imported.id);
      expect(result.lessons.single.manifest.title, 'Test lesson');
      expect(
        await File(result.lessons.single.audioPath).readAsBytes(),
        audioBytes,
      );
    },
  );

  test('checks a damaged audio file when opening its lesson', () async {
    final package = await createPackage(temporaryDirectory);
    final imported = await IlpImporter(libraryDirectory).importFile(package);
    await File(imported.audioPath).writeAsBytes([99], flush: true);

    final result = await IlpLibrary(libraryDirectory).load();

    expect(result.lessons, hasLength(1));
    expect(result.issues, isEmpty);
    expect(await IlpLibrary(libraryDirectory).loadById(imported.id), isNull);
  });

  test('returns an empty snapshot when the library does not exist', () async {
    final result = await IlpLibrary(libraryDirectory).load();

    expect(result.lessons, isEmpty);
    expect(result.issues, isEmpty);
  });
}
