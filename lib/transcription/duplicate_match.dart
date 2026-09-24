import '../ilp/ilp_models.dart';

enum DuplicateCase {
  identicalPackage,
  packageUpdate,
  sharedAudioLesson,
  sharedAudioJob,
}

class DuplicateMatch {
  const DuplicateMatch({
    required this.duplicateCase,
    required this.existingTitle,
    this.lesson,
    this.lessonId,
    this.jobId,
  });

  final DuplicateCase duplicateCase;
  final String existingTitle;
  final ImportedLesson? lesson;
  final String? lessonId;
  final String? jobId;
}

DuplicateMatch? findLessonDuplicate(
  List<ImportedLesson> lessons,
  IlpManifest manifest,
) {
  final id = ilpPackageId(manifest);
  for (final lesson in lessons) {
    if (lesson.id == id &&
        lesson.manifest.packageVersion == manifest.packageVersion) {
      return DuplicateMatch(
        duplicateCase: DuplicateCase.identicalPackage,
        existingTitle: lesson.manifest.title,
        lesson: lesson,
        lessonId: lesson.id,
      );
    }
    if (lesson.id == id) {
      return DuplicateMatch(
        duplicateCase: DuplicateCase.packageUpdate,
        existingTitle: lesson.manifest.title,
        lesson: lesson,
        lessonId: lesson.id,
      );
    }
  }
  return null;
}
