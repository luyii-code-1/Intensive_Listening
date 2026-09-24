import 'dart:io';

import 'package:path/path.dart' as p;

String locateFfmpeg() {
  final override = Platform.environment['ILP_FFMPEG_PATH'];
  if (override != null && override.trim().isNotEmpty) return override.trim();
  return p.join(p.dirname(Platform.resolvedExecutable), 'ffmpeg.exe');
}
