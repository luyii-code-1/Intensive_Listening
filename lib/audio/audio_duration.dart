import 'package:media_kit/media_kit.dart';

/// Reads a media file's duration with a throwaway player.
///
/// Returns null when the player cannot start, the file cannot be opened, or no
/// duration is reported, so callers degrade to indeterminate progress instead
/// of failing the pick.
Future<Duration?> probeAudioDuration(
  String path, {
  Duration timeout = const Duration(seconds: 10),
}) async {
  Player? player;
  try {
    player = Player();
    final reportedDuration = player.stream.duration
        .firstWhere((value) => value > Duration.zero)
        .timeout(timeout, onTimeout: () => Duration.zero)
        .catchError((Object _) => Duration.zero);
    await player
        .open(Media(Uri.file(path).toString()), play: false)
        .timeout(timeout);
    final duration = player.state.duration;
    if (duration > Duration.zero) return duration;
    final delayedDuration = await reportedDuration;
    return delayedDuration > Duration.zero ? delayedDuration : null;
  } catch (_) {
    return null;
  } finally {
    await player?.dispose();
  }
}
