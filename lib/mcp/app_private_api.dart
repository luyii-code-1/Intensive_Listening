import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;

import '../app_directories.dart';
import '../audio/audio_duration.dart';
import '../ilp/ilp_models.dart';
import '../ilp/lesson_exercises.dart';
import '../ilp/srt_parser.dart';
import '../ilp/srt_question_planner.dart';
import '../ilp/srt_sections.dart';
import '../ilp/standalone_lesson_exporter.dart';
import '../projects/course_project.dart';
import '../projects/project_delivery.dart';
import 'agent_help.dart';
import 'mcp_tools.dart';

typedef AppPrivateAsrStarter = Future<CourseProject> Function(
  CourseProject project,
);
typedef AppPrivateTranscriptReplacer = void Function(CourseProject project);

enum AgentApprovalDecision { approve, refuse, disableMcp }

enum _AgentApprovalState { user, pending, agent, refused }

const appPrivateApiVersion = 1;
const appMcpPort = 17683;

@visibleForTesting
File? debugPrivateApiDiscoveryFile;

class AppPrivateApiException implements Exception {
  const AppPrivateApiException(this.code, this.message);

  final String code;
  final String message;

  @override
  String toString() => '$code: $message';
}

typedef AppPrivateApiDispatcher = Future<Object?> Function(
  String method,
  Map<String, dynamic> parameters,
);

class AppPrivateApiServer {
  AppPrivateApiServer({
    required this.dispatch,
    this.onAgentStateChanged,
    this.onAgentAccessDenied,
    this.onAgentApprovalRequested,
  });

  final AppPrivateApiDispatcher dispatch;
  final ValueChanged<bool>? onAgentStateChanged;
  final VoidCallback? onAgentAccessDenied;
  final Future<AgentApprovalDecision> Function(String agentName)?
  onAgentApprovalRequested;
  HttpServer? _server;
  File? _discoveryFile;
  File? _helpFile;
  File? _bootstrapFile;
  String? _token;
  bool _agentActive = false;
  _AgentApprovalState _approvalState = _AgentApprovalState.user;
  int _approvalGeneration = 0;

  bool get isRunning => _server != null;
  bool get agentActive => _agentActive;

  void disconnectAgent() {
    _approvalGeneration++;
    _approvalState = _AgentApprovalState.user;
    _setAgentActive(false);
  }

  void _beginApproval(String? rawAgentName) {
    if (_approvalState == _AgentApprovalState.agent ||
        _approvalState == _AgentApprovalState.pending) {
      return;
    }
    final name = (rawAgentName?.trim().isNotEmpty ?? false)
        ? rawAgentName!.trim().substring(
            0,
            rawAgentName.trim().length.clamp(0, 80),
          )
        : '未命名智能体';
    final request = onAgentApprovalRequested;
    if (request == null) {
      _approvalState = _AgentApprovalState.agent;
      _setAgentActive(true);
      return;
    }
    _approvalState = _AgentApprovalState.pending;
    final generation = ++_approvalGeneration;
    unawaited(
      request(name)
          .then((decision) {
            if (generation != _approvalGeneration || _server == null) return;
            if (decision == AgentApprovalDecision.approve) {
              _approvalState = _AgentApprovalState.agent;
              _setAgentActive(true);
            } else {
              _approvalState = _AgentApprovalState.refused;
              _setAgentActive(false);
            }
          })
          .catchError((Object _) {
            if (generation == _approvalGeneration) {
              _approvalState = _AgentApprovalState.refused;
            }
          }),
    );
  }

  void _setAgentActive(bool active) {
    if (_agentActive == active) return;
    _agentActive = active;
    onAgentStateChanged?.call(active);
  }

  Future<void> start() async {
    if (_server != null) return;
    final discoveryFile = await _resolveDiscoveryFile();
    await discoveryFile.parent.create(recursive: true);
    final helpFile = File(p.join(discoveryFile.parent.path, agentHelpFileName));
    await helpFile.writeAsString(agentHelpMarkdown, flush: true);
    final token = await _loadOrCreateToken(discoveryFile.parent);
    final server = await HttpServer.bind(
      InternetAddress.loopbackIPv4,
      debugPrivateApiDiscoveryFile == null ? appMcpPort : 0,
    );
    final mcpUrl =
        'http://127.0.0.1:${server.port}/mcp?event=Agent&token=$token';
    final bootstrapFile = File(
      p.join(discoveryFile.parent.path, agentBootstrapFileName),
    );
    await bootstrapFile.writeAsString(
      agentBootstrapMarkdown(mcpUrl),
      flush: true,
    );
    final temporaryFile = File('${discoveryFile.path}.tmp');
    await temporaryFile.writeAsString(
      jsonEncode({
        'version': appPrivateApiVersion,
        'pid': pid,
        'host': InternetAddress.loopbackIPv4.address,
        'port': server.port,
        'token': token,
        'helpPath': helpFile.path,
        'bootstrapPath': bootstrapFile.path,
        'mcpUrl': mcpUrl,
        'toolCallUrl': 'http://127.0.0.1:${server.port}/v1/tools/call',
      }),
      flush: true,
    );
    if (await discoveryFile.exists()) await discoveryFile.delete();
    await temporaryFile.rename(discoveryFile.path);
    _server = server;
    _token = token;
    _discoveryFile = discoveryFile;
    _helpFile = helpFile;
    _bootstrapFile = bootstrapFile;
    unawaited(_serve(server));
  }

  Future<void> stop() async {
    disconnectAgent();
    final server = _server;
    _server = null;
    if (server != null) await server.close(force: true);
    final discoveryFile = _discoveryFile;
    final bootstrapFile = _bootstrapFile;
    final token = _token;
    _discoveryFile = null;
    _helpFile = null;
    _bootstrapFile = null;
    _token = null;
    if (bootstrapFile != null && await bootstrapFile.exists()) {
      await bootstrapFile.delete();
    }
    if (discoveryFile == null || !await discoveryFile.exists()) return;
    try {
      final decoded = jsonDecode(await discoveryFile.readAsString());
      if (decoded is Map<String, dynamic> && decoded['token'] == token) {
        await discoveryFile.delete();
      }
    } catch (_) {}
  }

  Future<void> _serve(HttpServer server) async {
    await for (final request in server) {
      unawaited(_handle(request));
    }
  }

  Future<void> _handle(HttpRequest request) async {
    request.response.headers.contentType = ContentType.json;
    final origin = request.headers.value('origin');
    if (origin != null &&
        origin != 'http://127.0.0.1' &&
        origin != 'http://localhost' &&
        origin != 'http://127.0.0.1:${request.connectionInfo?.localPort}' &&
        origin != 'http://localhost:${request.connectionInfo?.localPort}') {
      request.response.statusCode = HttpStatus.forbidden;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'invalid_origin', 'message': '请求来源不受信任'},
        }),
      );
      await request.response.close();
      return;
    }
    if (request.headers.value(HttpHeaders.authorizationHeader) !=
            'Bearer $_token' &&
        request.uri.queryParameters['token'] != _token) {
      request.response.statusCode = HttpStatus.unauthorized;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'unauthorized', 'message': '访问令牌无效'},
        }),
      );
      await request.response.close();
      return;
    }
    if (request.method == 'GET' && request.uri.path == '/v1/health') {
      request.response.write(
        jsonEncode({'ok': true, 'version': appPrivateApiVersion, 'pid': pid}),
      );
      await request.response.close();
      return;
    }
    if (request.uri.path == '/mcp' && request.method == 'GET') {
      request.response.statusCode = HttpStatus.methodNotAllowed;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'method_not_allowed', 'message': 'MCP 端点仅接受 POST'},
        }),
      );
      await request.response.close();
      return;
    }
    if (request.method == 'GET' && request.uri.path == '/v1/tools') {
      request.response.write(
        jsonEncode({
          'tools': [for (final tool in mcpTools) tool.toJson()],
        }),
      );
      await request.response.close();
      return;
    }
    if (request.method != 'POST' ||
        !const {
          '/mcp',
          '/v1/tools/call',
          '/v1/rpc',
          '/v1/agent/connect',
          '/v1/agent/disconnect',
        }.contains(request.uri.path)) {
      request.response.statusCode = HttpStatus.notFound;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'not_found', 'message': '接口不存在'},
        }),
      );
      await request.response.close();
      return;
    }

    if (request.contentLength > 8 * 1024 * 1024) {
      request.response.statusCode = HttpStatus.requestEntityTooLarge;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'request_too_large', 'message': '请求超过 8 MB'},
        }),
      );
      await request.response.close();
      return;
    }
    if (request.uri.path == '/mcp') {
      await _handleMcp(request);
      return;
    }
    if (request.uri.path == '/v1/tools/call') {
      await _handleToolCall(request);
      return;
    }
    if (request.uri.path.startsWith('/v1/agent/')) {
      await _handleAgentEvent(request);
      return;
    }
    await _handleLegacyRpc(request);
  }

  Future<Map<String, dynamic>> _readObject(HttpRequest request) async {
    final body = await utf8.decoder.bind(request).join();
    if (body.length > 8 * 1024 * 1024) {
      throw const AppPrivateApiException('request_too_large', '请求超过 8 MB');
    }
    Object? decoded;
    try {
      decoded = jsonDecode(body);
    } on FormatException {
      throw const AppPrivateApiException('invalid_json', '请求体不是有效 JSON');
    }
    if (decoded is! Map<String, dynamic>) {
      throw const AppPrivateApiException('invalid_request', '请求必须是 JSON 对象');
    }
    return decoded;
  }

  void _requireAgent() {
    if (_agentActive) return;
    if (_approvalState == _AgentApprovalState.pending) {
      throw const AppPrivateApiException(
        'pending_approval',
        'Pending Approval',
      );
    }
    if (_approvalState == _AgentApprovalState.refused) {
      throw const AppPrivateApiException('user_refused', 'User Refused');
    }
    onAgentAccessDenied?.call();
    throw const AppPrivateApiException(
      'agent_required',
      '当前 event=User，请先以 event=Agent 建立智能体会话。',
    );
  }

  Future<Object?> _call(String method, Map<String, dynamic> parameters) async {
    if (method == 'agent.connect') {
      _beginApproval(
        parameters['agentName'] is String
            ? parameters['agentName'] as String
            : null,
      );
      return _agentConnectionResult();
    }
    if (method == 'agent.disconnect') {
      disconnectAgent();
      return {'event': 'User', 'connected': false};
    }
    if (method != 'app.status') _requireAgent();
    final result = await dispatch(method, parameters);
    if (method == 'app.status' && result is Map) {
      return {...result, ..._agentConnectionResult()};
    }
    return result;
  }

  Map<String, Object> _agentConnectionResult() {
    final helpPath = _helpFile?.path ?? agentHelpFileName;
    return {
      'event': switch (_approvalState) {
        _AgentApprovalState.agent => 'Agent',
        _AgentApprovalState.pending => 'PendingApproval',
        _AgentApprovalState.refused => 'UserRefused',
        _ => 'User',
      },
      'connected': _agentActive,
      'helpPath': helpPath,
      'instructions': _agentActive
          ? agentConnectionInstructions(helpPath)
          : '等待应用内接管审批；批准后重新调用 intensive_listening_status。',
    };
  }

  Future<void> _handleAgentEvent(HttpRequest request) async {
    try {
      final body = await _readObject(request);
      if (request.uri.path.endsWith('/connect')) {
        if (body['event'] != 'Agent') {
          throw const AppPrivateApiException(
            'invalid_event',
            '启动智能体须设置 event=Agent',
          );
        }
        _beginApproval(
          body['agentName'] is String ? body['agentName'] as String : null,
        );
      } else {
        disconnectAgent();
      }
      request.response.write(
        jsonEncode(
          request.uri.path.endsWith('/connect')
              ? {'ok': true, ..._agentConnectionResult()}
              : {'ok': true, 'event': 'User', 'connected': false},
        ),
      );
    } on AppPrivateApiException catch (error) {
      request.response.statusCode = HttpStatus.badRequest;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': error.code, 'message': error.message},
        }),
      );
    }
    await request.response.close();
  }

  Future<void> _handleToolCall(HttpRequest request) async {
    try {
      final body = await _readObject(request);
      final name = body['name'];
      final tool = name is String ? findMcpTool(name) : null;
      if (tool == null) {
        throw const AppPrivateApiException('tool_not_found', '未知工具');
      }
      final arguments = body['arguments'];
      if (arguments != null && arguments is! Map<String, dynamic>) {
        throw const AppPrivateApiException(
          'invalid_arguments',
          'arguments 必须是对象',
        );
      }
      final result = await _call(
        tool.method,
        arguments as Map<String, dynamic>? ?? const {},
      );
      request.response.write(jsonEncode({'ok': true, 'result': result}));
    } on AppPrivateApiException catch (error) {
      request.response.statusCode =
          const {
            'agent_required',
            'pending_approval',
            'user_refused',
          }.contains(error.code)
          ? HttpStatus.forbidden
          : HttpStatus.badRequest;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': error.code, 'message': error.message},
        }),
      );
    } catch (error) {
      request.response.statusCode = HttpStatus.internalServerError;
      request.response.write(
        jsonEncode({
          'ok': false,
          'error': {'code': 'internal_error', 'message': '$error'},
        }),
      );
    }
    await request.response.close();
  }

  Future<void> _handleMcp(HttpRequest request) async {
    Object? id;
    try {
      final body = await _readObject(request);
      id = body['id'];
      if (body['jsonrpc'] != '2.0' || body['method'] is! String) {
        throw const AppPrivateApiException(
          'invalid_request',
          'MCP 请求必须是 JSON-RPC 2.0',
        );
      }
      final method = body['method'] as String;
      final params = body['params'];
      if (params != null && params is! Map<String, dynamic>) {
        throw const AppPrivateApiException('invalid_request', 'params 必须是对象');
      }
      final arguments = params as Map<String, dynamic>? ?? const {};
      if (id == null) {
        if (method == 'notifications/initialized') {
          request.response.statusCode = HttpStatus.accepted;
          await request.response.close();
          return;
        }
        request.response.statusCode = HttpStatus.accepted;
        await request.response.close();
        return;
      }
      Object? result;
      switch (method) {
        case 'initialize':
          final event =
              arguments['event'] ?? request.uri.queryParameters['event'];
          if (event != 'Agent') {
            throw const AppPrivateApiException(
              'invalid_event',
              'MCP 启动须设置 event=Agent',
            );
          }
          final clientInfo = arguments['clientInfo'];
          final identity =
              arguments['agentName'] ??
              (clientInfo is Map ? clientInfo['name'] : null) ??
              request.uri.queryParameters['agentName'];
          _beginApproval(identity is String ? identity : null);
          result = {
            'protocolVersion': '2025-11-25',
            'capabilities': {
              'tools': {'listChanged': false},
            },
            'serverInfo': {
              'name': 'Intensive Listening',
              'version': appVersion,
            },
            'instructions': _agentConnectionResult()['instructions'],
          };
          break;
        case 'ping':
          result = {};
          break;
        case 'tools/list':
          result = {
            'tools': [for (final tool in mcpTools) tool.toJson()],
          };
          break;
        case 'tools/call':
          final name = arguments['name'];
          final tool = name is String ? findMcpTool(name) : null;
          if (tool == null) {
            throw const AppPrivateApiException('tool_not_found', '未知工具');
          }
          final toolArguments = arguments['arguments'];
          if (toolArguments != null && toolArguments is! Map<String, dynamic>) {
            throw const AppPrivateApiException(
              'invalid_arguments',
              'arguments 必须是对象',
            );
          }
          try {
            final value = await _call(
              tool.method,
              toolArguments as Map<String, dynamic>? ?? const {},
            );
            result = {
              'content': [
                {'type': 'text', 'text': jsonEncode(value)},
              ],
              'isError': false,
            };
          } on AppPrivateApiException catch (error) {
            result = {
              'content': [
                {'type': 'text', 'text': '${error.code}: ${error.message}'},
              ],
              'isError': true,
            };
          }
          break;
        default:
          throw const AppPrivateApiException('method_not_found', 'MCP 方法不存在');
      }
      request.response.write(
        jsonEncode({'jsonrpc': '2.0', 'id': id, 'result': result}),
      );
    } on AppPrivateApiException catch (error) {
      request.response.write(
        jsonEncode({
          'jsonrpc': '2.0',
          'id': id,
          'error': {
            'code': -32600,
            'message': error.message,
            'data': {'code': error.code},
          },
        }),
      );
    } catch (error) {
      request.response.write(
        jsonEncode({
          'jsonrpc': '2.0',
          'id': id,
          'error': {'code': -32603, 'message': '$error'},
        }),
      );
    }
    await request.response.close();
  }

  Future<void> _handleLegacyRpc(HttpRequest request) async {
    Object? requestId;
    try {
      final decoded = await _readObject(request);
      requestId = decoded['id'];
      if (decoded['version'] != appPrivateApiVersion) {
        throw const AppPrivateApiException(
          'unsupported_version',
          '不支持的 App 私有协议版本',
        );
      }
      final method = decoded['method'];
      final parameters = decoded['params'];
      if (method is! String ||
          (parameters != null && parameters is! Map<String, dynamic>)) {
        throw const AppPrivateApiException(
          'invalid_request',
          'method 或 params 无效',
        );
      }
      if (method == 'agent.connect' && decoded['event'] != 'Agent') {
        throw const AppPrivateApiException(
          'invalid_event',
          '启动智能体须设置 event=Agent',
        );
      }
      final callParameters = <String, dynamic>{
        ...?parameters as Map<String, dynamic>?,
        if (method == 'agent.connect') 'agentName': decoded['agentName'],
      };
      final result = await _call(method, callParameters);
      request.response.write(
        jsonEncode({
          'version': appPrivateApiVersion,
          'id': requestId,
          'ok': true,
          'result': result,
        }),
      );
    } on AppPrivateApiException catch (error) {
      request.response.write(
        jsonEncode({
          'version': appPrivateApiVersion,
          'id': requestId,
          'ok': false,
          'error': {'code': error.code, 'message': error.message},
        }),
      );
    } catch (error) {
      request.response.write(
        jsonEncode({
          'version': appPrivateApiVersion,
          'id': requestId,
          'ok': false,
          'error': {'code': 'internal_error', 'message': '$error'},
        }),
      );
    }
    await request.response.close();
  }

  Future<File> _resolveDiscoveryFile() async {
    final override = debugPrivateApiDiscoveryFile;
    if (override != null) return override;
    final appData = await intensiveListeningDataDirectory();
    return File(p.join(appData.path, 'mcp', 'app-private-api.json'));
  }

  String _createToken() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64Url.encode(bytes).replaceAll('=', '');
  }

  Future<String> _loadOrCreateToken(Directory directory) async {
    final file = File(p.join(directory.path, 'access-token'));
    if (await file.exists()) {
      final token = (await file.readAsString()).trim();
      if (RegExp(r'^[A-Za-z0-9_-]{40,}$').hasMatch(token)) return token;
    }
    final token = _createToken();
    await file.writeAsString(token, flush: true);
    return token;
  }
}

class AppPrivateApiService {
  AppPrivateApiService({
    this._store = const CourseProjectStore(),
    required this.onProjectChanged,
    required this.onOpenProject,
    this.startAsr,
    this.onTranscriptReplacing,
    this.onLibraryChanged,
    Future<Directory> Function()? resolveLibraryDirectory,
  }) : _resolveLibraryDirectory =
           resolveLibraryDirectory ?? _defaultLibraryDirectory;

  final CourseProjectStore _store;
  final VoidCallback onProjectChanged;
  final ValueChanged<String> onOpenProject;
  final AppPrivateAsrStarter? startAsr;
  final AppPrivateTranscriptReplacer? onTranscriptReplacing;
  final VoidCallback? onLibraryChanged;
  final Future<Directory> Function() _resolveLibraryDirectory;

  static Future<Directory> _defaultLibraryDirectory() async {
    final appData = await intensiveListeningDataDirectory();
    return Directory(p.join(appData.path, 'library'));
  }

  Future<Object?> dispatch(
    String method,
    Map<String, dynamic> parameters,
  ) async {
    return switch (method) {
      'agent.connect' => {'event': 'Agent', 'connected': true},
      'agent.disconnect' => {'event': 'User', 'connected': false},
      'app.status' => {'ready': true, 'privateProtocolVersion': 1},
      'projects.list' => _listProjects(),
      'projects.get' => _getProject(parameters),
      'projects.create' => _createProject(parameters),
      'projects.delete' => _deleteProject(parameters),
      'projects.open' => _openProject(_requiredString(parameters, 'projectId')),
      'projects.importMedia' => _importMedia(parameters),
      'projects.importExamDocument' => _importExamDocument(parameters),
      'projects.readExamText' => _readExamText(parameters),
      'projects.importText' => _importProjectText(parameters),
      'projects.readText' => _readProjectText(parameters),
      'projects.startAsr' => _startAsr(parameters),
      'projects.importSrt' => _importSrt(parameters),
      'projects.readSrt' => _readSrt(parameters),
      'projects.setCueText' => _setCueText(parameters),
      'projects.autoPlanQuestions' => _autoPlanQuestions(parameters),
      'projects.applyQuestionPlan' => _applyQuestionPlan(parameters),
      'projects.addGroup' => _addGroup(parameters),
      'projects.editGroup' => _editGroup(parameters),
      'projects.deleteGroup' => _deleteGroup(parameters),
      'projects.applyClozePlan' => _applyClozePlan(parameters),
      'projects.setCloze' => _setCloze(parameters),
      'text.tokenize' => _tokenizeText(parameters),
      'projects.validate' => _validateProject(
        _requiredString(parameters, 'projectId'),
      ),
      'projects.addToLibrary' => _addToLibrary(parameters),
      'projects.exportIlp' => _exportIlp(parameters),
      'projects.exportStandalone' => _exportStandalone(parameters),
      _ => throw AppPrivateApiException('method_not_found', '未知方法：$method'),
    };
  }

  Future<Map<String, dynamic>> _deleteProject(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    await _store.delete(project.id);
    onProjectChanged();
    return {'projectId': project.id, 'deleted': true};
  }

  Future<List<Map<String, dynamic>>> _listProjects() async {
    final projects = await _store.loadAll();
    return [for (final project in projects) _projectSummary(project)];
  }

  Future<Map<String, dynamic>> _getProject(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final rawFields = parameters['fields'];
    if (rawFields != null && rawFields is! List) {
      throw const AppPrivateApiException('invalid_parameters', 'fields 必须是数组');
    }
    const supportedFields = {
      'examDocument',
      'sourceDocuments',
      'cues',
      'sections',
      'materials',
      'questions',
      'cloze',
    };
    final fields = rawFields?.whereType<String>().toSet();
    if (fields != null &&
        (fields.length != rawFields.length ||
            fields.any((field) => !supportedFields.contains(field)))) {
      throw const AppPrivateApiException(
        'invalid_parameters',
        'fields 包含不支持的字段',
      );
    }
    bool includes(String field) => fields == null || fields.contains(field);
    final response = <String, dynamic>{..._projectSummary(project)};
    if (includes('examDocument')) {
      final exam = await _store.readExamDocument(project);
      response['examDocument'] = exam == null
          ? null
          : {
              'sourceName': exam.sourceName,
              'textPath': exam.textPath,
              'sha256': exam.sha256,
              'paragraphCount': exam.paragraphCount,
              'tableCount': exam.tableCount,
            };
    }
    if (includes('sourceDocuments')) {
      response['sourceDocuments'] = await _sourceDocuments(project.id);
    }
    if (!project.hasTranscript) {
      for (final field in const [
        'cues',
        'sections',
        'materials',
        'questions',
      ]) {
        if (includes(field)) response[field] = const [];
      }
      if (includes('cloze')) response['cloze'] = const {};
      return response;
    }
    try {
      final cues = _parseCues(project);
      final structure = SrtTranscriptStructure.fromCues(cues);
      if (includes('cues')) {
        final page = _cuePage(parameters, cues.length);
        response['cues'] = [
          for (var index = page.offset; index < page.end; index++)
            _cueResponse(cues, structure, index),
        ];
        response['cuePage'] = page.toJson();
      }
      if (includes('sections')) {
        response['sections'] = _sectionResponse(structure);
      }
      if (includes('questions')) {
        response['questions'] = [
          for (final question in project.exercises.questions) question.toJson(),
        ];
      }
      if (includes('materials')) {
        response['materials'] = _materialResponse(project);
      }
      if (includes('cloze')) {
        response['cloze'] = _clozeResponse(project.exercises);
      }
    } on IlpException catch (error) {
      response['transcriptError'] = error.message;
    }
    return response;
  }

  Future<Map<String, dynamic>> _createProject(
    Map<String, dynamic> parameters,
  ) async {
    final audioPath = _optionalString(parameters, 'audioPath');
    File? audio;
    if (audioPath != null) {
      audio = File(audioPath);
      if (!await audio.exists()) {
        throw const AppPrivateApiException('file_not_found', '找不到指定音频');
      }
      final extension = p
          .extension(audio.path)
          .replaceFirst('.', '')
          .toLowerCase();
      if (!IlpManifest.supportedAudioExtensions.contains(extension)) {
        throw const AppPrivateApiException('unsupported_audio', '音频格式不受支持');
      }
    }
    var project = await _store.create(audio: audio);
    final title = _optionalString(parameters, 'title');
    if (title != null) {
      project = project.copyWith(title: title);
      await _store.save(project);
    }
    onProjectChanged();
    return _projectSummary(project);
  }

  Future<Map<String, dynamic>> _openProject(String projectId) async {
    await _requireProject(projectId);
    onOpenProject(projectId);
    return {'projectId': projectId, 'opened': true};
  }

  Future<Map<String, dynamic>> _importMedia(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final mediaPath = _requiredString(parameters, 'mediaPath');
    final media = File(mediaPath);
    if (!await media.exists()) {
      throw const AppPrivateApiException('file_not_found', '找不到指定音频');
    }
    final extension = p
        .extension(media.path)
        .replaceFirst('.', '')
        .toLowerCase();
    if (!IlpManifest.supportedAudioExtensions.contains(extension)) {
      throw const AppPrivateApiException('unsupported_audio', '音频格式不受支持');
    }
    final duration = await probeAudioDuration(media.path);
    final updated = await _store.bindAudio(project, media, duration: duration);
    onProjectChanged();
    return _projectSummary(updated);
  }

  Future<Map<String, dynamic>> _importExamDocument(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final path = _requiredString(parameters, 'documentPath');
    try {
      final document = await _store.importExamDocument(project, File(path));
      onProjectChanged();
      return _examDocumentResponse(document);
    } on CourseProjectDocumentException catch (error) {
      throw AppPrivateApiException('invalid_docx', error.message);
    }
  }

  Future<Map<String, dynamic>> _readExamText(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final document = await _store.readExamDocument(project);
    if (document == null) {
      throw const AppPrivateApiException('exam_required', '项目尚未导入 DOCX 试卷');
    }
    final response = _examDocumentResponse(document);
    if (!parameters.containsKey('offset') && !parameters.containsKey('limit')) {
      return response;
    }
    final page = _textPage(parameters, document.text.length);
    response['text'] = document.text.substring(page.offset, page.end);
    response['textPage'] = page.toJson();
    return response;
  }

  String _documentRole(Map<String, dynamic> parameters) {
    final role = _requiredString(parameters, 'role');
    if (role != 'exam' && role != 'reference') {
      throw const AppPrivateApiException(
        'invalid_role',
        'role 必须是 exam 或 reference',
      );
    }
    return role;
  }

  Future<Directory> _sourceDirectory(String projectId) async => Directory(
    p.join((await _store.rootDirectory()).path, projectId, 'sources'),
  );

  Future<Map<String, dynamic>> _sourceDocuments(String projectId) async {
    final directory = await _sourceDirectory(projectId);
    final result = <String, dynamic>{};
    for (final role in const ['exam', 'reference']) {
      final metadata = File(p.join(directory.path, '$role.json'));
      if (await metadata.exists()) {
        try {
          result[role] = jsonDecode(await metadata.readAsString());
        } catch (_) {}
      }
    }
    return result;
  }

  Future<Map<String, dynamic>> _importProjectText(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final role = _documentRole(parameters);
    final textFile = File(_requiredString(parameters, 'textPath'));
    if (!await textFile.exists()) {
      throw const AppPrivateApiException('file_not_found', '找不到转换后的 TXT');
    }
    late final String content;
    try {
      content = await textFile.readAsString(encoding: utf8);
    } on FormatException {
      throw const AppPrivateApiException('invalid_text', 'TXT 必须使用 UTF-8');
    }
    if (content.trim().isEmpty) {
      throw const AppPrivateApiException('invalid_text', '转换后的文本为空');
    }
    final sourcePath = _optionalString(parameters, 'sourcePath');
    File? source;
    String? extension;
    String? sourceName;
    if (sourcePath != null) {
      source = File(sourcePath);
      if (!await source.exists()) {
        throw const AppPrivateApiException('file_not_found', '找不到原始文档');
      }
      extension = p.extension(source.path).toLowerCase();
      if (extension != '.docx' && extension != '.pdf') {
        throw const AppPrivateApiException(
          'invalid_document',
          '原始文档必须是 DOCX 或 PDF',
        );
      }
      sourceName = p.basename(source.path);
    }
    final directory = await _sourceDirectory(project.id);
    await directory.create(recursive: true);
    final output = File(p.join(directory.path, '$role.txt'));
    if (source != null) {
      await source.copy(p.join(directory.path, '$role$extension'));
    }
    await output.writeAsString(content, encoding: utf8, flush: true);
    final metadata = {
      'role': role,
      'sourceName': sourceName,
      'textPath': output.path,
      'importedAt': DateTime.now().toIso8601String(),
      'textLength': content.length,
    };
    await File(p.join(directory.path, '$role.json'))
        .writeAsString(jsonEncode(metadata), flush: true);
    await _store.save(project.copyWith());
    onProjectChanged();
    return {'projectId': project.id, ...metadata};
  }

  Future<Map<String, dynamic>> _readProjectText(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final role = _documentRole(parameters);
    final file = File(
      p.join((await _sourceDirectory(project.id)).path, '$role.txt'),
    );
    if (!await file.exists()) {
      throw const AppPrivateApiException('document_required', '项目尚无该文档文本');
    }
    final value = await file.readAsString(encoding: utf8);
    final page = _textPage(parameters, value.length);
    return {
      'projectId': project.id,
      'role': role,
      'text': value.substring(page.offset, page.end),
      'textPage': page.toJson(),
    };
  }

  Future<Map<String, dynamic>> _startAsr(
    Map<String, dynamic> parameters,
  ) async {
    final starter = startAsr;
    if (starter == null) {
      throw const AppPrivateApiException('asr_unavailable', '当前应用未开放转写接口');
    }
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    if (!project.hasAudio) {
      throw const AppPrivateApiException('audio_required', '项目尚未绑定音频');
    }
    final updated = await starter(project);
    onProjectChanged();
    return _projectSummary(updated);
  }

  Future<Map<String, dynamic>> _readSrt(Map<String, dynamic> parameters) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final cues = _parseCues(project);
    final structure = SrtTranscriptStructure.fromCues(cues);
    final page = _cuePage(parameters, cues.length);
    final includeSrt = parameters['includeSrt'] == true;
    return {
      'projectId': project.id,
      if (includeSrt) 'srt': project.transcript,
      'cues': [
        for (var index = page.offset; index < page.end; index++)
          _cueResponse(cues, structure, index),
      ],
      'cuePage': page.toJson(),
    };
  }

  Future<Map<String, dynamic>> _importSrt(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    if (!project.hasAudio) {
      throw const AppPrivateApiException('audio_required', '项目尚未绑定音频');
    }
    final srtPath = _requiredString(parameters, 'srtPath');
    final file = File(srtPath);
    if (!await file.exists()) {
      throw const AppPrivateApiException('file_not_found', '找不到指定 SRT');
    }
    final transcript = await file.readAsString();
    late final List<SrtCue> importedCues;
    try {
      importedCues = SrtParser.parse(
        utf8.encode(transcript),
        project.audioDuration ?? const Duration(days: 7),
      );
    } on IlpException catch (error) {
      throw AppPrivateApiException('invalid_srt', error.message);
    }
    final requestedMode = _optionalString(parameters, 'mode') ?? 'auto';
    if (!const {'auto', 'merge', 'replace'}.contains(requestedMode)) {
      throw const AppPrivateApiException(
        'invalid_parameters',
        'mode 必须是 auto、merge 或 replace',
      );
    }
    List<SrtCue>? existingCues;
    if (project.hasTranscript) {
      try {
        existingCues = _parseCues(project);
      } on IlpException {
        existingCues = null;
      }
    }
    final sameTimeline =
        existingCues != null && _sameTimeline(existingCues, importedCues);
    if (requestedMode == 'merge' && !sameTimeline) {
      throw const AppPrivateApiException(
        'timeline_mismatch',
        'merge 模式要求 cue 数量和时间轴与当前字幕一致',
      );
    }
    final preserveExercises =
        requestedMode == 'merge' || (requestedMode == 'auto' && sameTimeline);
    final previousQuestionCount = project.exercises.questions.length;
    final previousClozeCount = project.exercises.clozeWordIndexes.values
        .fold<int>(0, (total, indexes) => total + indexes.length);
    final exercises = preserveExercises
        ? _exercisesForTranscript(project.exercises, importedCues)
        : const LessonExercises();
    final retainedClozeCount = exercises.clozeWordIndexes.values.fold<int>(
      0,
      (total, indexes) => total + indexes.length,
    );
    onTranscriptReplacing?.call(project);
    final updated = project.copyWith(
      transcript: transcript,
      transcriptionJobId: null,
      step: CourseProjectStep.review,
      reviewPhase: ReviewPhase.grouping,
      exercises: exercises,
      automaticQuestionPlanApplied: preserveExercises,
      autoQuestionPlanDeferred: !preserveExercises,
    );
    await _store.save(updated);
    onProjectChanged();
    return {
      ..._projectSummary(updated),
      'mode': preserveExercises ? 'merge' : 'replace',
      'preserved': {
        'questions': preserveExercises ? previousQuestionCount : 0,
        'cloze': retainedClozeCount,
      },
      'cleared': {
        'questions': preserveExercises ? 0 : previousQuestionCount,
        'cloze': previousClozeCount - retainedClozeCount,
      },
    };
  }

  Future<Map<String, dynamic>> _setCueText(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final cueIndex = _requiredIndex(parameters, 'cueIndex');
    final text = _requiredString(parameters, 'text');
    if (text.contains('\n') || text.contains('\r') || text.contains('-->')) {
      throw const AppPrivateApiException(
        'invalid_cue_text',
        '字幕文本必须为单行，且不能包含 SRT 时间标记',
      );
    }
    final cues = _parseCues(project);
    if (cueIndex >= cues.length) {
      throw const AppPrivateApiException('invalid_cue_index', '字幕索引超出范围');
    }
    final updatedCues = [...cues];
    final current = updatedCues[cueIndex];
    updatedCues[cueIndex] = SrtCue(
      start: current.start,
      end: current.end,
      text: text,
    );
    final updatedExercises = _exercisesForTranscript(
      project.exercises,
      updatedCues,
    );
    final previousCloze =
        project.exercises.clozeWordIndexes[cueIndex] ?? const <int>{};
    final retainedCloze =
        updatedExercises.clozeWordIndexes[cueIndex] ?? const <int>{};
    onTranscriptReplacing?.call(project);
    final updated = project.copyWith(
      transcript: SrtParser.serialize(updatedCues),
      transcriptionJobId: null,
      exercises: updatedExercises,
    );
    await _store.save(updated);
    onProjectChanged();
    final structure = SrtTranscriptStructure.fromCues(updatedCues);
    return {
      'projectId': project.id,
      'cue': _cueResponse(updatedCues, structure, cueIndex),
      'removedClozeIndexes': previousCloze.difference(retainedCloze).toList()
        ..sort(),
    };
  }

  Map<String, dynamic> _tokenizeText(Map<String, dynamic> parameters) {
    final text = _requiredString(parameters, 'text');
    return {
      'text': text,
      'words': [
        for (final part in tokenizeLessonText(text))
          if (part.isWord) {'index': part.wordIndex, 'text': part.text},
      ],
    };
  }

  Future<Map<String, dynamic>> _autoPlanQuestions(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    if (!project.hasTranscript) {
      throw const AppPrivateApiException('transcript_required', '项目尚无字幕');
    }
    if (project.exercises.questions.isNotEmpty) {
      throw const AppPrivateApiException(
        'question_plan_exists',
        '项目已有题目；请使用 apply_question_plan 调整题目',
      );
    }
    late final LessonExercises exercises;
    try {
      exercises = _planAgentProjectQuestions(project);
    } on IlpException catch (error) {
      throw AppPrivateApiException('invalid_srt', error.message);
    }
    final latest = await _requireProject(project.id);
    if (latest.updatedAt != project.updatedAt ||
        latest.transcript != project.transcript) {
      throw const AppPrivateApiException('project_changed', '工程已变化，请重新读取');
    }
    final updated = latest.copyWith(
      exercises: exercises,
      step: CourseProjectStep.review,
      reviewPhase: ReviewPhase.grouping,
      automaticQuestionPlanApplied: true,
      autoQuestionPlanDeferred: false,
    );
    await _store.save(updated);
    onProjectChanged();
    return {
      'projectId': project.id,
      'materialCount': exercises.effectiveMaterials.length,
      'questionCount': exercises.questions.length,
    };
  }

  Future<Map<String, dynamic>> _applyQuestionPlan(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final cues = _parseCues(project);
    final markers = SrtTranscriptStructure.fromCues(cues).markerCueIndexes;
    final usedCueIndexes = <int>{};
    final materials = <LessonMaterial>[];
    final questions = <LessonQuestion>[];
    var nextNumber = 1;
    final rawMaterials = parameters['materials'];
    final legacyQuestions = parameters['questions'];
    final plan = rawMaterials is List
        ? rawMaterials
        : legacyQuestions is List
        ? [
            for (final raw in legacyQuestions)
              if (raw is Map<String, dynamic>)
                {
                  'cueIndexes': raw['cueIndexes'],
                  'questions': [raw],
                }
              else
                raw,
          ]
        : null;
    if (plan == null) {
      throw const AppPrivateApiException(
        'invalid_questions',
        'materials 必须是数组',
      );
    }
    final stamp = DateTime.now().microsecondsSinceEpoch;
    for (var materialIndex = 0; materialIndex < plan.length; materialIndex++) {
      final raw = plan[materialIndex];
      if (raw is! Map<String, dynamic>) {
        throw const AppPrivateApiException('invalid_questions', '听力材料必须是对象');
      }
      final rawCueIndexes = raw['cueIndexes'];
      if (rawCueIndexes is! List || rawCueIndexes.isEmpty) {
        throw AppPrivateApiException(
          'invalid_questions',
          '第 ${materialIndex + 1} 段听力材料没有字幕句',
        );
      }
      final cueIndexes =
          rawCueIndexes
              .whereType<num>()
              .map((value) => value.round())
              .toSet()
              .toList()
            ..sort();
      if (cueIndexes.length != rawCueIndexes.length ||
          cueIndexes.any(
            (index) =>
                index < 0 || index >= cues.length || markers.contains(index),
          )) {
        throw AppPrivateApiException(
          'invalid_questions',
          '第 ${materialIndex + 1} 段听力材料包含无效字幕索引',
        );
      }
      if (cueIndexes.any((index) => !usedCueIndexes.add(index))) {
        throw const AppPrivateApiException(
          'cue_already_assigned',
          '一个字幕句只能属于一段听力材料',
        );
      }
      final rawLeadIn = raw['leadInCueIndexes'];
      if (rawLeadIn != null && rawLeadIn is! List) {
        throw const AppPrivateApiException('invalid_questions', '提前提示索引必须是数组');
      }
      final leadInCueIndexes = rawLeadIn is List
          ? (rawLeadIn.whereType<num>().map((value) => value.round()).toList()
              ..sort())
          : <int>[];
      if (rawLeadIn is List &&
          (leadInCueIndexes.length != rawLeadIn.length ||
              leadInCueIndexes.toSet().length != leadInCueIndexes.length ||
              leadInCueIndexes.any(
                (index) =>
                    index < 0 ||
                    index >= cueIndexes.first ||
                    !usedCueIndexes.add(index),
              ))) {
        throw const AppPrivateApiException(
          'invalid_questions',
          '提前提示索引无效或已归属其他材料',
        );
      }
      final rawRepeatedCueIndexes = raw['repeatedCueIndexes'];
      if (rawRepeatedCueIndexes != null && rawRepeatedCueIndexes is! List) {
        throw const AppPrivateApiException(
          'invalid_questions',
          '重复朗读字幕索引必须是数组',
        );
      }
      final repeatedCueIndexes = rawRepeatedCueIndexes is List
          ? (rawRepeatedCueIndexes
                .whereType<num>()
                .map((value) => value.round())
                .toList()
              ..sort())
          : <int>[];
      if (rawRepeatedCueIndexes is List &&
          (repeatedCueIndexes.length != rawRepeatedCueIndexes.length ||
              repeatedCueIndexes.toSet().length != repeatedCueIndexes.length ||
              repeatedCueIndexes.any((index) => !cueIndexes.contains(index)))) {
        throw AppPrivateApiException(
          'invalid_questions',
          '第 ${materialIndex + 1} 段听力材料的重复朗读索引必须属于该材料且不能重复',
        );
      }
      final rawQuestions = raw['questions'];
      if (rawQuestions is! List || rawQuestions.isEmpty) {
        throw AppPrivateApiException(
          'invalid_questions',
          '第 ${materialIndex + 1} 段听力材料没有小题',
        );
      }
      final materialId =
          _optionalString(raw, 'id') ?? 'material-$stamp-$materialIndex';
      final questionIds = <String>[];
      for (
        var questionIndex = 0;
        questionIndex < rawQuestions.length;
        questionIndex++
      ) {
        final rawQuestion = rawQuestions[questionIndex];
        if (rawQuestion is! Map<String, dynamic>) {
          throw const AppPrivateApiException('invalid_questions', '小题必须是对象');
        }
        final number = rawQuestion['number'] is num
            ? (rawQuestion['number'] as num).round()
            : nextNumber;
        if (number < 1) {
          throw const AppPrivateApiException('invalid_questions', '题号必须大于 0');
        }
        nextNumber = number + 1;
        final questionId =
            _optionalString(rawQuestion, 'id') ??
            'question-$stamp-$materialIndex-$questionIndex';
        final options = _questionOptions(rawQuestion['options']);
        final answerIndex = _questionAnswer(
          rawQuestion['answerIndex'],
          options,
        );
        questionIds.add(questionId);
        questions.add(
          LessonQuestion(
            id: questionId,
            title: _optionalString(rawQuestion, 'title') ?? '第 $number 题',
            number: number,
            materialId: materialId,
            cueIndexes: cueIndexes,
            repeatedCueIndexes: repeatedCueIndexes,
            options: options,
            answerIndex: answerIndex,
          ),
        );
      }
      materials.add(
        LessonMaterial(
          id: materialId,
          prompt: _optionalString(raw, 'prompt') ?? '',
          cueIndexes: cueIndexes,
          repeatedCueIndexes: repeatedCueIndexes,
          leadInCueIndexes: leadInCueIndexes,
          questionIds: questionIds,
        ),
      );
    }
    questions.sort((left, right) => left.number.compareTo(right.number));
    _assertUniqueQuestionNumbers(questions);
    final updated = project.copyWith(
      exercises: LessonExercises(
        materials: materials,
        questions: questions,
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
      reviewPhase: ReviewPhase.grouping,
      step: CourseProjectStep.review,
      automaticQuestionPlanApplied: true,
      autoQuestionPlanDeferred: false,
    );
    await _store.save(updated);
    onProjectChanged();
    return {
      'projectId': project.id,
      'materialCount': materials.length,
      'questionCount': questions.length,
      'repeatedMaterialCount': materials
          .where((material) => material.repeatedCueIndexes.isNotEmpty)
          .length,
    };
  }

  Future<Map<String, dynamic>> _addGroup(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final questions = [...project.exercises.questions];
    final materials = [...project.exercises.effectiveMaterials];
    final number = parameters['number'] is num
        ? (parameters['number'] as num).round()
        : questions.length + 1;
    final requestedMaterialId = _optionalString(parameters, 'materialId');
    final existingMaterialIndex = requestedMaterialId == null
        ? -1
        : materials.indexWhere(
            (material) => material.id == requestedMaterialId,
          );
    final stamp = DateTime.now().microsecondsSinceEpoch;
    late final LessonMaterial material;
    if (existingMaterialIndex >= 0) {
      material = materials[existingMaterialIndex];
    } else {
      final cues = _parseCues(project);
      final indexes = _validatedGroupCueIndexes(
        project,
        cues,
        parameters['cueIndexes'],
      );
      material = LessonMaterial(
        id: requestedMaterialId ?? 'material-$stamp',
        prompt: _optionalString(parameters, 'prompt') ?? '',
        cueIndexes: indexes,
        leadInCueIndexes: _validatedLeadInCueIndexes(
          project,
          cues,
          parameters['leadInCueIndexes'],
          indexes,
        ),
        questionIds: const [],
      );
      materials.add(material);
    }
    final question = LessonQuestion(
      id: 'question-$stamp',
      title: _optionalString(parameters, 'title') ?? '第 $number 题',
      number: number,
      materialId: material.id,
      cueIndexes: material.cueIndexes,
      repeatedCueIndexes: material.repeatedCueIndexes,
      options: _questionOptions(parameters['options']),
      answerIndex: _questionAnswer(
        parameters['answerIndex'],
        _questionOptions(parameters['options']),
      ),
    );
    questions.add(question);
    final materialIndex = materials.indexWhere(
      (item) => item.id == material.id,
    );
    materials[materialIndex] = LessonMaterial(
      id: material.id,
      prompt: material.prompt,
      cueIndexes: material.cueIndexes,
      repeatedCueIndexes: material.repeatedCueIndexes,
      leadInCueIndexes: material.leadInCueIndexes,
      questionIds: [...material.questionIds, question.id],
    );
    questions.sort((left, right) => left.number.compareTo(right.number));
    final updated = await _saveQuestions(project, materials, questions);
    return {
      'projectId': project.id,
      'questionCount': updated.exercises.questions.length,
      'createdGroupId': question.id,
      'createdMaterialId': material.id,
    };
  }

  Future<Map<String, dynamic>> _editGroup(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final groupId = _requiredString(parameters, 'groupId');
    final questions = [...project.exercises.questions];
    final index = questions.indexWhere((question) => question.id == groupId);
    if (index < 0) {
      throw const AppPrivateApiException('group_not_found', '找不到指定题目');
    }
    final current = questions[index];
    final materials = [...project.exercises.effectiveMaterials];
    final materialIndex = materials.indexWhere(
      (material) => material.questionIds.contains(groupId),
    );
    if (materialIndex < 0) {
      throw const AppPrivateApiException('group_not_found', '找不到题目所属听力材料');
    }
    var material = materials[materialIndex];
    if (parameters.containsKey('cueIndexes')) {
      final cues = _parseCues(project);
      final cueIndexes = _validatedGroupCueIndexes(
        project,
        cues,
        parameters['cueIndexes'],
        ignoreMaterialId: material.id,
      );
      material = LessonMaterial(
        id: material.id,
        prompt: _optionalString(parameters, 'prompt') ?? material.prompt,
        cueIndexes: cueIndexes,
        repeatedCueIndexes: material.repeatedCueIndexes
            .where(cueIndexes.contains)
            .toList(growable: false),
        leadInCueIndexes: parameters.containsKey('leadInCueIndexes')
            ? _validatedLeadInCueIndexes(
                project,
                cues,
                parameters['leadInCueIndexes'],
                cueIndexes,
                ignoreMaterialId: material.id,
              )
            : material.leadInCueIndexes,
        questionIds: material.questionIds,
      );
      materials[materialIndex] = material;
    }
    if (!parameters.containsKey('cueIndexes') &&
        parameters.containsKey('leadInCueIndexes')) {
      final leadIn = _validatedLeadInCueIndexes(
        project,
        _parseCues(project),
        parameters['leadInCueIndexes'],
        material.cueIndexes,
        ignoreMaterialId: material.id,
      );
      material = LessonMaterial(
        id: material.id,
        prompt: material.prompt,
        cueIndexes: material.cueIndexes,
        repeatedCueIndexes: material.repeatedCueIndexes,
        leadInCueIndexes: leadIn,
        questionIds: material.questionIds,
      );
      materials[materialIndex] = material;
    }
    final requestedNumber = parameters['number'];
    final options = parameters.containsKey('options')
        ? _questionOptions(parameters['options'])
        : current.options;
    final answer = parameters.containsKey('answerIndex')
        ? _questionAnswer(parameters['answerIndex'], options)
        : current.answerIndex != null && current.answerIndex! < options.length
        ? current.answerIndex
        : null;
    questions[index] = LessonQuestion(
      id: current.id,
      title: _optionalString(parameters, 'title') ?? current.title,
      number: requestedNumber is num ? requestedNumber.round() : current.number,
      materialId: material.id,
      cueIndexes: material.cueIndexes,
      repeatedCueIndexes: material.repeatedCueIndexes,
      options: options,
      answerIndex: answer,
    );
    questions.sort((left, right) => left.number.compareTo(right.number));
    final updated = await _saveQuestions(project, materials, questions);
    return {
      'projectId': project.id,
      'questionCount': updated.exercises.questions.length,
      'editedGroupId': current.id,
      'materialId': material.id,
    };
  }

  Future<Map<String, dynamic>> _deleteGroup(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final groupId = _requiredString(parameters, 'groupId');
    final questions = project.exercises.questions
        .where((question) => question.id != groupId)
        .toList(growable: false);
    if (questions.length == project.exercises.questions.length) {
      throw const AppPrivateApiException('group_not_found', '找不到指定题目');
    }
    final materials = <LessonMaterial>[];
    for (final material in project.exercises.effectiveMaterials) {
      final questionIds = material.questionIds
          .where((questionId) => questionId != groupId)
          .toList(growable: false);
      if (questionIds.isEmpty) continue;
      materials.add(
        LessonMaterial(
          id: material.id,
          prompt: material.prompt,
          cueIndexes: material.cueIndexes,
          repeatedCueIndexes: material.repeatedCueIndexes,
          leadInCueIndexes: material.leadInCueIndexes,
          questionIds: questionIds,
        ),
      );
    }
    await _saveQuestions(project, materials, questions);
    return {'projectId': project.id, 'deletedGroupId': groupId};
  }

  Future<Map<String, dynamic>> _applyClozePlan(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final rawItems = parameters['items'];
    if (rawItems is! List) {
      throw const AppPrivateApiException('invalid_cloze', 'items 必须是数组');
    }
    final cues = _parseCues(project);
    final cloze = <int, Set<int>>{};
    for (final raw in rawItems) {
      if (raw is! Map<String, dynamic> || raw['cueIndex'] is! num) {
        throw const AppPrivateApiException('invalid_cloze', '挖空项目格式无效');
      }
      final cueIndex = (raw['cueIndex'] as num).round();
      final rawWordIndexes = raw['wordIndexes'];
      if (cueIndex < 0 || cueIndex >= cues.length || rawWordIndexes is! List) {
        throw const AppPrivateApiException('invalid_cloze', '字幕或单词索引无效');
      }
      final wordCount = tokenizeLessonText(cues[cueIndex].text)
          .where((part) => part.isWord)
          .length;
      final wordIndexes = rawWordIndexes
          .whereType<num>()
          .map((value) => value.round())
          .toSet();
      if (wordIndexes.length != rawWordIndexes.length ||
          wordIndexes.any((index) => index < 0 || index >= wordCount)) {
        throw const AppPrivateApiException('invalid_cloze', '单词索引超出范围');
      }
      if (wordIndexes.isNotEmpty) cloze[cueIndex] = wordIndexes;
    }
    final updated = project.copyWith(
      exercises: LessonExercises(
        materials: project.exercises.effectiveMaterials,
        questions: project.exercises.questions,
        clozeWordIndexes: cloze,
      ),
      reviewPhase: ReviewPhase.cloze,
    );
    await _store.save(updated);
    onProjectChanged();
    return {'projectId': project.id, 'clozeCueCount': cloze.length};
  }

  Future<Map<String, dynamic>> _setCloze(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final cueIndexValue = parameters['cueIndex'];
    final rawWordIndexes = parameters['wordIndexes'];
    if (cueIndexValue is! num || rawWordIndexes is! List) {
      throw const AppPrivateApiException('invalid_cloze', '字幕或单词索引无效');
    }
    final cues = _parseCues(project);
    final cueIndex = cueIndexValue.round();
    if (cueIndex < 0 || cueIndex >= cues.length) {
      throw const AppPrivateApiException('invalid_cloze', '字幕索引超出范围');
    }
    final wordCount = tokenizeLessonText(cues[cueIndex].text)
        .where((part) => part.isWord)
        .length;
    final wordIndexes = rawWordIndexes
        .whereType<num>()
        .map((value) => value.round())
        .toSet();
    if (wordIndexes.length != rawWordIndexes.length ||
        wordIndexes.any((index) => index < 0 || index >= wordCount)) {
      throw const AppPrivateApiException('invalid_cloze', '单词索引超出范围');
    }
    final cloze = {
      for (final entry in project.exercises.clozeWordIndexes.entries)
        entry.key: {...entry.value},
    };
    if (wordIndexes.isEmpty) {
      cloze.remove(cueIndex);
    } else {
      cloze[cueIndex] = wordIndexes;
    }
    final updated = project.copyWith(
      exercises: LessonExercises(
        materials: project.exercises.effectiveMaterials,
        questions: project.exercises.questions,
        clozeWordIndexes: cloze,
      ),
      reviewPhase: ReviewPhase.cloze,
    );
    await _store.save(updated);
    onProjectChanged();
    return {
      'projectId': project.id,
      'cueIndex': cueIndex,
      'wordIndexes': wordIndexes.toList()..sort(),
    };
  }

  List<int> _validatedGroupCueIndexes(
    CourseProject project,
    List<SrtCue> cues,
    Object? rawIndexes, {
    String? ignoreMaterialId,
  }) {
    if (rawIndexes is! List || rawIndexes.isEmpty) {
      throw const AppPrivateApiException('invalid_questions', '题目没有字幕句');
    }
    final indexes =
        rawIndexes
            .whereType<num>()
            .map((value) => value.round())
            .toSet()
            .toList()
          ..sort();
    final markers = SrtTranscriptStructure.fromCues(cues).markerCueIndexes;
    if (indexes.length != rawIndexes.length ||
        indexes.any(
          (index) =>
              index < 0 || index >= cues.length || markers.contains(index),
        )) {
      throw const AppPrivateApiException('invalid_questions', '题目包含无效字幕索引');
    }
    final assigned = {
      for (final material in project.exercises.effectiveMaterials)
        if (material.id != ignoreMaterialId) ...[
          ...material.cueIndexes,
          ...material.leadInCueIndexes,
        ],
    };
    if (indexes.any(assigned.contains)) {
      throw const AppPrivateApiException(
        'cue_already_assigned',
        '一个字幕句只能属于一段听力材料',
      );
    }
    return indexes;
  }

  List<int> _validatedLeadInCueIndexes(
    CourseProject project,
    List<SrtCue> cues,
    Object? rawIndexes,
    List<int> materialCueIndexes, {
    String? ignoreMaterialId,
  }) {
    if (rawIndexes == null) return const [];
    if (rawIndexes is! List) {
      throw const AppPrivateApiException('invalid_questions', '提前提示索引必须是数组');
    }
    final indexes =
        rawIndexes
            .whereType<num>()
            .map((value) => value.round())
            .toSet()
            .toList()
          ..sort();
    final assigned = {
      for (final material in project.exercises.effectiveMaterials)
        if (material.id != ignoreMaterialId) ...[
          ...material.cueIndexes,
          ...material.leadInCueIndexes,
        ],
    };
    if (indexes.length != rawIndexes.length ||
        indexes.any(
          (index) =>
              index < 0 ||
              index >= cues.length ||
              index >= materialCueIndexes.first ||
              assigned.contains(index),
        )) {
      throw const AppPrivateApiException(
        'invalid_questions',
        '提前提示索引无效或已归属其他材料',
      );
    }
    return indexes;
  }

  Future<CourseProject> _saveQuestions(
    CourseProject project,
    List<LessonMaterial> materials,
    List<LessonQuestion> questions,
  ) async {
    _assertUniqueQuestionNumbers(questions);
    final updated = project.copyWith(
      exercises: LessonExercises(
        materials: materials,
        questions: questions,
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
      reviewPhase: ReviewPhase.grouping,
      step: CourseProjectStep.review,
      automaticQuestionPlanApplied: true,
      autoQuestionPlanDeferred: false,
    );
    await _store.save(updated);
    onProjectChanged();
    return updated;
  }

  Future<Map<String, dynamic>> _validateProject(String projectId) async {
    final project = await _requireProject(projectId);
    final issues = <Map<String, String>>[];
    if (!project.hasAudio) {
      issues.add({'code': 'audio_required', 'message': '项目尚未绑定音频'});
    }
    if (!project.hasTranscript) {
      issues.add({'code': 'transcript_required', 'message': '项目尚无字幕'});
    } else {
      try {
        _parseCues(project);
      } on IlpException catch (error) {
        issues.add({'code': 'invalid_srt', 'message': error.message});
      }
    }
    final seenNumbers = <int>{};
    for (final question in project.exercises.questions) {
      if (!seenNumbers.add(question.number)) {
        issues.add({
          'code': 'duplicate_question_numbers',
          'message': '题号 ${question.number} 重复',
        });
      }
      if (question.answerIndex != null &&
          (question.answerIndex! < 0 ||
              question.answerIndex! >= question.options.length)) {
        issues.add({
          'code': 'invalid_answer',
          'message': '题号 ${question.number} 答案索引无效',
        });
      }
    }
    return {'projectId': project.id, 'valid': issues.isEmpty, 'issues': issues};
  }

  List<String> _questionOptions(Object? raw) {
    if (raw == null) return const [];
    if (raw is! List || raw.any((item) => item is! String)) {
      throw const AppPrivateApiException('invalid_options', '选项必须是字符串数组');
    }
    return raw
        .cast<String>()
        .map((item) => item.trim())
        .toList(growable: false);
  }

  int? _questionAnswer(Object? raw, List<String> options) {
    if (raw == null) return null;
    if (raw is! int || raw < 0 || raw >= options.length) {
      throw const AppPrivateApiException('invalid_answer', '答案索引必须对应已有选项');
    }
    return raw;
  }

  void _assertUniqueQuestionNumbers(List<LessonQuestion> questions) {
    final numbers = <int>{};
    for (final question in questions) {
      if (!numbers.add(question.number)) {
        throw AppPrivateApiException(
          'duplicate_question_numbers',
          '题号 ${question.number} 重复',
        );
      }
    }
  }

  Future<Map<String, dynamic>> _addToLibrary(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    try {
      final result = await const ProjectDelivery().addToLibrary(
        project,
        await _resolveLibraryDirectory(),
      );
      final updated = project.copyWith(
        packageVersion: result.packageVersion,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      onProjectChanged();
      onLibraryChanged?.call();
      return {
        'projectId': project.id,
        'lessonId': result.lesson.id,
        'packageUuid': project.packageUuid,
        'packageVersion': result.packageVersion,
        'addedToPlayback': true,
      };
    } on IlpException catch (error) {
      throw AppPrivateApiException('publish_failed', error.message);
    }
  }

  Future<Map<String, dynamic>> _exportIlp(
    Map<String, dynamic> parameters,
  ) async {
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    if (!project.hasAudio || !project.hasTranscript) {
      throw const AppPrivateApiException(
        'project_incomplete',
        '导出前必须绑定音频并准备字幕',
      );
    }
    final outputPath = await _resolveExportPath(
      _requiredString(parameters, 'outputPath'),
      '.ilp',
    );
    try {
      final packageVersion = project.packageVersion + 1;
      final output = await const ProjectDelivery().createIlp(
        project,
        File(outputPath),
        packageVersion: packageVersion,
      );
      final updated = project.copyWith(
        packageVersion: packageVersion,
        lastExportPath: output.path,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      onProjectChanged();
      return {
        'projectId': project.id,
        'outputPath': output.path,
        'packageUuid': project.packageUuid,
        'packageVersion': packageVersion,
      };
    } on IlpException catch (error) {
      throw AppPrivateApiException('export_failed', error.message);
    }
  }

  Future<String> _resolveExportPath(String requested, String extension) async {
    final exports = Directory(
      p.join((await intensiveListeningDataDirectory()).path, 'exports'),
    );
    await exports.create(recursive: true);
    final raw = requested.trim();
    if (p.split(raw).contains('..') || raw.isEmpty) {
      throw const AppPrivateApiException('invalid_export_path', '导出路径无效');
    }
    final withExtension = raw.toLowerCase().endsWith(extension)
        ? raw
        : '$raw$extension';
    final target = p.normalize(
      p.isAbsolute(withExtension)
          ? withExtension
          : p.join(exports.path, withExtension),
    );
    final profile =
        Platform.environment['USERPROFILE'] ?? Platform.environment['HOME'];
    final allowed = <Directory>[
      exports,
      if (profile != null) ...[
        Directory(p.join(profile, 'Desktop')),
        Directory(p.join(profile, 'Documents')),
      ],
    ];
    final parent = Directory(p.dirname(target));
    if (!await parent.exists()) {
      throw const AppPrivateApiException('invalid_export_path', '请先创建导出目录');
    }
    final canonicalParent = await parent.resolveSymbolicLinks();
    var accepted = false;
    for (final root in allowed) {
      if (!await root.exists()) continue;
      final canonicalRoot = await root.resolveSymbolicLinks();
      final relative = p.relative(canonicalParent, from: canonicalRoot);
      if (relative == '.' ||
          (!p.isAbsolute(relative) &&
              relative != '..' &&
              !relative.startsWith('..${p.separator}'))) {
        accepted = true;
        break;
      }
    }
    if (!accepted || await File(target).exists()) {
      throw const AppPrivateApiException(
        'invalid_export_path',
        '仅可导出到桌面、文档或应用导出目录，且文件名不能与现有文件重复',
      );
    }
    return target;
  }

  Future<Map<String, dynamic>> _exportStandalone(
    Map<String, dynamic> parameters,
  ) async {
    if (!Platform.isWindows) {
      throw const AppPrivateApiException(
        'windows_required',
        '独立播放器需要在 Windows 版本中生成',
      );
    }
    final project = await _requireProject(
      _requiredString(parameters, 'projectId'),
    );
    final outputPath = await _resolveExportPath(
      _requiredString(parameters, 'outputPath'),
      '.exe',
    );
    final temporaryDirectory = await Directory.systemTemp.createTemp(
      'intensive-listening-mcp-standalone-',
    );
    try {
      final packageVersion = project.packageVersion + 1;
      final ilp = await const ProjectDelivery().createIlp(
        project,
        File(p.join(temporaryDirectory.path, 'lesson.ilp')),
        packageVersion: packageVersion,
      );
      final output = await const StandaloneLessonExporter().create(
        ilpFile: ilp,
        outputFile: File(outputPath),
        packageUuid: project.packageUuid,
        packageVersion: packageVersion,
        title: project.title,
      );
      final updated = project.copyWith(
        packageVersion: packageVersion,
        lastExportPath: output.path,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      onProjectChanged();
      return {
        'projectId': project.id,
        'outputPath': output.path,
        'packageUuid': project.packageUuid,
        'packageVersion': packageVersion,
      };
    } on IlpException catch (error) {
      throw AppPrivateApiException('export_failed', error.message);
    } on StandaloneLessonExportException catch (error) {
      throw AppPrivateApiException('export_failed', error.message);
    } finally {
      if (await temporaryDirectory.exists()) {
        await temporaryDirectory.delete(recursive: true);
      }
    }
  }

  _SlicePage _cuePage(Map<String, dynamic> parameters, int total) {
    final offset =
        _optionalNonNegativeInt(parameters, 'offset') ??
        _optionalNonNegativeInt(parameters, 'cueOffset') ??
        0;
    final requestedLimit =
        _optionalPositiveInt(parameters, 'limit') ??
        _optionalPositiveInt(parameters, 'cueLimit');
    final limit = requestedLimit == null
        ? total
        : requestedLimit.clamp(1, 500).toInt();
    return _SlicePage(
      offset: offset.clamp(0, total).toInt(),
      limit: limit,
      total: total,
    );
  }

  _SlicePage _textPage(Map<String, dynamic> parameters, int total) {
    final offset = _optionalNonNegativeInt(parameters, 'offset') ?? 0;
    final limit = (_optionalPositiveInt(parameters, 'limit') ?? 12000)
        .clamp(1, 50000)
        .toInt();
    return _SlicePage(
      offset: offset.clamp(0, total).toInt(),
      limit: limit,
      total: total,
    );
  }

  Map<String, dynamic> _cueResponse(
    List<SrtCue> cues,
    SrtTranscriptStructure structure,
    int index,
  ) => {
    'index': index,
    'startMs': cues[index].start.inMilliseconds,
    'endMs': cues[index].end.inMilliseconds,
    'text': cues[index].text,
    'sectionIndex': structure.sectionIndexForCue(index),
    'isMarker': structure.markerCueIndexes.contains(index),
    'words': [
      for (final part in tokenizeLessonText(cues[index].text))
        if (part.isWord) {'index': part.wordIndex, 'text': part.text},
    ],
  };

  List<Map<String, dynamic>> _sectionResponse(
    SrtTranscriptStructure structure,
  ) => [
    for (var index = 0; index < structure.sections.length; index++)
      {
        'index': index,
        'label': structure.sections[index].label,
        'cueIndexes': structure.sections[index].cueIndexes,
      },
  ];

  List<Map<String, dynamic>> _materialResponse(CourseProject project) => [
    for (final material in project.exercises.effectiveMaterials)
      {
        ...material.toJson(),
        'questions': [
          for (final question in project.exercises.questionsForMaterial(
            material,
          ))
            {
              'id': question.id,
              'number': question.number,
              'title': question.title,
              'options': question.options,
              'answerIndex': question.answerIndex,
            },
        ],
      },
  ];

  Map<String, List<int>> _clozeResponse(LessonExercises exercises) => {
    for (final entry in exercises.clozeWordIndexes.entries)
      '${entry.key}': (entry.value.toList()..sort()),
  };

  bool _sameTimeline(List<SrtCue> left, List<SrtCue> right) {
    if (left.length != right.length) return false;
    for (var index = 0; index < left.length; index++) {
      if (left[index].start != right[index].start ||
          left[index].end != right[index].end) {
        return false;
      }
    }
    return true;
  }

  LessonExercises _exercisesForTranscript(
    LessonExercises exercises,
    List<SrtCue> cues,
  ) {
    final cloze = <int, Set<int>>{};
    for (final entry in exercises.clozeWordIndexes.entries) {
      if (entry.key < 0 || entry.key >= cues.length) continue;
      final wordCount = tokenizeLessonText(cues[entry.key].text)
          .where((part) => part.isWord)
          .length;
      final retained = entry.value
          .where((index) => index >= 0 && index < wordCount)
          .toSet();
      if (retained.isNotEmpty) cloze[entry.key] = retained;
    }
    return LessonExercises(
      materials: exercises.effectiveMaterials,
      questions: exercises.questions,
      clozeWordIndexes: cloze,
    );
  }

  List<SrtCue> _parseCues(CourseProject project) {
    if (!project.hasTranscript) {
      throw const AppPrivateApiException('transcript_required', '项目尚无字幕');
    }
    return SrtParser.parse(
      utf8.encode(project.transcript),
      project.audioDuration ?? const Duration(days: 7),
    );
  }

  Future<CourseProject> _requireProject(String projectId) async {
    final projects = await _store.loadAll();
    for (final project in projects) {
      if (project.id == projectId) return project;
    }
    throw AppPrivateApiException('project_not_found', '找不到项目：$projectId');
  }

  Map<String, dynamic> _projectSummary(CourseProject project) => {
    'id': project.id,
    'title': project.title,
    'step': project.step.name,
    'hasAudio': project.hasAudio,
    'hasTranscript': project.hasTranscript,
    'audioPath': project.audioPath,
    'durationMs': project.audioDuration?.inMilliseconds,
    'packageUuid': project.packageUuid,
    'packageVersion': project.packageVersion,
    'questionPlanPending':
        project.autoQuestionPlanDeferred &&
        project.hasTranscript &&
        !project.automaticQuestionPlanApplied,
    'updatedAt': project.updatedAt.toIso8601String(),
  };

  Map<String, dynamic> _examDocumentResponse(
    CourseProjectExamDocument document,
  ) => {
    'projectId': document.project.id,
    'sourceName': document.sourceName,
    'documentPath': document.documentPath,
    'textPath': document.textPath,
    'text': document.text,
    'sha256': document.sha256,
    'paragraphCount': document.paragraphCount,
    'tableCount': document.tableCount,
  };

  String _requiredString(Map<String, dynamic> source, String key) {
    final value = _optionalString(source, key);
    if (value == null) {
      throw AppPrivateApiException('invalid_parameters', '$key 不能为空');
    }
    return value;
  }

  String? _optionalString(Map<String, dynamic> source, String key) {
    final value = source[key];
    if (value is! String || value.trim().isEmpty) return null;
    return value.trim();
  }

  int _requiredIndex(Map<String, dynamic> source, String key) {
    final value = source[key];
    if (value is! num || value.round() < 0) {
      throw AppPrivateApiException('invalid_parameters', '$key 必须是非负整数');
    }
    return value.round();
  }

  int? _optionalNonNegativeInt(Map<String, dynamic> source, String key) {
    if (!source.containsKey(key)) return null;
    final value = source[key];
    if (value is! num || value.round() < 0) {
      throw AppPrivateApiException('invalid_parameters', '$key 必须是非负整数');
    }
    return value.round();
  }

  int? _optionalPositiveInt(Map<String, dynamic> source, String key) {
    if (!source.containsKey(key)) return null;
    final value = source[key];
    if (value is! num || value.round() < 1) {
      throw AppPrivateApiException('invalid_parameters', '$key 必须是正整数');
    }
    return value.round();
  }
}

LessonExercises _planAgentProjectQuestions(CourseProject project) {
  final cues = SrtParser.parse(
    utf8.encode(project.transcript),
    project.audioDuration ?? const Duration(days: 7),
  );
  return const SrtQuestionPlanner().plan(
    cues,
    clozeWordIndexes: project.exercises.clozeWordIndexes,
  );
}

class _SlicePage {
  const _SlicePage({
    required this.offset,
    required this.limit,
    required this.total,
  });

  final int offset;
  final int limit;
  final int total;

  int get end => (offset + limit).clamp(0, total).toInt();

  Map<String, dynamic> toJson() => {
    'offset': offset,
    'limit': limit,
    'returned': end - offset,
    'total': total,
    'hasMore': end < total,
  };
}
