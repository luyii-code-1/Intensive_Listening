import 'dart:io';

class FileAssociationException implements Exception {
  const FileAssociationException(this.message);

  final String message;

  @override
  String toString() => message;
}

class IlpFileAssociation {
  const IlpFileAssociation();

  static const _className = 'IntensiveListening.ilp';

  Future<void> apply(bool enabled) async {
    if (!Platform.isWindows) return;
    if (enabled) {
      final launcher = Platform.environment['ILP_PORTABLE_LAUNCHER'];
      final executable = launcher != null && launcher.isNotEmpty
          ? launcher
          : Platform.resolvedExecutable;
      await _regAdd(r'HKCU\Software\Classes\.ilp', _className);
      await _regAdd(
        r'HKCU\Software\Classes\IntensiveListening.ilp',
        'Intensive Listening 精听包',
      );
      await _regAdd(
        r'HKCU\Software\Classes\IntensiveListening.ilp\DefaultIcon',
        '"$executable",0',
      );
      await _regAdd(
        r'HKCU\Software\Classes\IntensiveListening.ilp\shell\open\command',
        '"$executable" "%1"',
      );
    } else {
      await _regDelete(r'HKCU\Software\Classes\IntensiveListening.ilp');
      await _regDelete(r'HKCU\Software\Classes\.ilp');
    }
    await Process.run('ie4uinit.exe', const ['-show']);
  }

  Future<void> _regAdd(String key, String value) async {
    final result = await Process.run('reg.exe', [
      'add',
      key,
      '/ve',
      '/d',
      value,
      '/f',
    ]);
    if (result.exitCode != 0) {
      throw FileAssociationException('${result.stderr}'.trim());
    }
  }

  Future<void> _regDelete(String key) async {
    final result = await Process.run('reg.exe', ['delete', key, '/f']);
    // reg.exe uses exit code 1 when the key is already absent. Treat disabling
    // an association as idempotent on every Windows display language.
    if (result.exitCode != 0 && result.exitCode != 1) {
      throw FileAssociationException('${result.stderr}'.trim());
    }
  }
}
