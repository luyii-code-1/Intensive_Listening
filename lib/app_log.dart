import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import 'app_directories.dart';

class AppLog {
  AppLog._();

  @visibleForTesting
  static Directory? debugDirectory;
  static bool debugEnabled = false;
  static Future<void> _pending = Future<void>.value();

  static Future<Directory> directory() async {
    if (debugDirectory case final directory?) {
      await directory.create(recursive: true);
      return directory;
    }
    final data = await intensiveListeningDataDirectory();
    final logs = Directory(p.join(data.path, 'logs'));
    await logs.create(recursive: true);
    return logs;
  }

  static void warning(String message, [StackTrace? stack]) =>
      _write('WARN', message, stack);

  static void error(String message, [StackTrace? stack]) =>
      _write('ERROR', message, stack);

  static void debug(String message) {
    if (debugEnabled) _write('DEBUG', message);
  }

  static void notice(
    String title,
    String message, {
    bool isWarning = false,
    bool isError = false,
  }) {
    final text = '$title: $message';
    if (isError || RegExp(r'失败|无法|错误|未完成|未更新').hasMatch(title)) {
      error(text);
    } else if (isWarning || RegExp(r'警告|未绑定').hasMatch(title)) {
      warning(text);
    }
  }

  static void _write(String level, String message, [StackTrace? stack]) {
    if (Platform.environment.containsKey('FLUTTER_TEST') &&
        debugDirectory == null) {
      return;
    }
    final now = DateTime.now();
    final text =
        '${now.toIso8601String()} [$level] $message${stack == null ? '' : '\n$stack'}\n';
    _pending = _pending.then((_) async {
      try {
        final logs = await directory();
        final name = now.toIso8601String().substring(0, 10);
        await File(p.join(logs.path, '$name.log'))
            .writeAsString(text, mode: FileMode.append, flush: true);
      } catch (_) {
        // Logging must not prevent the application from running.
      }
    });
  }

  static Future<int> clear() async {
    await _pending;
    final logs = await directory();
    var bytes = 0;
    await for (final entry in logs.list(followLinks: false)) {
      if (entry is! File || p.extension(entry.path) != '.log') continue;
      bytes += await entry.length();
      await entry.delete();
    }
    return bytes;
  }
}

void installAppLogging() {
  final previousFlutterError = FlutterError.onError;
  FlutterError.onError = (details) {
    AppLog.error(details.exceptionAsString(), details.stack);
    previousFlutterError?.call(details);
  };

  final previousPlatformError = PlatformDispatcher.instance.onError;
  PlatformDispatcher.instance.onError = (error, stack) {
    AppLog.error('$error', stack);
    return previousPlatformError?.call(error, stack) ?? true;
  };

  final previousDebugPrint = debugPrint;
  debugPrint = (message, {wrapWidth}) {
    previousDebugPrint(message, wrapWidth: wrapWidth);
    if (message == null) return;
    if (RegExp(
      r'error|exception|failed',
      caseSensitive: false,
    ).hasMatch(message)) {
      AppLog.error(message);
    } else if (RegExp(r'warning', caseSensitive: false).hasMatch(message)) {
      AppLog.warning(message);
    } else {
      AppLog.debug(message);
    }
  };
}
