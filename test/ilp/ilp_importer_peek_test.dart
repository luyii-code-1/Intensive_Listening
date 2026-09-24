import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';

import 'ilp_importer_test.dart' show audioBytes, createPackage, expectIlpError;

void main() {
  late Directory temporaryDirectory;
  late Directory libraryDirectory;
  late IlpImporter importer;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('peek-test-');
    libraryDirectory = Directory('${temporaryDirectory.path}/library');
    importer = IlpImporter(libraryDirectory);
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('reads the manifest without a transcript entry', () async {
    final package = await createPackage(
      temporaryDirectory,
      includeTranscript: false,
    );

    final manifest = await importer.readManifest(package);

    expect(manifest.title, 'Test lesson');
    expect(manifest.duration, const Duration(seconds: 5));
    expect(manifest.audioSha256, sha256.convert(audioBytes).toString());
  });

  test('does not create the library directory', () async {
    final package = await createPackage(temporaryDirectory);

    await importer.readManifest(package);

    expect(await libraryDirectory.exists(), isFalse);
  });

  test('agrees with the package id assigned on import', () async {
    final package = await createPackage(temporaryDirectory);

    final manifest = await importer.readManifest(package);
    final lesson = await importer.importFile(package);

    expect(ilpPackageId(manifest), lesson.id);
  });

  test('rejects unsupported format versions', () async {
    final package = await createPackage(temporaryDirectory, version: 999);

    await expectIlpError(
      importer.readManifest(package),
      IlpError.unsupportedVersion,
    );
  });

  test('rejects a damaged ZIP', () async {
    final package = File('${temporaryDirectory.path}/damaged.ilp');
    await package.writeAsBytes([0, 1, 2, 3, 4]);

    await expectIlpError(
      importer.readManifest(package),
      IlpError.corruptArchive,
    );
  });
}
