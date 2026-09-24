import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import '../cancellation.dart';
import 'ilp_models.dart';

class _DigestSink implements Sink<Digest> {
  Digest? _digest;

  Digest get digest {
    final value = _digest;
    if (value == null) throw StateError('摘要尚未完成');
    return value;
  }

  @override
  void add(Digest data) => _digest = data;

  @override
  void close() {}
}

class FileHashes {
  const FileHashes({required this.md5, required this.sha256});

  final String md5;
  final String sha256;
}

Future<FileHashes> hashFileMd5AndSha256(
  File file, {
  void Function(int done, int total)? onProgress,
  bool Function()? isCanceled,
}) async {
  final total = await file.length();
  final md5Sink = _DigestSink();
  final sha256Sink = _DigestSink();
  final md5Input = md5.startChunkedConversion(md5Sink);
  final sha256Input = sha256.startChunkedConversion(sha256Sink);

  var done = 0;
  onProgress?.call(done, total);
  await for (final chunk in file.openRead()) {
    if (isCanceled?.call() ?? false) throw const OperationCanceled();
    md5Input.add(chunk);
    sha256Input.add(chunk);
    done += chunk.length;
    onProgress?.call(done, total);
  }
  md5Input.close();
  sha256Input.close();
  return FileHashes(
    md5: md5Sink.digest.toString(),
    sha256: sha256Sink.digest.toString(),
  );
}

Future<String> hashFileSha256(
  File file, {
  void Function(int done, int total)? onProgress,
  bool Function()? isCanceled,
}) async {
  final total = await file.length();
  final sink = _DigestSink();
  final input = sha256.startChunkedConversion(sink);

  var done = 0;
  onProgress?.call(done, total);
  await for (final chunk in file.openRead()) {
    if (isCanceled?.call() ?? false) throw const OperationCanceled();
    input.add(chunk);
    done += chunk.length;
    onProgress?.call(done, total);
  }
  input.close();
  return sink.digest.toString();
}

Future<void> verifyFileSha256(
  File file,
  String expected, {
  bool Function()? isCanceled,
}) async {
  final actual = await hashFileSha256(file, isCanceled: isCanceled);
  if (actual != expected) {
    throw IlpException(
      IlpError.hashMismatch,
      '${p.basename(file.path)} 完整性校验失败',
    );
  }
}
