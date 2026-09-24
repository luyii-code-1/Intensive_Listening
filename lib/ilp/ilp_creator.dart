import 'dart:io';
import 'dart:isolate';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'ilp_models.dart';
import 'lesson_exercises.dart';
import 'srt_parser.dart';

class IlpCreator {
  const IlpCreator();

  Future<File> create({
    required String title,
    required File audioFile,
    required File transcriptFile,
    required File outputFile,
    required String packageUuid,
    required int packageVersion,
    Duration? audioDuration,
    LessonExercises exercises = const LessonExercises(),
  }) async {
    return Isolate.run(
      () => _createIlp(
        title: title,
        audioFile: audioFile,
        transcriptFile: transcriptFile,
        outputFile: outputFile,
        packageUuid: packageUuid,
        packageVersion: packageVersion,
        audioDuration: audioDuration,
        exercises: exercises,
      ),
    );
  }
}

Future<File> _createIlp({
  required String title,
  required File audioFile,
  required File transcriptFile,
  required File outputFile,
  required String packageUuid,
  required int packageVersion,
  required Duration? audioDuration,
  required LessonExercises exercises,
}) async {
  final normalizedTitle = title.trim();
  if (normalizedTitle.isEmpty) {
    throw const IlpException(IlpError.invalidManifest, '请输入课程标题');
  }
  if (!await audioFile.exists()) {
    throw const IlpException(IlpError.missingFile, '请选择音频文件');
  }
  if (!await transcriptFile.exists()) {
    throw const IlpException(IlpError.missingFile, '请选择 SRT 文件');
  }

  final extension = p
      .extension(audioFile.path)
      .replaceFirst('.', '')
      .toLowerCase();
  if (!IlpManifest.supportedAudioExtensions.contains(extension)) {
    throw const IlpException(IlpError.invalidPath, '音频格式不在当前支持范围内');
  }

  final transcriptBytes = await transcriptFile.readAsBytes();
  final cues = SrtParser.parse(transcriptBytes, const Duration(days: 7));
  final duration = audioDuration != null && audioDuration > cues.last.end
      ? audioDuration
      : cues.last.end;
  final audioBytes = await audioFile.readAsBytes();
  final audioPath = 'audio.$extension';
  final manifest = IlpManifest(
    formatVersion: IlpManifest.supportedVersion,
    title: normalizedTitle,
    duration: duration,
    audioPath: audioPath,
    transcriptPath: IlpManifest.canonicalTranscriptPath,
    audioSha256: sha256.convert(audioBytes).toString(),
    transcriptSha256: sha256.convert(transcriptBytes).toString(),
    packageUuid: packageUuid,
    packageVersion: packageVersion,
    exercises: exercises,
  );

  final archive = Archive()
    ..add(ArchiveFile.bytes('manifest.json', manifest.toBytes()))
    ..add(ArchiveFile.bytes(audioPath, audioBytes))
    ..add(
      ArchiveFile.bytes(IlpManifest.canonicalTranscriptPath, transcriptBytes),
    );

  await outputFile.parent.create(recursive: true);
  await outputFile.writeAsBytes(ZipEncoder().encodeBytes(archive), flush: true);
  return outputFile;
}
