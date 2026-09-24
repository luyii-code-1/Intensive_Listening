import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/settings/api_configuration_archive.dart';
import 'package:intensive_listening/settings/app_settings.dart';

void main() {
  test('encrypted API config preserves every API field', () {
    final current = AppSettings.defaults().copyWith(
      cloudBaseUrl: 'https://api.example.test',
      cloudEndpoint: '/v1/asr',
      cloudModel: 'english-asr',
      cloudApiKey: 'secret-key',
      cloudTimeoutSeconds: 420,
      cloudConcurrency: 9,
      cloudLanguage: 'en',
      translateChineseToEnglish: false,
      localModelsDirectory: r'C:\models',
      selectedLocalModel: 'local-model',
      detectedLocalModels: const ['local-model'],
      fileAssociationEnabled: true,
      mcpEnabled: true,
    );
    final bytes = const ApiConfigurationArchive().export(
      current,
      password: 'correct horse battery staple',
    );
    final restored = const ApiConfigurationArchive().import(
      bytes,
      password: 'correct horse battery staple',
      current: AppSettings.defaults().copyWith(
        fileAssociationEnabled: true,
        mcpEnabled: true,
      ),
    );

    expect(restored.cloudBaseUrl, current.cloudBaseUrl);
    expect(restored.cloudEndpoint, current.cloudEndpoint);
    expect(restored.cloudModel, current.cloudModel);
    expect(restored.cloudApiKey, current.cloudApiKey);
    expect(restored.cloudTimeoutSeconds, current.cloudTimeoutSeconds);
    expect(restored.cloudConcurrency, current.cloudConcurrency);
    expect(restored.cloudLanguage, current.cloudLanguage);
    expect(restored.translateChineseToEnglish, isFalse);
    expect(restored.localModelsDirectory, current.localModelsDirectory);
    expect(restored.selectedLocalModel, current.selectedLocalModel);
    expect(restored.detectedLocalModels, current.detectedLocalModels);
    expect(restored.fileAssociationEnabled, isTrue);
    expect(restored.mcpEnabled, isTrue);
  });

  test('encrypted API config rejects the wrong password', () {
    final bytes = const ApiConfigurationArchive().export(
      AppSettings.defaults(),
      password: 'correct-password',
    );
    expect(
      () => const ApiConfigurationArchive().import(
        bytes,
        password: 'wrong-password',
        current: AppSettings.defaults(),
      ),
      throwsA(isA<ApiConfigurationArchiveException>()),
    );
  });
}
