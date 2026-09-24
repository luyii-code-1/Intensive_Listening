import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/cancellation.dart';
import 'package:intensive_listening/ilp/file_hashing.dart';
import 'package:intensive_listening/ilp/ilp_models.dart';

void main() {
  late Directory temporaryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp('hashing-test-');
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  Future<File> writeFile(String name, List<int> bytes) async {
    final file = File('${temporaryDirectory.path}/$name');
    await file.writeAsBytes(bytes, flush: true);
    return file;
  }

  test('matches a one-shot sha256 digest', () async {
    final bytes = utf8.encode('intensive listening');
    final file = await writeFile('sample.bin', bytes);

    expect(await hashFileSha256(file), sha256.convert(bytes).toString());
  });

  test('reports monotonic progress that ends at the total', () async {
    final file = await writeFile('large.bin', List<int>.filled(300000, 7));
    final seen = <int>[];
    var total = -1;

    await hashFileSha256(
      file,
      onProgress: (done, length) {
        total = length;
        seen.add(done);
      },
    );

    expect(total, 300000);
    expect(seen.first, 0);
    expect(seen.last, 300000);
    for (var index = 1; index < seen.length; index++) {
      expect(seen[index], greaterThanOrEqualTo(seen[index - 1]));
    }
  });

  test('hashes an empty file', () async {
    final file = await writeFile('empty.bin', const []);

    expect(await hashFileSha256(file), sha256.convert(const []).toString());
  });

  test('stops when canceled', () async {
    final file = await writeFile('large.bin', List<int>.filled(300000, 7));

    await expectLater(
      hashFileSha256(file, isCanceled: () => true),
      throwsA(isA<OperationCanceled>()),
    );
  });

  test('verify accepts a matching digest', () async {
    final bytes = utf8.encode('intensive listening');
    final file = await writeFile('sample.bin', bytes);

    await expectLater(
      verifyFileSha256(file, sha256.convert(bytes).toString()),
      completes,
    );
  });

  test('verify rejects a mismatched digest', () async {
    final file = await writeFile('sample.bin', utf8.encode('actual'));

    await expectLater(
      verifyFileSha256(file, sha256.convert(utf8.encode('other')).toString()),
      throwsA(
        isA<IlpException>()
            .having((error) => error.code, 'code', IlpError.hashMismatch)
            .having((error) => error.message, 'message', contains('完整性校验失败')),
      ),
    );
  });
}
