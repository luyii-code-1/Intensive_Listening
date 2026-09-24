import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/settings/app_settings.dart';

void main() {
  late Directory temporaryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-settings-test-',
    );
    debugSettingsFile = File(
      '${temporaryDirectory.path}${Platform.pathSeparator}settings.json',
    );
  });

  tearDown(() async {
    debugSettingsFile = null;
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('settings survive a new store instance', () async {
    const settings = AppSettings(
      asrProvider: AsrProviderKind.cloud,
      cloudBaseUrl: 'https://example.invalid',
      cloudEndpoint: '/v1/audio/transcriptions',
      cloudModel: 'test-asr',
      cloudApiKey: 'test-key',
      cloudTimeoutSeconds: 240,
      cloudConcurrency: 8,
      cloudLanguage: 'en',
      translateChineseToEnglish: true,
      fileAssociationEnabled: true,
      fileAssociationPrompted: true,
      mcpEnabled: true,
      localModelsDirectory: '',
      selectedLocalModel: '',
      detectedLocalModels: [],
    );

    await const AppSettingsStore().save(settings);
    final restored = await const AppSettingsStore().load();

    expect(restored.cloudBaseUrl, settings.cloudBaseUrl);
    expect(restored.cloudEndpoint, settings.cloudEndpoint);
    expect(restored.cloudModel, settings.cloudModel);
    expect(restored.cloudApiKey, settings.cloudApiKey);
    expect(restored.cloudTimeoutSeconds, settings.cloudTimeoutSeconds);
    expect(restored.cloudConcurrency, settings.cloudConcurrency);
    expect(restored.cloudLanguage, 'en');
    expect(restored.translateChineseToEnglish, isTrue);
    expect(restored.fileAssociationEnabled, isTrue);
    expect(restored.fileAssociationPrompted, isTrue);
    expect(restored.mcpEnabled, isTrue);
    expect(restored.themeMode, 'system');
  });

  test('existing settings show the association prompt once', () {
    final existing = AppSettings.fromJson({'fileAssociationEnabled': false});
    expect(existing.fileAssociationPrompted, isFalse);

    final dismissed = AppSettings.fromJson(
      existing.copyWith(fileAssociationPrompted: true).toJson(),
    );
    expect(dismissed.fileAssociationEnabled, isFalse);
    expect(dismissed.fileAssociationPrompted, isTrue);
  });

  test('themeMode defaults to system and persists dark theme', () async {
    final defaults = AppSettings.defaults();
    expect(defaults.themeMode, 'system');

    final updated = defaults.copyWith(themeMode: 'dark');
    expect(updated.themeMode, 'dark');

    final json = updated.toJson();
    expect(json['themeMode'], 'dark');

    final restoredFromJson = AppSettings.fromJson(json);
    expect(restoredFromJson.themeMode, 'dark');

    await const AppSettingsStore().save(updated);
    final restoredFromStore = await const AppSettingsStore().load();
    expect(restoredFromStore.themeMode, 'dark');
  });

  test('playback preferences and agreement choice persist', () async {
    final settings = AppSettings.defaults().copyWith(
      skipOpeningPrompts: true,
      transcriptFontSize: 24,
      eulaAcceptedVersion: '2026-09-22',
    );
    await const AppSettingsStore().save(settings);
    final restored = await const AppSettingsStore().load();
    expect(restored.skipOpeningPrompts, isTrue);
    expect(restored.transcriptFontSize, 24);
    expect(restored.eulaAcceptedVersion, '2026-09-22');
  });

  test('new and older settings default to skipping opening prompts', () {
    expect(AppSettings.defaults().skipOpeningPrompts, isTrue);
    expect(AppSettings.fromJson(const {}).skipOpeningPrompts, isTrue);
    expect(
      AppSettings.fromJson(const {'skipOpeningPrompts': false})
          .skipOpeningPrompts,
      isFalse,
    );
  });
}
