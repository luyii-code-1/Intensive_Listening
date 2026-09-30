import 'dart:io';
import 'dart:typed_data';
import 'package:intensive_listening/ilp/ilp_creator.dart';
import 'package:intensive_listening/ilp/ilp_importer.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';

Future<void> main(List<String> args) async {
  final root = Directory('tests/fixtures');
  await root.create(recursive: true);
  if (args.isNotEmpty && args.first == '--verify') {
    final temporary = await Directory.systemTemp.createTemp('il2-dart-read-');
    try {
      final lesson = await IlpImporter(temporary).importFile(File(args[1]));
      if (lesson.cues.length != 2 || lesson.manifest.title != 'C# export 兼容课程') throw StateError('C# export differs');
      print('Dart imported C# package: ${lesson.manifest.title}, ${lesson.cues.length} cues');
    } finally { await temporary.delete(recursive: true); }
    return;
  }
  final temporary = await Directory.systemTemp.createTemp('il2-dart-export-');
  try {
    final header = ByteData(44);
    void ascii(int offset, String value) { for (var i = 0; i < value.length; i++) { header.setUint8(offset + i, value.codeUnitAt(i)); } }
    ascii(0, 'RIFF'); header.setUint32(4, 160036, Endian.little); ascii(8, 'WAVE'); ascii(12, 'fmt ');
    header.setUint32(16, 16, Endian.little); header.setUint16(20, 1, Endian.little); header.setUint16(22, 1, Endian.little);
    header.setUint32(24, 16000, Endian.little); header.setUint32(28, 32000, Endian.little); header.setUint16(32, 2, Endian.little);
    header.setUint16(34, 16, Endian.little); ascii(36, 'data'); header.setUint32(40, 160000, Endian.little);
    final audio = File('${temporary.path}/lesson.wav');
    await audio.writeAsBytes([...header.buffer.asUint8List(), ...List<int>.filled(160000, 0)]);
    final transcript = File('${temporary.path}/lesson.srt');
    await transcript.writeAsString('1\n00:00:00,000 --> 00:00:02,000\nWelcome to listening practice.\n\n2\n00:00:02,000 --> 00:00:05,000\nWrite down the key details.\n');
    const exercises = LessonExercises(
      materials: [LessonMaterial(id: 'm1', prompt: 'Listen carefully', cueIndexes: [0, 1], repeatedCueIndexes: [1], questionIds: ['q1'])],
      questions: [LessonQuestion(id: 'q1', title: 'What should you write down?', cueIndexes: [0, 1], materialId: 'm1', number: 1, options: ['The key details', 'Every word'], answerIndex: 0)],
      clozeWordIndexes: {1: {3, 4}},
    );
    final package = File('${root.path}/dart-course.ilp');
    await const IlpCreator().create(title: 'Dart compatibility 兼容课程', audioFile: audio, transcriptFile: transcript, outputFile: package,
      packageUuid: '11111111-1111-4111-8111-111111111111', packageVersion: 3, exercises: exercises);
    final manifest = await IlpImporter(temporary).readManifest(package);
    await File('${root.path}/dart-manifest.json').writeAsBytes(manifest.toBytes());
    print('Dart fixtures generated from current lib/');
  } finally { await temporary.delete(recursive: true); }
}
