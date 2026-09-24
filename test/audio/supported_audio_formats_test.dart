import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/audio/supported_audio_formats.dart';

void main() {
  test('includes common standalone audio formats', () {
    expect(
      standaloneAudioExtensions,
      containsAll(['mp3', 'wav', 'm4a', 'aac', 'flac', 'ogg', 'opus', 'wma']),
    );
  });
}
