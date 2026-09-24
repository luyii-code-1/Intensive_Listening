import 'dart:convert';
import 'dart:typed_data';

import 'package:archive/archive.dart';

import 'app_settings.dart';

class ApiConfigurationArchiveException implements Exception {
  const ApiConfigurationArchiveException(this.message);

  final String message;

  @override
  String toString() => message;
}

class ApiConfigurationArchive {
  const ApiConfigurationArchive();

  static const fileName = 'api-config.json';

  Uint8List export(AppSettings settings, {required String password}) {
    if (password.isEmpty) {
      throw const ApiConfigurationArchiveException('必须设置导出密码');
    }
    final payload = {
      'format': 'intensive-listening-api-config',
      'version': 1,
      'exportedAt': DateTime.now().toUtc().toIso8601String(),
      'api': {
        'endpoint': settings.cloudBaseUrl,
        'key': settings.cloudApiKey,
        'path': settings.cloudEndpoint,
        'name': settings.cloudModel,
        'provider': settings.asrProvider.name,
        'timeoutSeconds': settings.cloudTimeoutSeconds,
        'concurrency': settings.cloudConcurrency,
        'language': settings.cloudLanguage,
        'translateChineseToEnglish': settings.translateChineseToEnglish,
        'localModelsDirectory': settings.localModelsDirectory,
        'selectedLocalModel': settings.selectedLocalModel,
        'detectedLocalModels': settings.detectedLocalModels,
      },
    };
    final archive = Archive()
      ..addFile(
        ArchiveFile.string(
          fileName,
          const JsonEncoder.withIndent('  ').convert(payload),
        ),
      );
    return ZipEncoder(password: password).encodeBytes(archive);
  }

  AppSettings import(
    List<int> bytes, {
    required String password,
    required AppSettings current,
  }) {
    if (password.isEmpty) {
      throw const ApiConfigurationArchiveException('请输入配置包密码');
    }
    try {
      final archive = ZipDecoder().decodeBytes(
        bytes,
        verify: true,
        password: password,
      );
      final entry = archive.find(fileName);
      final content = entry?.readBytes();
      if (entry == null || !entry.isFile || content == null) {
        throw const ApiConfigurationArchiveException('配置包缺少 api-config.json');
      }
      final decoded = jsonDecode(utf8.decode(content));
      if (decoded is! Map<String, dynamic> ||
          decoded['format'] != 'intensive-listening-api-config' ||
          decoded['version'] != 1 ||
          decoded['api'] is! Map<String, dynamic>) {
        throw const ApiConfigurationArchiveException('配置包格式无效');
      }
      final api = decoded['api'] as Map<String, dynamic>;
      return current.copyWith(
        asrProvider: AsrProviderKind.values.firstWhere(
          (value) => value.name == api['provider'],
          orElse: () => AsrProviderKind.cloud,
        ),
        cloudBaseUrl: _string(api, 'endpoint', current.cloudBaseUrl),
        cloudEndpoint: _string(api, 'path', current.cloudEndpoint),
        cloudModel: _string(api, 'name', current.cloudModel),
        cloudApiKey: _string(api, 'key', current.cloudApiKey),
        cloudTimeoutSeconds: _integer(
          api,
          'timeoutSeconds',
          current.cloudTimeoutSeconds,
        ).clamp(30, 1800),
        cloudConcurrency: _integer(
          api,
          'concurrency',
          current.cloudConcurrency,
        ).clamp(1, 10),
        cloudLanguage: _string(api, 'language', 'en'),
        translateChineseToEnglish: api['translateChineseToEnglish'] is bool
            ? api['translateChineseToEnglish'] as bool
            : current.translateChineseToEnglish,
        localModelsDirectory: _string(
          api,
          'localModelsDirectory',
          current.localModelsDirectory,
        ),
        selectedLocalModel: _string(
          api,
          'selectedLocalModel',
          current.selectedLocalModel,
        ),
        detectedLocalModels: (api['detectedLocalModels'] as List? ?? const [])
            .whereType<String>()
            .toList(growable: false),
      );
    } on ApiConfigurationArchiveException {
      rethrow;
    } catch (_) {
      throw const ApiConfigurationArchiveException('密码错误或配置包已经损坏');
    }
  }

  String _string(Map<String, dynamic> source, String key, String fallback) {
    final value = source[key];
    return value is String ? value : fallback;
  }

  int _integer(Map<String, dynamic> source, String key, int fallback) {
    final value = source[key];
    if (value is num) return value.round();
    return int.tryParse('$value') ?? fallback;
  }
}
