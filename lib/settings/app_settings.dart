import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';
import '../asr/asr_client.dart';

@visibleForTesting
File? debugSettingsFile;

enum AsrProviderKind {
  cloud,
  local;

  String get label => switch (this) {
    AsrProviderKind.cloud => '云端 API',
    AsrProviderKind.local => '本地模型',
  };
}

class AppSettings {
  const AppSettings({
    required this.asrProvider,
    required this.cloudBaseUrl,
    required this.cloudEndpoint,
    required this.cloudModel,
    required this.cloudApiKey,
    required this.cloudTimeoutSeconds,
    required this.cloudConcurrency,
    required this.cloudLanguage,
    required this.translateChineseToEnglish,
    required this.fileAssociationEnabled,
    this.fileAssociationPrompted = false,
    required this.mcpEnabled,
    required this.localModelsDirectory,
    required this.selectedLocalModel,
    required this.detectedLocalModels,
    this.themeMode = 'system',
    this.skipOpeningPrompts = true,
    this.transcriptFontSize = 18,
    this.eulaAcceptedVersion = '',
  });

  factory AppSettings.defaults() {
    final config = AsrConfig.fromEnvironment();
    return AppSettings(
      asrProvider: AsrProviderKind.cloud,
      cloudBaseUrl: config.baseUrl,
      cloudEndpoint: config.endpoint,
      cloudModel: config.model,
      cloudApiKey: config.apiKey,
      cloudTimeoutSeconds: 180,
      cloudConcurrency: 10,
      cloudLanguage: 'en',
      translateChineseToEnglish: true,
      fileAssociationEnabled: false,
      fileAssociationPrompted: false,
      mcpEnabled: false,
      localModelsDirectory: '',
      selectedLocalModel: '',
      detectedLocalModels: const [],
      themeMode: 'system',
      skipOpeningPrompts: true,
      transcriptFontSize: 18,
      eulaAcceptedVersion: '',
    );
  }

  factory AppSettings.fromJson(Map<String, dynamic> json) {
    return AppSettings(
      asrProvider: AsrProviderKind.cloud,
      cloudBaseUrl: _stringValue(
        json['cloudBaseUrl'],
        AsrConfig.defaultBaseUrl,
      ),
      cloudEndpoint: _stringValue(
        json['cloudEndpoint'],
        AsrConfig.defaultEndpoint,
      ),
      cloudModel: _stringValue(json['cloudModel'], AsrConfig.defaultModel),
      cloudApiKey: _stringValue(json['cloudApiKey'], ''),
      cloudTimeoutSeconds: _intValue(json['cloudTimeoutSeconds'], 180),
      cloudConcurrency: _intValue(json['cloudConcurrency'], 10).clamp(1, 10),
      cloudLanguage: 'en',
      translateChineseToEnglish: json['translateChineseToEnglish'] != false,
      fileAssociationEnabled: json['fileAssociationEnabled'] == true,
      fileAssociationPrompted: json['fileAssociationPrompted'] == true,
      mcpEnabled: json['mcpEnabled'] == true,
      localModelsDirectory: _stringValue(json['localModelsDirectory'], ''),
      selectedLocalModel: _stringValue(json['selectedLocalModel'], ''),
      detectedLocalModels: _stringListValue(json['detectedLocalModels']),
      themeMode: _stringValue(json['themeMode'], 'system'),
      skipOpeningPrompts: json['skipOpeningPrompts'] != false,
      transcriptFontSize: _intValue(
        json['transcriptFontSize'],
        18,
      ).clamp(14, 28),
      eulaAcceptedVersion: _stringValue(json['eulaAcceptedVersion'], ''),
    );
  }

  final AsrProviderKind asrProvider;
  final String cloudBaseUrl;
  final String cloudEndpoint;
  final String cloudModel;
  final String cloudApiKey;
  final int cloudTimeoutSeconds;
  final int cloudConcurrency;
  final String cloudLanguage;
  final bool translateChineseToEnglish;
  final bool fileAssociationEnabled;
  final bool fileAssociationPrompted;
  final bool mcpEnabled;
  final String localModelsDirectory;
  final String selectedLocalModel;
  final List<String> detectedLocalModels;
  final String themeMode;
  final bool skipOpeningPrompts;
  final int transcriptFontSize;
  final String eulaAcceptedVersion;

  AsrConfig get cloudAsrConfig => AsrConfig(
    baseUrl: cloudBaseUrl,
    endpoint: cloudEndpoint,
    model: cloudModel,
    apiKey: cloudApiKey,
    language: cloudLanguage,
  );

  bool get cloudReady => cloudAsrConfig.isComplete;

  bool get localReady =>
      localModelsDirectory.trim().isNotEmpty &&
      selectedLocalModel.trim().isNotEmpty;

  /// Stable transcription-output identity. Secrets, concurrency and timeouts
  /// are intentionally excluded because they do not change the transcript.
  String get asrCacheProfile => jsonEncode({
    'provider': asrProvider.name,
    'baseUrl': cloudBaseUrl.trim(),
    'endpoint': cloudEndpoint.trim(),
    'model': cloudModel.trim(),
    'language': cloudLanguage.trim(),
    'translateChineseToEnglish': translateChineseToEnglish,
    'localModel': selectedLocalModel.trim(),
    'segmentMaxSeconds': 120,
  });

  AppSettings copyWith({
    AsrProviderKind? asrProvider,
    String? cloudBaseUrl,
    String? cloudEndpoint,
    String? cloudModel,
    String? cloudApiKey,
    int? cloudTimeoutSeconds,
    int? cloudConcurrency,
    String? cloudLanguage,
    bool? translateChineseToEnglish,
    bool? fileAssociationEnabled,
    bool? fileAssociationPrompted,
    bool? mcpEnabled,
    String? localModelsDirectory,
    String? selectedLocalModel,
    List<String>? detectedLocalModels,
    String? themeMode,
    bool? skipOpeningPrompts,
    int? transcriptFontSize,
    String? eulaAcceptedVersion,
  }) {
    return AppSettings(
      asrProvider: asrProvider ?? this.asrProvider,
      cloudBaseUrl: cloudBaseUrl ?? this.cloudBaseUrl,
      cloudEndpoint: cloudEndpoint ?? this.cloudEndpoint,
      cloudModel: cloudModel ?? this.cloudModel,
      cloudApiKey: cloudApiKey ?? this.cloudApiKey,
      cloudTimeoutSeconds: cloudTimeoutSeconds ?? this.cloudTimeoutSeconds,
      cloudConcurrency: cloudConcurrency ?? this.cloudConcurrency,
      cloudLanguage: cloudLanguage ?? this.cloudLanguage,
      translateChineseToEnglish:
          translateChineseToEnglish ?? this.translateChineseToEnglish,
      fileAssociationEnabled:
          fileAssociationEnabled ?? this.fileAssociationEnabled,
      fileAssociationPrompted:
          fileAssociationPrompted ?? this.fileAssociationPrompted,
      mcpEnabled: mcpEnabled ?? this.mcpEnabled,
      localModelsDirectory: localModelsDirectory ?? this.localModelsDirectory,
      selectedLocalModel: selectedLocalModel ?? this.selectedLocalModel,
      detectedLocalModels: detectedLocalModels ?? this.detectedLocalModels,
      themeMode: themeMode ?? this.themeMode,
      skipOpeningPrompts: skipOpeningPrompts ?? this.skipOpeningPrompts,
      transcriptFontSize: transcriptFontSize ?? this.transcriptFontSize,
      eulaAcceptedVersion: eulaAcceptedVersion ?? this.eulaAcceptedVersion,
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'asrProvider': asrProvider.name,
      'cloudBaseUrl': cloudBaseUrl,
      'cloudEndpoint': cloudEndpoint,
      'cloudModel': cloudModel,
      'cloudApiKey': cloudApiKey,
      'cloudTimeoutSeconds': cloudTimeoutSeconds,
      'cloudConcurrency': cloudConcurrency,
      'cloudLanguage': cloudLanguage,
      'translateChineseToEnglish': translateChineseToEnglish,
      'fileAssociationEnabled': fileAssociationEnabled,
      'fileAssociationPrompted': fileAssociationPrompted,
      'mcpEnabled': mcpEnabled,
      'localModelsDirectory': localModelsDirectory,
      'selectedLocalModel': selectedLocalModel,
      'detectedLocalModels': detectedLocalModels,
      'themeMode': themeMode,
      'skipOpeningPrompts': skipOpeningPrompts,
      'transcriptFontSize': transcriptFontSize,
      'eulaAcceptedVersion': eulaAcceptedVersion,
    };
  }

  static String _stringValue(Object? value, String fallback) {
    return value is String ? value : fallback;
  }

  static int _intValue(Object? value, int fallback) {
    if (value is int) return value;
    if (value is num) return value.round();
    if (value is String) return int.tryParse(value) ?? fallback;
    return fallback;
  }

  static List<String> _stringListValue(Object? value) {
    if (value is! List) return const [];
    return value.whereType<String>().toList(growable: false);
  }
}

class AppSettingsStore {
  const AppSettingsStore();

  Future<AppSettings> load() async {
    final file = await _settingsFile();
    if (!await file.exists()) return AppSettings.defaults();
    try {
      final decoded = jsonDecode(await file.readAsString());
      if (decoded is Map<String, dynamic>) return AppSettings.fromJson(decoded);
    } catch (_) {
      return AppSettings.defaults();
    }
    return AppSettings.defaults();
  }

  Future<void> save(AppSettings settings) async {
    final file = await _settingsFile();
    await file.parent.create(recursive: true);
    const encoder = JsonEncoder.withIndent('  ');
    await file.writeAsString(encoder.convert(settings.toJson()), flush: true);
  }

  Future<File> _settingsFile() async {
    final override = debugSettingsFile;
    if (override != null) return override;
    final supportDirectory = await intensiveListeningDataDirectory();
    return File(p.join(supportDirectory.path, 'settings.json'));
  }
}

Future<List<String>> scanLocalModels(String directoryPath) async {
  final root = Directory(directoryPath.trim());
  if (!await root.exists()) return const [];

  final names = <String>{};
  await for (final entity in root.list(followLinks: false)) {
    if (entity is Directory) {
      names.add(p.basename(entity.path));
      continue;
    }
    if (entity is File && _looksLikeModelFile(entity.path)) {
      names.add(p.basenameWithoutExtension(entity.path));
    }
  }

  final sorted = names.toList()..sort((a, b) => a.compareTo(b));
  return sorted;
}

bool _looksLikeModelFile(String path) {
  final extension = p.extension(path).toLowerCase();
  return extension == '.bin' ||
      extension == '.gguf' ||
      extension == '.onnx' ||
      extension == '.xml';
}
