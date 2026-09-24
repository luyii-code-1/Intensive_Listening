import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_library.dart';

import 'ilp_importer_test.dart' show createPackage;

void main() {
  late Directory temporaryDirectory;
  late Directory libraryDirectory;
  late IlpLibrary library;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('lookup-test-');
    libraryDirectory = Directory('${temporaryDirectory.path}/library');
    library = IlpLibrary(libraryDirectory);
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('finds an imported lesson by its audio hash', () async {
    final package = await createPackage(temporaryDirectory);
    final imported = await IlpImporter(libraryDirectory).importFile(package);

    final match = await library.findByAudioSha256(
      imported.manifest.audioSha256,
    );

    expect(match?.id, imported.id);
    expect(match?.manifest.title, 'Test lesson');
  });

  test('returns null when no lesson uses the audio', () async {
    final package = await createPackage(temporaryDirectory);
    await IlpImporter(libraryDirectory).importFile(package);

    expect(await library.findByAudioSha256('a' * 64), isNull);
  });

  test('looks up by manifest only and does not re-read the audio', () async {
    final package = await createPackage(temporaryDirectory);
    final imported = await IlpImporter(libraryDirectory).importFile(package);
    await File(imported.audioPath).writeAsBytes([99], flush: true);

    expect(
      await library.findByAudioSha256(imported.manifest.audioSha256),
      isNotNull,
    );

    final result = await library.load();
    expect(result.lessons, hasLength(1));
    expect(result.issues, isEmpty);
    expect(await library.loadById(imported.id), isNull);
  });

  test('skips directories without a usable manifest', () async {
    final package = await createPackage(temporaryDirectory);
    final imported = await IlpImporter(libraryDirectory).importFile(package);
    await Directory('${libraryDirectory.path}/not-a-lesson')
        .create(recursive: true);
    await Directory('${libraryDirectory.path}/.import').create(recursive: true);

    final refs = await library.scanManifests();

    expect(refs.map((ref) => ref.id), [imported.id]);
  });

  test('returns nothing when the library does not exist', () async {
    expect(await library.scanManifests(), isEmpty);
    expect(await library.findByAudioSha256('b' * 64), isNull);
  });
}
