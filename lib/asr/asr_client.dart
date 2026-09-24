import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:http/http.dart' as http;

import '../cancellation.dart';
import '../ilp/ilp_models.dart';
import 'asr_http_client.dart';

class AsrConfig {
  const AsrConfig({
    required this.baseUrl,
    required this.endpoint,
    required this.model,
    required this.apiKey,
    this.language = 'en',
  });

  static const defaultBaseUrl = 'https://dashscope.aliyuncs.com';
  static const defaultEndpoint =
      '/api/v1/services/aigc/multimodal-generation/generation';
  static const defaultModel = 'qwen-audio-3.0-asr-flash';

  final String baseUrl;
  final String endpoint;
  final String model;
  final String apiKey;
  final String language;

  bool get isComplete =>
      baseUrl.trim().isNotEmpty &&
      endpoint.trim().isNotEmpty &&
      model.trim().isNotEmpty &&
      apiKey.trim().isNotEmpty;

  static AsrConfig fromEnvironment() {
    return AsrConfig(
      baseUrl: Platform.environment['ILP_ASR_BASE_URL'] ?? defaultBaseUrl,
      endpoint: Platform.environment['ILP_ASR_ENDPOINT'] ?? defaultEndpoint,
      model: Platform.environment['ILP_ASR_MODEL'] ?? defaultModel,
      apiKey: Platform.environment['ILP_ASR_API_KEY'] ?? '',
    );
  }
}

enum AsrStage { decoding, slicing, uploading, recognizing, formatting, merging }

class AsrProgress {
  const AsrProgress({
    required this.stage,
    this.stageFraction,
    this.bytesDone = 0,
    this.bytesTotal = 0,
    this.segmentIndex,
    this.segmentTotal,
  });

  final AsrStage stage;
  final double? stageFraction;
  final int bytesDone;
  final int bytesTotal;
  final int? segmentIndex;
  final int? segmentTotal;
}

typedef AsrProgressCallback = void Function(AsrProgress progress);

class AsrClient {
  const AsrClient({
    this.httpClient,
    this.timeout = const Duration(minutes: 20),
  });

  final http.Client? httpClient;
  final Duration timeout;

  Future<String> transcribeToSrt({
    required AsrConfig config,
    required File audioFile,
    AsrProgressCallback? onProgress,
    Future<void>? abortTrigger,
  }) async {
    if (!config.isComplete) {
      throw const AsrException('请填写 ASR 配置。');
    }
    if (!await audioFile.exists()) {
      throw const AsrException('请选择音频文件。');
    }

    final client = httpClient ?? await createAsrHttpClient();
    try {
      final audioBytes = await audioFile.readAsBytes();
      onProgress?.call(
        AsrProgress(
          stage: AsrStage.uploading,
          stageFraction: 0.0,
          bytesTotal: audioBytes.length,
        ),
      );
      final endpoint = transcriptionEndpoint(config.baseUrl, config.endpoint);
      final request = abortTrigger == null
          ? http.Request('POST', endpoint)
          : http.AbortableRequest('POST', endpoint, abortTrigger: abortTrigger);
      request.headers.addAll({
        'Authorization': 'Bearer ${config.apiKey.trim()}',
        'Content-Type': 'application/json',
      });
      request.body = jsonEncode({
        'model': config.model.trim(),
        'input': {
          'messages': [
            {
              'role': 'user',
              'content': [
                {'audio': 'data:audio/wav;base64,${base64Encode(audioBytes)}'},
              ],
            },
          ],
        },
        'parameters': {'format': 'wav', 'language': config.language},
      });

      final streamed = await client.send(request).timeout(timeout);
      onProgress?.call(
        AsrProgress(
          stage: AsrStage.uploading,
          stageFraction: 1.0,
          bytesDone: audioBytes.length,
          bytesTotal: audioBytes.length,
        ),
      );
      onProgress?.call(const AsrProgress(stage: AsrStage.recognizing));
      final body = await streamed.stream.bytesToString();
      if (streamed.statusCode < 200 || streamed.statusCode >= 300) {
        if (_isNoWordsResponse(streamed.statusCode, body)) {
          onProgress?.call(const AsrProgress(stage: AsrStage.formatting));
          return '';
        }
        throw AsrException(_serverError(streamed.statusCode, body));
      }
      onProgress?.call(const AsrProgress(stage: AsrStage.formatting));
      return AsrTranscription.fromDashScopeResponseBody(body).toSrt();
    } on AsrException {
      rethrow;
    } on OperationCanceled {
      rethrow;
    } on http.RequestAbortedException {
      throw const OperationCanceled();
    } on TimeoutException {
      throw const AsrException('ASR 请求超时。');
    } on FormatException catch (error) {
      throw AsrException(error.message);
    } on http.ClientException catch (error) {
      throw AsrException('网络连接失败：${error.message}');
    } on SocketException {
      throw const AsrException('网络连接失败，请检查网络或服务地址。');
    } catch (error) {
      throw AsrException('转写失败：$error');
    } finally {
      if (httpClient == null) client.close();
    }
  }
}

Uri transcriptionEndpoint(String baseUrl, [String? endpoint]) {
  final base = Uri.parse(baseUrl.trim());
  final endpointValue = endpoint?.trim();
  if (endpointValue == null || endpointValue.isEmpty) return base;
  final endpointUri = Uri.parse(endpointValue);
  if (endpointUri.hasScheme) return endpointUri;
  final pathSegments = endpointUri.pathSegments
      .where((segment) => segment.isNotEmpty)
      .toList();
  return base.replace(
    pathSegments: pathSegments,
    queryParameters: null,
    fragment: null,
  );
}

bool _isNoWordsResponse(int statusCode, String body) {
  if (statusCode != 400) return false;
  try {
    final decoded = jsonDecode(body);
    if (decoded is! Map) return false;
    return decoded['code'] == 'ASR_RESPONSE_HAVE_NO_WORDS' ||
        decoded['message'] == 'ASR_RESPONSE_HAVE_NO_WORDS';
  } on FormatException {
    return false;
  }
}

String _serverError(int statusCode, String body) {
  try {
    final decoded = jsonDecode(body);
    if (decoded is Map) {
      final message = decoded['message'] ?? decoded['code'];
      if (message is String && message.trim().isNotEmpty) {
        return 'ASR 请求失败（$statusCode）：${message.trim()}';
      }
    }
  } catch (_) {}
  return 'ASR 请求失败，状态码 $statusCode。';
}

class AsrTranscription {
  const AsrTranscription(this.cues);

  final List<SrtCue> cues;

  factory AsrTranscription.fromResponseBody(String body) {
    final decoded = jsonDecode(body);
    final segments = _findSegments(decoded);
    if (segments == null || segments.isEmpty) {
      throw const FormatException('ASR 返回结果没有包含分句时间。');
    }

    final cues = <SrtCue>[];
    for (final segment in segments) {
      final cue = _cueFromSegment(segment);
      if (cue != null) cues.add(cue);
    }
    if (cues.isEmpty) {
      throw const FormatException('ASR 返回结果没有可用字幕。');
    }
    cues.sort((left, right) => left.start.compareTo(right.start));
    return AsrTranscription(List.unmodifiable(cues));
  }

  factory AsrTranscription.fromDashScopeResponseBody(String body) {
    final decoded = jsonDecode(body);
    final sentences = _findSentences(decoded);
    if (sentences == null) {
      throw const FormatException('ASR 返回结果没有包含句子时间。');
    }

    final cues = <SrtCue>[];
    for (final sentence in sentences.whereType<Map>()) {
      cues.addAll(_cuesFromSentence(sentence));
    }
    cues.sort((left, right) => left.start.compareTo(right.start));
    return AsrTranscription(List.unmodifiable(cues));
  }

  String toSrt() {
    return cues.indexed
        .map(
          (entry) =>
              '${entry.$1 + 1}\n${formatSrtTime(entry.$2.start)} --> ${formatSrtTime(entry.$2.end)}\n${entry.$2.text}',
        )
        .join('\n\n');
  }

  static List<dynamic>? _findSegments(Object? value, [int depth = 0]) {
    if (depth > 4) return null;
    if (value is List &&
        value.every((item) => item is Map && _looksLikeSegment(item))) {
      return value;
    }
    if (value is Map<String, dynamic>) {
      final direct = value['segments'];
      if (direct is List) return direct;
      for (final child in value.values) {
        final result = _findSegments(child, depth + 1);
        if (result != null) return result;
      }
    }
    return null;
  }

  static List<dynamic>? _findSentences(Object? value, [int depth = 0]) {
    if (depth > 6) return null;
    if (value is Map) {
      final sentence = value['sentence'];
      if (sentence is List) return sentence;
      if (sentence is Map) return [sentence];
      for (final child in value.values) {
        final result = _findSentences(child, depth + 1);
        if (result != null) return result;
      }
    }
    if (value is List) {
      for (final child in value) {
        final result = _findSentences(child, depth + 1);
        if (result != null) return result;
      }
    }
    return null;
  }

  static List<SrtCue> _cuesFromSentence(Map<dynamic, dynamic> sentence) {
    final words = sentence['words'];
    if (words is! List || words.isEmpty) {
      final cue = _cueFromSegment(sentence);
      return cue == null ? const [] : [cue];
    }

    final cues = <SrtCue>[];
    final buffer = <String>[];
    int? beginMs;
    var endMs = 0;
    for (final value in words.whereType<Map>()) {
      final text = value['text'];
      if (text is! String || text.isEmpty) continue;
      final punctuation = value['punctuation'];
      final piece = '$text${punctuation is String ? punctuation : ''}';
      buffer.add(piece);
      beginMs ??= _milliseconds(value['begin_time']) ?? 0;
      endMs = _milliseconds(value['end_time']) ?? beginMs;
      final joined = _joinWords(buffer);
      final endsSentence =
          punctuation is String &&
          punctuation.isNotEmpty &&
          '.?!。？！…'.contains(punctuation[0]);
      if (endsSentence || joined.length >= 80 || endMs - beginMs >= 8000) {
        _appendCue(cues, beginMs, endMs, joined);
        buffer.clear();
        beginMs = null;
      }
    }
    if (buffer.isNotEmpty) {
      final sentenceEnd = _milliseconds(sentence['end_time']) ?? endMs;
      _appendCue(cues, beginMs ?? 0, sentenceEnd, _joinWords(buffer));
    }
    return cues;
  }

  static int? _milliseconds(Object? value) {
    if (value is num) return value.round();
    return value is String ? num.tryParse(value)?.round() : null;
  }

  static String _joinWords(List<String> words) {
    final output = StringBuffer();
    for (final word in words) {
      if (output.isNotEmpty &&
          _isAsciiAlphaNumeric(
            output.toString().codeUnitAt(output.length - 1),
          ) &&
          _isAsciiAlphaNumeric(word.codeUnitAt(0))) {
        output.write(' ');
      }
      output.write(word);
    }
    return output.toString().trim();
  }

  static bool _isAsciiAlphaNumeric(int code) =>
      (code >= 48 && code <= 57) ||
      (code >= 65 && code <= 90) ||
      (code >= 97 && code <= 122);

  static void _appendCue(
    List<SrtCue> cues,
    int beginMs,
    int endMs,
    String text,
  ) {
    if (text.isEmpty || endMs <= beginMs) return;
    cues.add(
      SrtCue(
        start: Duration(milliseconds: beginMs),
        end: Duration(milliseconds: endMs),
        text: text,
      ),
    );
  }

  static bool _looksLikeSegment(Map<dynamic, dynamic> value) {
    return _textFrom(value) != null &&
        _durationFrom(value['start'] ?? value['begin']) != null &&
        _durationFrom(value['end'] ?? value['finish']) != null;
  }

  static SrtCue? _cueFromSegment(Object? value) {
    if (value is! Map) return null;
    final text = _textFrom(value);
    final start = _durationFrom(
      value['start'] ?? value['begin'] ?? value['begin_time'],
      milliseconds: value.containsKey('begin_time'),
    );
    final end = _durationFrom(
      value['end'] ?? value['finish'] ?? value['end_time'],
      milliseconds: value.containsKey('end_time'),
    );
    if (text == null || start == null || end == null || end <= start) {
      return null;
    }
    return SrtCue(start: start, end: end, text: text);
  }

  static String? _textFrom(Map<dynamic, dynamic> value) {
    final text = value['text'] ?? value['transcript'] ?? value['sentence'];
    if (text is! String || text.trim().isEmpty) return null;
    return text.trim();
  }

  static Duration? _durationFrom(Object? value, {bool milliseconds = false}) {
    if (value is num) {
      return Duration(
        milliseconds: milliseconds ? value.round() : (value * 1000).round(),
      );
    }
    if (value is String) {
      final parsed = num.tryParse(value);
      if (parsed == null) return null;
      return Duration(
        milliseconds: milliseconds ? parsed.round() : (parsed * 1000).round(),
      );
    }
    return null;
  }
}

String formatSrtTime(Duration value) {
  final hours = value.inHours.toString().padLeft(2, '0');
  final minutes = value.inMinutes.remainder(60).toString().padLeft(2, '0');
  final seconds = value.inSeconds.remainder(60).toString().padLeft(2, '0');
  final milliseconds = value.inMilliseconds
      .remainder(1000)
      .toString()
      .padLeft(3, '0');
  return '$hours:$minutes:$seconds,$milliseconds';
}

class AsrException implements Exception {
  const AsrException(this.message);

  final String message;

  @override
  String toString() => message;
}
