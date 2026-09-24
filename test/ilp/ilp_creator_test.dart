import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/ilp_creator.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';

void main() {
  late Directory temporaryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('ilp-create-');
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('creates an importable package from mp3 and srt', () async {
    final audio = File('${temporaryDirectory.path}/lesson.mp3');
    final transcript = File('${temporaryDirectory.path}/lesson.srt');
    final output = File('${temporaryDirectory.path}/lesson.ilp');
    await audio.writeAsBytes([1, 2, 3, 4, 5], flush: true);
    await transcript.writeAsString(testTranscript, flush: true);

    await const IlpCreator().create(
      title: 'Unit 1 Listening',
      audioFile: audio,
      transcriptFile: transcript,
      outputFile: output,
      packageUuid: '11111111-1111-4111-8111-111111111111',
      packageVersion: 1,
    );

    final library = Directory('${temporaryDirectory.path}/library');
    final lesson = await IlpImporter(library).importFile(output);
    expect(lesson.manifest.title, 'Unit 1 Listening');
    expect(lesson.manifest.audioPath, 'audio.mp3');
    expect(lesson.manifest.duration, const Duration(seconds: 5));
    expect(lesson.cues, hasLength(2));
  });
}

const testTranscript = '''1
00:00:00,000 --> 00:00:02,000
Welcome to listening practice.

2
00:00:02,000 --> 00:00:05,000
Write down the key details.
''';
