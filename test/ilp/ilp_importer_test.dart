import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';

void main() {
  late Directory temporaryDirectory;
  late Directory libraryDirectory;
  late IlpImporter importer;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('ilp-test-');
    libraryDirectory = Directory('${temporaryDirectory.path}/library');
    importer = IlpImporter(libraryDirectory);
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('imports and validates a correct package', () async {
    final package = await createPackage(temporaryDirectory);

    final lesson = await importer.importFile(package);

    expect(lesson.manifest.title, 'Test lesson');
    expect(lesson.cues, hasLength(2));
    expect(await File(lesson.audioPath).readAsBytes(), audioBytes);
    expect(await File(lesson.transcriptPath).readAsString(), transcript);
  });

  test('rejects unsupported format versions', () async {
    final package = await createPackage(temporaryDirectory, version: 999);

    await expectIlpError(
      importer.importFile(package),
      IlpError.unsupportedVersion,
    );
  });

  test('rejects a package with a missing required file', () async {
    final package = await createPackage(
      temporaryDirectory,
      includeTranscript: false,
    );

    await expectIlpError(importer.importFile(package), IlpError.missingFile);
  });

  test('rejects a package with an invalid content hash', () async {
    final package = await createPackage(
      temporaryDirectory,
      audioHash: List.filled(64, '0').join(),
    );

    await expectIlpError(importer.importFile(package), IlpError.hashMismatch);
  });

  test('rejects a damaged ZIP', () async {
    final package = File('${temporaryDirectory.path}/damaged.ilp');
    await package.writeAsBytes([0, 1, 2, 3, 4]);

    await expectIlpError(importer.importFile(package), IlpError.corruptArchive);
  });

  test('rejects importing the same package twice', () async {
    final package = await createPackage(temporaryDirectory);
    await importer.importFile(package);

    await expectIlpError(
      importer.importFile(package),
      IlpError.duplicatePackage,
    );
  });
}

const audioBytes = [1, 8, 3, 4, 5, 9, 2];
const transcript = '''1
00:00:00,000 --> 00:00:02,000
Hello and welcome.

2
00:00:02,000 --> 00:00:05,000
Today we are learning English.
''';

Future<File> createPackage(
  Directory directory, {
  int version = IlpManifest.supportedVersion,
  bool includeTranscript = true,
  String? audioHash,
}) async {
  final transcriptBytes = utf8.encode(transcript);
  final manifest = {
    'formatVersion': version,
    'title': 'Test lesson',
    'durationMs': 5000,
    'audioPath': 'audio.m4a',
    'transcriptPath': 'transcript.srt',
    'packageUuid': '22222222-2222-4222-8222-222222222222',
    'packageVersion': 1,
    'exercises': const {'questions': [], 'cloze': {}},
    'sha256': {
      'audio.m4a': audioHash ?? sha256.convert(audioBytes).toString(),
      'transcript.srt': sha256.convert(transcriptBytes).toString(),
    },
  };
  final archive = Archive()
    ..add(ArchiveFile.string('manifest.json', jsonEncode(manifest)))
    ..add(ArchiveFile.bytes('audio.m4a', audioBytes));
  if (includeTranscript) {
    archive.add(ArchiveFile.bytes('transcript.srt', transcriptBytes));
  }
  final package = File(
    '${directory.path}/lesson-$version-$includeTranscript.ilp',
  );
  await package.writeAsBytes(ZipEncoder().encodeBytes(archive), flush: true);
  return package;
}

Future<void> expectIlpError(Future<Object?> operation, IlpError code) async {
  await expectLater(
    operation,
    throwsA(isA<IlpException>().having((error) => error.code, 'code', code)),
  );
}
