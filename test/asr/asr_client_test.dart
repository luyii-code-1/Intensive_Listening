import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intensive_listening/asr/asr_client.dart';

void main() {
  test('builds the DashScope native transcription endpoint', () {
    expect(
      transcriptionEndpoint(
        'https://dashscope.aliyuncs.com',
        '/api/v1/services/aigc/multimodal-generation/generation',
      ).toString(),
      'https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation',
    );
  });

  test('converts DashScope word timing and punctuation to SRT', () {
    final transcription = AsrTranscription.fromDashScopeResponseBody('''
{
  "output": {
    "output": {
      "sentence": [{
        "begin_time": 0,
        "end_time": 2200,
        "words": [
          {"text": "Good", "begin_time": 0, "end_time": 500, "punctuation": ""},
          {"text": "morning", "begin_time": 500, "end_time": 1100, "punctuation": "."},
          {"text": "Listen", "begin_time": 1200, "end_time": 1700, "punctuation": ""},
          {"text": "carefully", "begin_time": 1700, "end_time": 2200, "punctuation": "!"}
        ]
      }]
    }
  }
}
''');

    expect(transcription.toSrt(), '''1
00:00:00,000 --> 00:00:01,100
Good morning.

2
00:00:01,200 --> 00:00:02,200
Listen carefully!''');
  });

  test('accepts a successful DashScope response with no recognized speech', () {
    final emptyList = AsrTranscription.fromDashScopeResponseBody(
      '{"output":{"output":{"sentence":[]}}}',
    );
    final emptySentence = AsrTranscription.fromDashScopeResponseBody(
      '{"output":{"output":{"sentence":[{"text":"","begin_time":0,"end_time":500}]}}}',
    );

    expect(emptyList.toSrt(), isEmpty);
    expect(emptySentence.toSrt(), isEmpty);
  });

  test('sends WAV data URI and English mode to DashScope', () async {
    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'ilp-asr-client-',
    );
    addTearDown(() => temporaryDirectory.delete(recursive: true));
    final audio = File('${temporaryDirectory.path}/chunk.wav');
    await audio.writeAsBytes([0x52, 0x49, 0x46, 0x46]);

    late Uri capturedUrl;
    late Map<String, String> capturedHeaders;
    late Map<String, dynamic> capturedBody;
    final client = MockClient.streaming((request, bodyStream) async {
      capturedUrl = request.url;
      capturedHeaders = request.headers;
      capturedBody = jsonDecode(
        utf8.decode(await bodyStream.toBytes()),
      ) as Map<String, dynamic>;
      return http.StreamedResponse(
        Stream.value(
          utf8.encode(
            '{"output":{"output":{"sentence":{"text":"Hello.","begin_time":0,"end_time":500}}}}',
          ),
        ),
        200,
      );
    });

    final srt = await AsrClient(httpClient: client).transcribeToSrt(
      config: const AsrConfig(
        baseUrl: 'https://dashscope.aliyuncs.com',
        endpoint: '/api/v1/services/aigc/multimodal-generation/generation',
        model: 'qwen-audio-3.0-asr-flash',
        apiKey: 'test-key',
      ),
      audioFile: audio,
    );

    final input = capturedBody['input'] as Map<String, dynamic>;
    final messages = input['messages'] as List<dynamic>;
    final content =
        (messages.single as Map<String, dynamic>)['content'] as List<dynamic>;
    final parameters = capturedBody['parameters'] as Map<String, dynamic>;
    expect(capturedUrl.path, AsrConfig.defaultEndpoint);
    expect(capturedHeaders['authorization'], 'Bearer test-key');
    expect(
      (content.single as Map<String, dynamic>)['audio'],
      'data:audio/wav;base64,UklGRg==',
    );
    expect(parameters, {'format': 'wav', 'language': 'en'});
    expect(srt, contains('Hello.'));
  });

  test('treats the no-words API response as an empty slice', () async {
    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'ilp-asr-no-words-',
    );
    addTearDown(() => temporaryDirectory.delete(recursive: true));
    final audio = File('${temporaryDirectory.path}/chunk.wav');
    await audio.writeAsBytes([0x52, 0x49, 0x46, 0x46]);
    const config = AsrConfig(
      baseUrl: 'https://dashscope.aliyuncs.com',
      endpoint: AsrConfig.defaultEndpoint,
      model: AsrConfig.defaultModel,
      apiKey: 'test-key',
    );

    for (final body in [
      '{"code":"ASR_RESPONSE_HAVE_NO_WORDS","message":"No words found"}',
      '{"message":"ASR_RESPONSE_HAVE_NO_WORDS"}',
    ]) {
      final client = MockClient((_) async => http.Response(body, 400));
      final srt = await AsrClient(httpClient: client)
          .transcribeToSrt(config: config, audioFile: audio);
      expect(srt, isEmpty);
      client.close();
    }

    final otherError = MockClient(
      (_) async => http.Response('{"code":"InvalidApiKey"}', 400),
    );
    await expectLater(
      AsrClient(httpClient: otherError)
          .transcribeToSrt(config: config, audioFile: audio),
      throwsA(isA<AsrException>()),
    );
    otherError.close();
  });

  test('converts timestamped ASR response to SRT', () {
    final transcription = AsrTranscription.fromResponseBody('''
{
  "text": "Hello world. Keep listening.",
  "segments": [
    {"start": 0.0, "end": 1.25, "text": "Hello world."},
    {"start": 1.25, "end": 3.5, "text": "Keep listening."}
  ]
}
''');

    expect(transcription.toSrt(), '''1
00:00:00,000 --> 00:00:01,250
Hello world.

2
00:00:01,250 --> 00:00:03,500
Keep listening.''');
  });

  test('rejects ASR responses without sentence timing', () {
    expect(
      () => AsrTranscription.fromResponseBody('{"text":"Hello world."}'),
      throwsFormatException,
    );
  });
}
