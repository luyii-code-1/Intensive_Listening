import 'dart:io';

import 'package:path/path.dart' as p;

import '../ilp/ilp_creator.dart';
import '../ilp/ilp_importer.dart';
import '../ilp/ilp_models.dart';
import 'course_project.dart';

class ProjectDeliveryResult {
  const ProjectDeliveryResult({
    required this.packageVersion,
    required this.lesson,
  });

  final int packageVersion;
  final ImportedLesson lesson;
}

class ProjectDelivery {
  const ProjectDelivery();

  Future<File> createIlp(
    CourseProject project,
    File outputFile, {
    int? packageVersion,
  }) async {
    if (!project.hasAudio || !project.hasTranscript) {
      throw const IlpException(IlpError.missingFile, '课程需要音频和字幕才能生成精听包');
    }
    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-delivery-',
    );
    try {
      final transcript = File(
        p.join(temporaryDirectory.path, 'transcript.srt'),
      );
      await transcript.writeAsString(project.transcript, flush: true);
      return await const IlpCreator().create(
        title: project.title,
        audioFile: File(project.audioPath!),
        transcriptFile: transcript,
        outputFile: outputFile,
        packageUuid: project.packageUuid,
        packageVersion: packageVersion ?? project.packageVersion + 1,
        audioDuration: project.audioDuration,
        exercises: project.exercises,
      );
    } finally {
      if (await temporaryDirectory.exists()) {
        await temporaryDirectory.delete(recursive: true);
      }
    }
  }

  Future<ProjectDeliveryResult> addToLibrary(
    CourseProject project,
    Directory libraryDirectory,
  ) async {
    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-publish-',
    );
    try {
      final packageVersion = project.packageVersion + 1;
      final package = await createIlp(
        project,
        File(p.join(temporaryDirectory.path, 'lesson.ilp')),
        packageVersion: packageVersion,
      );
      final lesson = await IlpImporter(libraryDirectory)
          .importFile(package, replaceExisting: true);
      return ProjectDeliveryResult(
        packageVersion: packageVersion,
        lesson: lesson,
      );
    } finally {
      if (await temporaryDirectory.exists()) {
        await temporaryDirectory.delete(recursive: true);
      }
    }
  }
}
