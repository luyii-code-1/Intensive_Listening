import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/ilp/lesson_exercises.dart';
import 'package:intensive_listening/mcp/app_private_api.dart';
import 'package:intensive_listening/projects/course_project.dart';

void main() {
  late Directory temporaryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-private-api-',
    );
    debugProjectsDirectory = Directory('${temporaryDirectory.path}/projects');
    debugPrivateApiDiscoveryFile = File(
      '${temporaryDirectory.path}/app-private-api.json',
    );
  });

  tearDown(() async {
    debugProjectsDirectory = null;
    debugPrivateApiDiscoveryFile = null;
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test(
    'returns structured transcript context and applies a question plan',
    () async {
      final audio = File('${temporaryDirectory.path}/lesson.mp3');
      await audio.writeAsBytes([1, 2, 3]);
      final store = const CourseProjectStore();
      final created = await store.create(audio: audio);
      await store.save(
        created.copyWith(
          audioDuration: const Duration(seconds: 8),
          step: CourseProjectStep.review,
          transcript:
              '1\n00:00:00,000 --> 00:00:01,000\nText 1\n\n'
              '2\n00:00:01,000 --> 00:00:03,000\nGood morning.\n\n'
              '3\n00:00:03,000 --> 00:00:05,000\nHow are you?\n',
        ),
      );
      var changes = 0;
      final service = AppPrivateApiService(
        onProjectChanged: () => changes += 1,
        onOpenProject: (_) {},
      );

      final context = await service.dispatch('projects.get', {
        'projectId': created.id,
      }) as Map<String, dynamic>;
      expect(context['sections'], hasLength(1));
      expect((context['cues'] as List).first['isMarker'], isTrue);
      expect((context['cues'] as List)[1]['words'], [
        {'index': 0, 'text': 'Good'},
        {'index': 1, 'text': 'morning'},
      ]);

      final result = await service.dispatch('projects.applyQuestionPlan', {
        'projectId': created.id,
        'questions': [
          {
            'title': 'What did they say?',
            'cueIndexes': [1, 2],
          },
        ],
      }) as Map<String, dynamic>;
      expect(result['questionCount'], 1);
      expect(changes, 1);
      final saved = (await store.loadAll()).single;
      expect(saved.exercises.questions.single.title, 'What did they say?');
    },
  );

  test('explicitly plans deferred Agent transcript', () async {
    final audio = File('${temporaryDirectory.path}/agent.mp3');
    await audio.writeAsBytes([1]);
    final store = const CourseProjectStore();
    final created = await store.create(audio: audio);
    await store.save(
      created.copyWith(
        audioDuration: const Duration(seconds: 8),
        step: CourseProjectStep.review,
        autoQuestionPlanDeferred: true,
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\n听下面的对话，回答第 1 题。\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nWhere are they going?\n\n'
            '3\n00:00:04,000 --> 00:00:06,000\nThey are going to school.\n',
      ),
    );
    var changes = 0;
    final service = AppPrivateApiService(
      onProjectChanged: () => changes += 1,
      onOpenProject: (_) {},
    );

    final before = await service.dispatch('projects.get', {
      'projectId': created.id,
      'fields': <String>[],
    }) as Map<String, dynamic>;
    expect(before['questionPlanPending'], isTrue);
    expect((await store.loadById(created.id))!.exercises.questions, isEmpty);

    final result = await service.dispatch('projects.autoPlanQuestions', {
      'projectId': created.id,
    }) as Map<String, dynamic>;
    expect(result['questionCount'], 1);
    expect(changes, 1);
    final saved = (await store.loadById(created.id))!;
    expect(saved.autoQuestionPlanDeferred, isFalse);
    expect(saved.automaticQuestionPlanApplied, isTrue);
    expect(saved.exercises.questions.single.number, 1);
  });

  test('rejects assigning one cue to more than one question', () async {
    final audio = File('${temporaryDirectory.path}/lesson.mp3');
    await audio.writeAsBytes([1]);
    final store = const CourseProjectStore();
    final created = await store.create(audio: audio);
    await store.save(
      created.copyWith(
        audioDuration: const Duration(seconds: 5),
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\nOne.\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nTwo.\n',
      ),
    );
    final service = AppPrivateApiService(
      onProjectChanged: () {},
      onOpenProject: (_) {},
    );

    await expectLater(
      service.dispatch('projects.applyQuestionPlan', {
        'projectId': created.id,
        'questions': [
          {
            'title': 'First',
            'cueIndexes': [0],
          },
          {
            'title': 'Second',
            'cueIndexes': [0, 1],
          },
        ],
      }),
      throwsA(
        isA<AppPrivateApiException>().having(
          (error) => error.code,
          'code',
          'cue_already_assigned',
        ),
      ),
    );
  });

  test('stores multiple questions under one listening material', () async {
    final audio = File('${temporaryDirectory.path}/lesson.mp3');
    await audio.writeAsBytes([1]);
    final store = const CourseProjectStore();
    final created = await store.create(audio: audio);
    await store.save(
      created.copyWith(
        audioDuration: const Duration(seconds: 6),
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\nWhere are they?\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nWhat will they do?\n',
      ),
    );
    final service = AppPrivateApiService(
      onProjectChanged: () {},
      onOpenProject: (_) {},
    );

    final result = await service.dispatch('projects.applyQuestionPlan', {
      'projectId': created.id,
      'materials': [
        {
          'prompt': '听下面一段对话，回答第6和第7小题',
          'cueIndexes': [0, 1],
          'questions': [
            {'number': 6, 'title': 'Where are the speakers?'},
            {'number': 7, 'title': 'What will they do next?'},
          ],
        },
      ],
    }) as Map<String, dynamic>;

    expect(result['materialCount'], 1);
    expect(result['questionCount'], 2);
    final saved = (await store.loadAll()).single;
    expect(saved.exercises.effectiveMaterials.single.questionIds, hasLength(2));
    expect(saved.exercises.questions.map((question) => question.number), [
      6,
      7,
    ]);
    expect(saved.exercises.questions[0].cueIndexes, [0, 1]);
    expect(saved.exercises.questions[1].cueIndexes, [0, 1]);

    final details = await service.dispatch('projects.get', {
      'projectId': created.id,
    }) as Map<String, dynamic>;
    expect(details['materials'], hasLength(1));
    expect((details['materials'] as List).single['questions'], hasLength(2));
  });

  test('stores the second reading within a single question', () async {
    final audio = File('${temporaryDirectory.path}/repeated.mp3');
    await audio.writeAsBytes([1]);
    final store = const CourseProjectStore();
    final created = await store.create(audio: audio);
    await store.save(
      created.copyWith(
        audioDuration: const Duration(seconds: 10),
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\nGood morning.\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nHow are you?\n\n'
            '3\n00:00:04,000 --> 00:00:06,000\nGood morning.\n\n'
            '4\n00:00:06,000 --> 00:00:08,000\nHow are you?\n',
      ),
    );
    final service = AppPrivateApiService(
      onProjectChanged: () {},
      onOpenProject: (_) {},
    );

    final result = await service.dispatch('projects.applyQuestionPlan', {
      'projectId': created.id,
      'materials': [
        {
          'cueIndexes': [0, 1, 2, 3],
          'repeatedCueIndexes': [2, 3],
          'questions': [
            {'number': 1, 'title': 'What do they say?'},
          ],
        },
      ],
    }) as Map<String, dynamic>;
    expect(result['repeatedMaterialCount'], 1);
    final saved = (await store.loadById(created.id))!;
    expect(saved.exercises.effectiveMaterials.single.repeatedCueIndexes, [
      2,
      3,
    ]);
    expect(saved.exercises.questions.single.repeatedCueIndexes, [2, 3]);
  });

  test('supports incremental question and cloze operations', () async {
    final audio = File('${temporaryDirectory.path}/lesson.mp3');
    await audio.writeAsBytes([1]);
    final store = const CourseProjectStore();
    final created = await store.create(audio: audio);
    await store.save(
      created.copyWith(
        audioDuration: const Duration(seconds: 5),
        step: CourseProjectStep.review,
        transcript:
            '1\n00:00:00,000 --> 00:00:02,000\nGood morning.\n\n'
            '2\n00:00:02,000 --> 00:00:04,000\nHow are you?\n',
      ),
    );
    var changes = 0;
    final service = AppPrivateApiService(
      onProjectChanged: () => changes += 1,
      onOpenProject: (_) {},
    );

    final added = await service.dispatch('projects.addGroup', {
      'projectId': created.id,
      'title': 'Greeting',
      'cueIndexes': [0],
      'number': 1,
    }) as Map<String, dynamic>;
    expect(added['createdGroupId'], isNotEmpty);
    expect(added['createdMaterialId'], isNotEmpty);
    var saved = (await store.loadAll()).single;
    final groupId = saved.exercises.questions.single.id;

    await service.dispatch('projects.editGroup', {
      'projectId': created.id,
      'groupId': groupId,
      'title': 'Follow-up',
      'cueIndexes': [1],
    });
    await service.dispatch('projects.setCloze', {
      'projectId': created.id,
      'cueIndex': 1,
      'wordIndexes': [0, 2],
    });
    saved = (await store.loadAll()).single;
    expect(saved.exercises.questions.single.title, 'Follow-up');
    expect(saved.exercises.questions.single.cueIndexes, [1]);
    expect(saved.exercises.clozeWordIndexes[1], {0, 2});

    await service.dispatch('projects.deleteGroup', {
      'projectId': created.id,
      'groupId': groupId,
    });
    saved = (await store.loadAll()).single;
    expect(saved.exercises.questions, isEmpty);
    expect(changes, 4);
  });

  test(
    'supports incremental reads and preserves work across transcript edits',
    () async {
      final audio = File('${temporaryDirectory.path}/lesson.mp3');
      await audio.writeAsBytes([1]);
      final store = const CourseProjectStore();
      final created = await store.create(audio: audio);
      const originalSrt =
          '1\n00:00:00,000 --> 00:00:02,000\nGood morning.\n\n'
          '2\n00:00:02,000 --> 00:00:04,000\nHow are you today?\n';
      const question = LessonQuestion(
        id: 'question-1',
        title: 'Greeting',
        number: 1,
        materialId: 'material-1',
        cueIndexes: [0, 1],
      );
      const material = LessonMaterial(
        id: 'material-1',
        prompt: '听下面的录音，回答第 1 题。',
        cueIndexes: [0, 1],
        questionIds: ['question-1'],
      );
      await store.save(
        created.copyWith(
          audioDuration: const Duration(seconds: 5),
          transcript: originalSrt,
          transcriptionJobId: 'finished-job',
          exercises: const LessonExercises(
            materials: [material],
            questions: [question],
            clozeWordIndexes: {
              1: {0, 3},
            },
          ),
        ),
      );
      var replacements = 0;
      final service = AppPrivateApiService(
        onProjectChanged: () {},
        onOpenProject: (_) {},
        onTranscriptReplacing: (_) => replacements += 1,
      );

      final summary = await service.dispatch('projects.get', {
        'projectId': created.id,
        'fields': <String>[],
      }) as Map<String, dynamic>;
      expect(summary.containsKey('cues'), isFalse);
      final page = await service.dispatch('projects.readSrt', {
        'projectId': created.id,
        'offset': 1,
        'limit': 1,
        'includeSrt': false,
      }) as Map<String, dynamic>;
      expect(page.containsKey('srt'), isFalse);
      expect((page['cues'] as List).single['index'], 1);
      expect((page['cuePage'] as Map<String, dynamic>)['hasMore'], isFalse);
      final defaultPage = await service.dispatch('projects.readSrt', {
        'projectId': created.id,
        'limit': 1,
      }) as Map<String, dynamic>;
      expect(defaultPage.containsKey('srt'), isFalse);

      await expectLater(
        service.dispatch('projects.setCueText', {
          'projectId': created.id,
          'cueIndex': 1,
          'text': 'Hello.\n00:00:04,000 --> 00:00:05,000',
        }),
        throwsA(isA<AppPrivateApiException>()),
      );

      final corrected = await service.dispatch('projects.setCueText', {
        'projectId': created.id,
        'cueIndex': 1,
        'text': 'How are you?',
      }) as Map<String, dynamic>;
      expect(corrected['removedClozeIndexes'], [3]);
      var saved = (await store.loadAll()).single;
      expect(saved.transcriptionJobId, isNull);
      expect(saved.exercises.questions.single.id, 'question-1');
      expect(saved.exercises.clozeWordIndexes[1], {0});

      final mergedSrt = File('${temporaryDirectory.path}/merged.srt');
      await mergedSrt.writeAsString(
        '1\n00:00:00,000 --> 00:00:02,000\nGood evening.\n\n'
        '2\n00:00:02,000 --> 00:00:04,000\nHow are you?\n',
      );
      final merged = await service.dispatch('projects.importSrt', {
        'projectId': created.id,
        'srtPath': mergedSrt.path,
      }) as Map<String, dynamic>;
      expect(merged['mode'], 'merge');
      expect((merged['preserved'] as Map<String, dynamic>)['questions'], 1);
      saved = (await store.loadAll()).single;
      expect(saved.exercises.questions, hasLength(1));
      expect(replacements, 2);

      final tokenized = await service.dispatch('text.tokenize', {
        'text': "Don't re-enter, please.",
      }) as Map<String, dynamic>;
      expect(tokenized['words'], [
        {'index': 0, 'text': "Don't"},
        {'index': 1, 'text': 're-enter'},
        {'index': 2, 'text': 'please'},
      ]);
    },
  );

  test('imports DOCX and returns extracted exam text', () async {
    final store = const CourseProjectStore();
    final project = await store.create();
    final source = File('${temporaryDirectory.path}/exam.docx');
    final docx = Archive()
      ..addFile(
        ArchiveFile.string(
          'word/document.xml',
          '''<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Choose the best answer.</w:t></w:r></w:p></w:body></w:document>''',
        ),
      );
    await source.writeAsBytes(ZipEncoder().encodeBytes(docx), flush: true);
    var changes = 0;
    final service = AppPrivateApiService(
      onProjectChanged: () => changes += 1,
      onOpenProject: (_) {},
    );

    final imported = await service.dispatch('projects.importExamDocument', {
      'projectId': project.id,
      'documentPath': source.path,
    }) as Map<String, dynamic>;
    expect(imported['text'], 'Choose the best answer.');
    expect(imported['sourceName'], 'exam.docx');

    final read = await service.dispatch('projects.readExamText', {
      'projectId': project.id,
    }) as Map<String, dynamic>;
    expect(read['text'], 'Choose the best answer.');
    final readPage = await service.dispatch('projects.readExamText', {
      'projectId': project.id,
      'offset': 7,
      'limit': 4,
    }) as Map<String, dynamic>;
    expect(readPage['text'], 'the ');
    expect((readPage['textPage'] as Map<String, dynamic>)['hasMore'], isTrue);
    expect(changes, 1);

    final details = await service.dispatch('projects.get', {
      'projectId': project.id,
    }) as Map<String, dynamic>;
    expect(details['examDocument'], isNotNull);
  });

  test(
    'requires Agent event and keeps the server running after disconnect',
    () async {
      final agentStates = <bool>[];
      final server = AppPrivateApiServer(
        dispatch: (method, parameters) async => {
          'method': method,
          'value': parameters['value'],
        },
        onAgentStateChanged: agentStates.add,
      );
      addTearDown(server.stop);
      await server.start();
      final discovery = jsonDecode(
        await debugPrivateApiDiscoveryFile!.readAsString(),
      ) as Map<String, dynamic>;
      final helpFile = File(discovery['helpPath'] as String);
      expect(await helpFile.exists(), isTrue);
      expect(await helpFile.readAsString(), contains('推荐流程'));
      final client = HttpClient()..findProxy = (_) => 'DIRECT';
      addTearDown(() => client.close(force: true));
      Future<Map<String, dynamic>> rpc(String method, {String? event}) async {
        final request = await client.post(
          discovery['host'] as String,
          discovery['port'] as int,
          '/v1/rpc',
        );
        request.headers.set(
          HttpHeaders.authorizationHeader,
          'Bearer ${discovery['token']}',
        );
        request.headers.contentType = ContentType.json;
        request.write(
          jsonEncode({
            'version': 1,
            'id': 'test-request',
            'event': ?event,
            'method': method,
            'params': {'value': 42},
          }),
        );
        final response = await request.close();
        return jsonDecode(await utf8.decoder.bind(response).join())
            as Map<String, dynamic>;
      }

      final denied = await rpc('echo');
      expect(
        (denied['error'] as Map<String, dynamic>)['code'],
        'agent_required',
      );
      final connected = await rpc('agent.connect', event: 'Agent');
      expect(connected['ok'], isTrue);
      final connectionResult = connected['result'] as Map<String, dynamic>;
      expect(connectionResult['helpPath'], helpFile.path);
      expect(connectionResult['instructions'], contains('HELP.md'));
      expect((await rpc('echo'))['ok'], isTrue);
      expect((await rpc('agent.disconnect'))['ok'], isTrue);
      expect(server.isRunning, isTrue);
      expect((await rpc('echo'))['ok'], isFalse);
      expect(agentStates, [true, false]);
    },
  );

  test('serves MCP tools and convenient HTTP Tool Call', () async {
    final server = AppPrivateApiServer(
      dispatch: (method, _) async => {'method': method},
    );
    addTearDown(server.stop);
    await server.start();
    final discovery = jsonDecode(
      await debugPrivateApiDiscoveryFile!.readAsString(),
    ) as Map<String, dynamic>;
    final client = HttpClient()..findProxy = (_) => 'DIRECT';
    addTearDown(() => client.close(force: true));

    Future<Map<String, dynamic>> post(
      String path,
      Map<String, Object?> body,
    ) async {
      final request = await client.post(
        discovery['host'] as String,
        discovery['port'] as int,
        path,
      );
      request.headers.set(
        HttpHeaders.authorizationHeader,
        'Bearer ${discovery['token']}',
      );
      request.headers.contentType = ContentType.json;
      request.write(jsonEncode(body));
      return jsonDecode(await utf8.decoder.bind(await request.close()).join())
          as Map<String, dynamic>;
    }

    final denied = await post('/v1/tools/call', {
      'name': 'list_course_projects',
      'arguments': <String, Object?>{},
    });
    expect((denied['error'] as Map<String, dynamic>)['code'], 'agent_required');

    final initialized = await post('/mcp?event=Agent', {
      'jsonrpc': '2.0',
      'id': 1,
      'method': 'initialize',
      'params': {'protocolVersion': '2025-11-25'},
    });
    expect(
      (initialized['result'] as Map<String, dynamic>)['protocolVersion'],
      '2025-11-25',
    );
    expect(
      (initialized['result'] as Map<String, dynamic>)['instructions'],
      allOf(contains('HELP.md'), contains(discovery['helpPath'] as String)),
    );
    final tools = await post('/mcp', {
      'jsonrpc': '2.0',
      'id': 2,
      'method': 'tools/list',
      'params': <String, Object?>{},
    });
    expect((tools['result'] as Map<String, dynamic>)['tools'], isNotEmpty);
    final called = await post('/v1/tools/call', {
      'name': 'list_course_projects',
      'arguments': <String, Object?>{},
    });
    expect(
      (called['result'] as Map<String, dynamic>)['method'],
      'projects.list',
    );
  });
}
