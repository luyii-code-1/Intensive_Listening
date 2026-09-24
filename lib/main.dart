import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:file_picker/file_picker.dart';
import 'package:fluent_ui/fluent_ui.dart';
import 'package:scrollable_positioned_list/scrollable_positioned_list.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart' show PointerScrollEvent;
import 'package:flutter/rendering.dart' show RenderAbstractViewport;
import 'package:flutter/services.dart';
import 'package:local_notifier/local_notifier.dart';
import 'package:media_kit/media_kit.dart';
import 'package:path/path.dart' as p;

import 'app_directories.dart';
import 'asr/asr_client.dart';
import 'asr/segmented_asr_runner.dart';
import 'audio/audio_duration.dart';
import 'audio/supported_audio_formats.dart';
import 'ilp/cloze_sync.dart';
import 'ilp/ilp_creator.dart';
import 'ilp/ilp_importer.dart';
import 'ilp/ilp_library.dart';
import 'ilp/ilp_models.dart';
import 'ilp/lesson_exercises.dart';
import 'ilp/package_dialogs.dart';
import 'ilp/srt_parser.dart';
import 'ilp/srt_question_planner.dart';
import 'ilp/srt_sections.dart';
import 'ilp/standalone_lesson_exporter.dart';
import 'mcp/app_private_api.dart';
import 'projects/course_project.dart';
import 'projects/project_delivery.dart';
import 'settings/app_settings.dart';
import 'settings/api_configuration_archive.dart';
import 'settings/app_update.dart';
import 'settings/file_association.dart';
import 'student/lesson_progress_store.dart';
import 'transcription/duplicate_dialog.dart';
import 'transcription/duplicate_match.dart';
import 'transcription/queue_store.dart';
import 'transcription/srt_recognition_cache.dart';
import 'transcription/transcription_queue.dart';
import 'transcription/transcription_queue_panel.dart';
import 'widgets/confirm_delete_dialog.dart';
import 'widgets/empty_state.dart';
import 'widgets/spring_motion.dart';
import 'widgets/stacked_info_bars.dart';

@visibleForTesting
Directory? debugLibraryDirectory;

@visibleForTesting
File? debugQueueFile;

typedef DebugFilePicker = Future<String?> Function({
  required List<String> allowedExtensions,
  required String dialogTitle,
});

@visibleForTesting
DebugFilePicker? debugPickFile;

Future<Directory> resolveLessonLibraryDirectory() async {
  final override = debugLibraryDirectory;
  if (override != null) return override;
  if (!kIsWeb && Platform.isWindows) {
    final standalone = Platform.environment['ILP_STANDALONE_LIBRARY']?.trim();
    if (standalone != null && standalone.isNotEmpty) {
      return Directory(standalone);
    }
  }
  final supportDirectory = await intensiveListeningDataDirectory();
  return Directory(p.join(supportDirectory.path, 'library'));
}

@visibleForTesting
Future<Duration?> Function(String path)? debugAudioDurationProbe;

Future<String?> pickFileWith({
  required List<String> allowedExtensions,
  required String dialogTitle,
}) async {
  final override = debugPickFile;
  if (override != null) {
    return override(
      allowedExtensions: allowedExtensions,
      dialogTitle: dialogTitle,
    );
  }
  final selection = await FilePicker.pickFile(
    type: FileType.custom,
    allowedExtensions: allowedExtensions,
    dialogTitle: dialogTitle,
  );
  return selection?.path;
}

@visibleForTesting
const paneToggleButtonKey = Key('pane-toggle-button');

@visibleForTesting
const teacherWorkspaceDividerKey = Key('teacher-workspace-divider');

@visibleForTesting
Key reviewCueSelectionHandleKey(int cueIndex) =>
    ValueKey('review-cue-selection-$cueIndex');

@visibleForTesting
const reviewQuestionOverviewButtonKey = Key('review-question-overview-button');

@visibleForTesting
Key studentLessonRemoveButtonKey(String lessonId) =>
    ValueKey('student-lesson-remove-$lessonId');

@visibleForTesting
int activeCueIndexForPosition(List<SrtCue> cues, Duration position) {
  if (cues.isEmpty) return -1;
  for (var index = cues.length - 1; index >= 0; index--) {
    if (position >= cues[index].start) return index;
  }
  return 0;
}

@visibleForTesting
Duration cueSeekPosition(SrtCue cue) {
  final offset = cue.start + const Duration(milliseconds: 50);
  return offset < cue.end ? offset : cue.start;
}

Duration cueNavigationPosition(SrtCue cue) {
  final position = cue.start - const Duration(milliseconds: 200);
  return position > Duration.zero ? position : Duration.zero;
}

Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();
  if (!kIsWeb) {
    // A throw here would happen before runApp, so the first frame never
    // renders and the window never appears while the process stays alive.
    try {
      MediaKit.ensureInitialized();
    } catch (error) {
      runApp(StartupFailure(error: error));
      return;
    }
  }
  var initialPackagePath = arguments
      .where((argument) => argument.toLowerCase().endsWith('.ilp'))
      .firstOrNull;
  if (initialPackagePath == null &&
      Platform.environment['ILP_STANDALONE_LESSON'] == '1') {
    final candidate = p.join(
      p.dirname(Platform.resolvedExecutable),
      'lesson.ilp',
    );
    if (File(candidate).existsSync()) {
      initialPackagePath = candidate;
    }
  }
  runApp(IntensiveListeningApp(initialPackagePath: initialPackagePath));
  if (!kIsWeb && Platform.isWindows) {
    unawaited(_setupWindowsNotifications());
  }
}

Future<void> _setupWindowsNotifications() async {
  try {
    await localNotifier.setup(appName: 'Intensive Listening');
  } catch (_) {}
}

class StartupFailure extends StatelessWidget {
  const StartupFailure({super.key, required this.error});

  final Object error;

  @override
  Widget build(BuildContext context) {
    return FluentApp(
      title: 'Intensive Listening',
      debugShowCheckedModeBanner: false,
      theme: _buildAppTheme(),
      home: ScaffoldPage(
        content: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(FluentIcons.error_badge, size: 48),
              const SizedBox(height: 16),
              const Text('应用组件加载失败'),
              const SizedBox(height: 8),
              const Text('当前安装包不完整，请使用完整的 Windows 发布包。'),
              const SizedBox(height: 16),
              Text('$error'),
            ],
          ),
        ),
      ),
    );
  }
}

class IntensiveListeningApp extends StatefulWidget {
  const IntensiveListeningApp({super.key, this.initialPackagePath});

  final String? initialPackagePath;

  @override
  State<IntensiveListeningApp> createState() => _IntensiveListeningAppState();
}

class _IntensiveListeningAppState extends State<IntensiveListeningApp> {
  ThemeMode _themeMode = ThemeMode.system;

  @override
  void initState() {
    super.initState();
    unawaited(_loadThemeMode());
  }

  Future<void> _loadThemeMode() async {
    final settings = await const AppSettingsStore().load();
    if (mounted) {
      setState(() {
        _themeMode = _parseThemeMode(settings.themeMode);
      });
    }
  }

  void _onThemeChanged(String mode) {
    if (mounted) {
      setState(() {
        _themeMode = _parseThemeMode(mode);
      });
    }
  }

  static ThemeMode _parseThemeMode(String mode) {
    return switch (mode) {
      'light' => ThemeMode.light,
      'dark' => ThemeMode.dark,
      _ => ThemeMode.system,
    };
  }

  @override
  Widget build(BuildContext context) {
    return FluentApp(
      title: 'Intensive Listening',
      debugShowCheckedModeBanner: false,
      themeMode: _themeMode,
      theme: _buildAppTheme(),
      darkTheme: _buildDarkAppTheme(),
      home: AppShell(
        initialPackagePath: widget.initialPackagePath,
        onThemeChanged: _onThemeChanged,
      ),
    );
  }
}

FluentThemeData _buildAppTheme() {
  const fontFamily = 'SourceHanSansSC';
  const textColor = Color(0xE4000000);
  const typography = Typography.raw(
    display: TextStyle(
      fontFamily: fontFamily,
      fontSize: 48,
      height: 1.2,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    titleLarge: TextStyle(
      fontFamily: fontFamily,
      fontSize: 30,
      height: 1.25,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    title: TextStyle(
      fontFamily: fontFamily,
      fontSize: 22,
      height: 1.3,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    subtitle: TextStyle(
      fontFamily: fontFamily,
      fontSize: 17,
      height: 1.4,
      fontWeight: FontWeight.w500,
      color: textColor,
    ),
    bodyLarge: TextStyle(
      fontFamily: fontFamily,
      fontSize: 16,
      height: 1.4,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
    bodyStrong: TextStyle(
      fontFamily: fontFamily,
      fontSize: 14,
      height: 20 / 14,
      fontWeight: FontWeight.w500,
      color: textColor,
    ),
    body: TextStyle(
      fontFamily: fontFamily,
      fontSize: 14,
      height: 20 / 14,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
    caption: TextStyle(
      fontFamily: fontFamily,
      fontSize: 12,
      height: 1.5,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
  );
  return FluentThemeData(
    accentColor: Colors.red,
    fastAnimationDuration: const Duration(milliseconds: 220),
    brightness: Brightness.light,
    fontFamily: fontFamily,
    typography: typography,
    navigationPaneTheme: const NavigationPaneThemeData(
      animationDuration: Duration(milliseconds: 210),
      animationCurve: Curves.easeOutCubic,
    ),
    scaffoldBackgroundColor: Colors.grey[10],
    visualDensity: VisualDensity.standard,
  );
}

FluentThemeData _buildDarkAppTheme() {
  const fontFamily = 'SourceHanSansSC';
  const textColor = Color(0xF2FFFFFF);
  const typography = Typography.raw(
    display: TextStyle(
      fontFamily: fontFamily,
      fontSize: 48,
      height: 1.2,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    titleLarge: TextStyle(
      fontFamily: fontFamily,
      fontSize: 30,
      height: 1.25,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    title: TextStyle(
      fontFamily: fontFamily,
      fontSize: 22,
      height: 1.3,
      fontWeight: FontWeight.w700,
      color: textColor,
    ),
    subtitle: TextStyle(
      fontFamily: fontFamily,
      fontSize: 17,
      height: 1.4,
      fontWeight: FontWeight.w500,
      color: textColor,
    ),
    bodyLarge: TextStyle(
      fontFamily: fontFamily,
      fontSize: 16,
      height: 1.4,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
    bodyStrong: TextStyle(
      fontFamily: fontFamily,
      fontSize: 14,
      height: 20 / 14,
      fontWeight: FontWeight.w500,
      color: textColor,
    ),
    body: TextStyle(
      fontFamily: fontFamily,
      fontSize: 14,
      height: 20 / 14,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
    caption: TextStyle(
      fontFamily: fontFamily,
      fontSize: 12,
      height: 1.5,
      fontWeight: FontWeight.w400,
      color: textColor,
    ),
  );
  return FluentThemeData(
    accentColor: Colors.red,
    fastAnimationDuration: const Duration(milliseconds: 220),
    brightness: Brightness.dark,
    fontFamily: fontFamily,
    typography: typography,
    navigationPaneTheme: const NavigationPaneThemeData(
      animationDuration: Duration(milliseconds: 210),
      animationCurve: Curves.easeOutCubic,
    ),
    scaffoldBackgroundColor: const Color(0xFF202020),
    cardColor: const Color(0xFF2C2C2C),
    visualDensity: VisualDensity.standard,
  );
}

class AppShell extends StatefulWidget {
  const AppShell({super.key, this.initialPackagePath, this.onThemeChanged});

  final String? initialPackagePath;
  final ValueChanged<String>? onThemeChanged;

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  static const _windowChannel = MethodChannel('intensive_listening/window');
  var _selectedIndex = 0;
  var _settingsReturnIndex = 0;
  var _paneExpanded = true;
  final _libraryChanged = StreamController<void>.broadcast();
  final _openLessonRequests = StreamController<ImportedLesson>.broadcast();
  final _loadSrtRequests = StreamController<TranscriptionJob>.broadcast();
  final _projectChanged = StreamController<void>.broadcast();
  final _openProjectRequests = StreamController<String>.broadcast();
  final _settingsStore = const AppSettingsStore();
  final _promptedDecisions = <String>{};
  final _announcedJobs = <String>{};
  final _applyingCompletedJobs = <String>{};
  final _noticeController = StackedInfoBarController();
  var _settings = AppSettings.defaults();
  var _agentActive = false;
  DateTime? _lastAgentAccessNotice;
  late final TranscriptionQueue _transcriptionQueue;
  AppPrivateApiServer? _privateApiServer;

  // Page identity keys — keep State alive across rebuilds.
  final _studentPageKey = GlobalKey();
  final _teacherPageKey = GlobalKey<_TeacherPageState>();
  final _settingsPageKey = GlobalKey();

  @override
  void initState() {
    super.initState();
    _transcriptionQueue = TranscriptionQueue(
      runner: _runTranscription,
      resolveDuplicate: _resolveDuplicate,
      store: _queueStore(),
      cache: SrtRecognitionCache(),
      resolveCacheProfile: () => _settings.asrCacheProfile,
    )..addListener(_onQueueChanged);
    unawaited(_loadSettings());
    if (widget.initialPackagePath == null) {
      unawaited(_transcriptionQueue.restore());
    }
  }

  @override
  void dispose() {
    _transcriptionQueue.removeListener(_onQueueChanged);
    unawaited(_transcriptionQueue.disposeQueue());
    _libraryChanged.close();
    _openLessonRequests.close();
    _loadSrtRequests.close();
    _projectChanged.close();
    _openProjectRequests.close();
    _noticeController.dispose();
    unawaited(_privateApiServer?.stop());
    super.dispose();
  }

  void _refreshStudentLibrary() => _libraryChanged.add(null);

  QueueStore _queueStore() {
    final override = debugQueueFile;
    if (override == null) return QueueStore();
    return QueueStore(resolveFile: () async => override);
  }

  Future<Directory> _libraryDirectory() async {
    return resolveLessonLibraryDirectory();
  }

  Future<String> _runTranscription({
    required File audioFile,
    AsrProgressCallback? onProgress,
    Future<void>? abortTrigger,
    Duration? estimatedProcessingTime,
  }) {
    final config = _settings.cloudAsrConfig;
    if (!config.isComplete) {
      throw const AsrException('请在设置中填写服务地址、模型和密钥。');
    }
    final timeoutSeconds = _settings.cloudTimeoutSeconds.clamp(30, 1800);
    return SegmentedAsrRunner(
      config: config,
      concurrency: _settings.cloudConcurrency,
      timeout: Duration(seconds: timeoutSeconds),
    ).call(
      audioFile: audioFile,
      onProgress: onProgress,
      abortTrigger: abortTrigger,
      estimatedProcessingTime: estimatedProcessingTime,
      confirmForcedCuts: _confirmForcedCuts,
    );
  }

  Future<bool> _confirmForcedCuts() async {
    if (!mounted) return false;
    if (Platform.isWindows) {
      try {
        await _windowChannel.invokeMethod<void>('showWindow');
      } catch (_) {}
    }
    if (!mounted) return false;
    final accepted = await showSpringDialog<bool>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('未找到合适的静音切点'),
        content: const Text('音频将在 120 秒边界强制切开，切点附近可能落在单词中间。是否本次继续转写？'),
        actions: [
          Button(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: const Text('取消任务'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: const Text('强制切片并重试'),
          ),
        ],
      ),
    );
    return accepted == true;
  }

  Future<DuplicateMatch?> _resolveDuplicate(String sha256) async {
    final ref = await IlpLibrary(await _libraryDirectory())
        .findByAudioSha256(sha256);
    if (ref == null) return null;
    return DuplicateMatch(
      duplicateCase: DuplicateCase.sharedAudioLesson,
      existingTitle: ref.manifest.title,
      lessonId: ref.id,
    );
  }

  void _onQueueChanged() {
    if (!mounted) return;
    unawaited(_applyCompletedTranscriptions());
    for (final job in _transcriptionQueue.pendingDecisions) {
      if (_promptedDecisions.add(job.id)) unawaited(_askAboutDuplicate(job));
    }
    for (final job in _transcriptionQueue.jobs) {
      if (!job.isTerminal) continue;
      final finishedAt = job.finishedAt;
      if (finishedAt == null) continue;
      if (DateTime.now().difference(finishedAt) > const Duration(seconds: 10)) {
        continue;
      }
      if (_announcedJobs.add(job.id)) _showQueueNotice(job);
    }
  }

  Future<void> _applyCompletedTranscriptions() async {
    for (final job in _transcriptionQueue.jobs) {
      if (job.status != TranscriptionJobStatus.completed ||
          job.srtConsumed ||
          job.projectId == null ||
          !_applyingCompletedJobs.add(job.id)) {
        continue;
      }
      try {
        final store = const CourseProjectStore();
        final project = await store.loadById(job.projectId!);
        if (project == null || project.transcriptionJobId != job.id) continue;
        final transcript = job.srt ?? '';
        final updated =
            transcript.trim().isEmpty || project.transcript == transcript
            ? project.copyWith(transcriptionJobId: null)
            : project.copyWith(
                transcript: transcript,
                transcriptionJobId: null,
                step: CourseProjectStep.review,
                reviewPhase: ReviewPhase.grouping,
                exercises: const LessonExercises(),
                automaticQuestionPlanApplied: false,
              );
        await store.save(updated);
        _transcriptionQueue.markSrtConsumed(job.id);
        if (!_projectChanged.isClosed) _projectChanged.add(null);
      } catch (error) {
        debugPrint('无法同步转写结果: $error');
      } finally {
        _applyingCompletedJobs.remove(job.id);
      }
    }
  }

  void _showQueueNotice(TranscriptionJob job) {
    final notice = switch (job.status) {
      TranscriptionJobStatus.completed => TeacherNotice.success(
        '转写完成',
        (job.srt?.trim().isNotEmpty ?? false)
            ? '「${job.title}」的字幕已写入 SRT。'
            : '「${job.title}」未识别到可用语音。',
      ),
      TranscriptionJobStatus.failed => TeacherNotice.error('转写失败', job.message),
      _ => null,
    };
    if (notice == null) return;
    if (job.status == TranscriptionJobStatus.completed &&
        !kIsWeb &&
        Platform.isWindows) {
      unawaited(
        LocalNotification(
          title: '转写完成',
          body: (job.srt?.trim().isNotEmpty ?? false)
              ? '「${job.title}」的字幕已经可以审阅。'
              : '「${job.title}」未识别到可用语音。',
        ).show().catchError((_) {}),
      );
    }
    _noticeController.show(
      title: notice.title,
      message: notice.message,
      severity: notice.severity,
      duration: const Duration(seconds: 5),
    );
  }

  Future<void> _askAboutDuplicate(TranscriptionJob job) async {
    final duplicate = job.duplicate;
    if (duplicate == null) return;
    final choice = await showDuplicateDialog(
      context: context,
      duplicateCase: duplicate.duplicateCase,
      existingTitle: duplicate.existingTitle,
    );
    if (!mounted) return;
    if (choice == DuplicateChoice.openExisting) {
      _transcriptionQueue.resolve(job.id, proceed: false);
      await _openExisting(duplicate);
      return;
    }
    _transcriptionQueue.resolve(
      job.id,
      proceed: choice == DuplicateChoice.continueAnyway,
    );
  }

  Future<void> _openExisting(DuplicateMatch duplicate) async {
    if (duplicate.jobId != null) {
      setState(() => _selectedIndex = 1);
      return;
    }
    final lessonId = duplicate.lessonId;
    if (lessonId == null) return;
    final lesson = await IlpLibrary(await _libraryDirectory())
        .loadById(lessonId);
    if (lesson == null || !mounted) return;
    setState(() => _selectedIndex = 0);
    _openLessonRequests.add(lesson);
  }

  Future<void> _loadSettings() async {
    var settings = await _settingsStore.load();
    if (!mounted) return;
    widget.onThemeChanged?.call(settings.themeMode);
    setState(() => _settings = settings);
    if (!kIsWeb &&
        Platform.isWindows &&
        !Platform.environment.containsKey('FLUTTER_TEST') &&
        settings.eulaAcceptedVersion != '2026-09-22') {
      await WidgetsBinding.instance.endOfFrame;
      if (!mounted) return;
      final accepted = await _showEula(acceptanceRequired: true);
      if (accepted != true) {
        exit(0);
      }
      settings = settings.copyWith(eulaAcceptedVersion: '2026-09-22');
      await _settingsStore.save(settings);
      if (!mounted) return;
      setState(() => _settings = settings);
    }
    if (!kIsWeb &&
        Platform.isWindows &&
        settings.fileAssociationEnabled &&
        !Platform.environment.containsKey('FLUTTER_TEST')) {
      try {
        await const IlpFileAssociation().apply(true);
      } catch (error) {
        if (mounted) _showShellNotice('文件关联未更新', '$error');
      }
    }
    if (widget.initialPackagePath != null) return;
    if (!kIsWeb &&
        Platform.isWindows &&
        !Platform.environment.containsKey('FLUTTER_TEST') &&
        !settings.fileAssociationPrompted &&
        !settings.fileAssociationEnabled) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) unawaited(_showFileAssociationPrompt());
      });
    }
    try {
      await _applyMcpSetting(settings.mcpEnabled);
    } catch (error) {
      settings = _settings.copyWith(mcpEnabled: false);
      await _settingsStore.save(settings);
      if (!mounted) return;
      setState(() => _settings = settings);
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _showShellNotice('MCP 启动失败', '$error');
      });
    }
  }

  Future<bool?> _showEula({bool acceptanceRequired = false}) async {
    final agreement = await rootBundle.loadString(
      'assets/legal/eula_zh_cn.txt',
    );
    if (!mounted) return null;
    return showSpringDialog<bool>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('最终用户许可协议'),
        constraints: const BoxConstraints(maxWidth: 680, maxHeight: 680),
        content: SizedBox(
          width: 620,
          height: 460,
          child: SingleChildScrollView(child: SelectableText(agreement)),
        ),
        actions: [
          Button(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: Text(acceptanceRequired ? '退出应用' : '关闭'),
          ),
          if (acceptanceRequired)
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext, true),
              child: const Text('同意并继续'),
            ),
        ],
      ),
    );
  }

  Future<void> _showFileAssociationPrompt() async {
    final associate = await showSpringDialog<bool>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('打开精听包'),
        content: const Text('是否关联 .ilp 文件？关联后，双击精听包即可直接进入播放界面。'),
        actions: [
          Button(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('暂不关联'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('关联 .ilp'),
          ),
        ],
      ),
    );
    if (!mounted) return;
    final next = _settings.copyWith(fileAssociationPrompted: true);
    if (associate == true) {
      try {
        await const IlpFileAssociation().apply(true);
        final enabled = next.copyWith(fileAssociationEnabled: true);
        await _settingsStore.save(enabled);
        if (mounted) setState(() => _settings = enabled);
        if (mounted) _showShellNotice('文件关联已启用', '双击 .ilp 文件即可打开播放界面。');
        return;
      } catch (error) {
        if (mounted) {
          _noticeController.show(
            title: '文件关联失败',
            message: '$error',
            severity: InfoBarSeverity.error,
          );
        }
      }
    }
    await _settingsStore.save(next);
    if (mounted) setState(() => _settings = next);
  }

  Future<void> _saveSettings(AppSettings settings) async {
    final previous = _settings;
    setState(() => _settings = settings);
    widget.onThemeChanged?.call(settings.themeMode);
    await _settingsStore.save(settings);
    if (previous.fileAssociationEnabled != settings.fileAssociationEnabled) {
      await const IlpFileAssociation().apply(settings.fileAssociationEnabled);
    }
    try {
      await _applyMcpSetting(settings.mcpEnabled);
    } catch (_) {
      final fallback = settings.copyWith(mcpEnabled: false);
      await _settingsStore.save(fallback);
      if (mounted) setState(() => _settings = fallback);
      rethrow;
    }
  }

  Future<CourseProject> _startProjectAsrFromAgent(CourseProject project) async {
    if (!_settings.cloudReady) {
      throw const AppPrivateApiException('api_not_configured', '云端转写配置尚未完成');
    }
    final jobId = _transcriptionQueue.enqueue(
      projectId: project.id,
      title: project.title,
      audio: File(project.audioPath!),
      audioDuration: project.audioDuration,
    );
    final updated = project.copyWith(
      step: CourseProjectStep.transcription,
      transcriptionJobId: jobId,
      autoQuestionPlanDeferred: true,
    );
    await const CourseProjectStore().save(updated);
    unawaited(_applyCompletedTranscriptions());
    return updated;
  }

  void _retireProjectTranscriptionForAgent(CourseProject project) {
    final jobId = project.transcriptionJobId;
    if (jobId == null) return;
    final job = _transcriptionQueue.jobById(jobId);
    if (job == null) return;
    if (job.isActive || job.status == TranscriptionJobStatus.interrupted) {
      _transcriptionQueue.cancel(jobId);
    }
    _transcriptionQueue.markSrtConsumed(jobId);
  }

  Future<void> _applyMcpSetting(bool enabled) async {
    if (kIsWeb || !Platform.isWindows) return;
    final isFlutterTest = Platform.environment.containsKey('FLUTTER_TEST');
    if (isFlutterTest) return;
    if (!enabled) {
      await _privateApiServer?.stop();
      _privateApiServer = null;
      await _windowChannel.invokeMethod<void>('enableTray', false);
      if (mounted && _agentActive) setState(() => _agentActive = false);
      return;
    }
    if (_privateApiServer?.isRunning == true) return;
    final service = AppPrivateApiService(
      onProjectChanged: () {
        if (!_projectChanged.isClosed) _projectChanged.add(null);
      },
      onOpenProject: (projectId) {
        if (!mounted) return;
        setState(() => _selectedIndex = 1);
        Future<void>.delayed(const Duration(milliseconds: 160), () {
          if (!_openProjectRequests.isClosed) {
            _openProjectRequests.add(projectId);
          }
        });
      },
      startAsr: _startProjectAsrFromAgent,
      onTranscriptReplacing: _retireProjectTranscriptionForAgent,
      resolveLibraryDirectory: resolveLessonLibraryDirectory,
      onLibraryChanged: _refreshStudentLibrary,
    );
    final server = AppPrivateApiServer(
      dispatch: service.dispatch,
      onAgentStateChanged: (active) {
        if (mounted && active != _agentActive) {
          setState(() => _agentActive = active);
        }
      },
      onAgentAccessDenied: () {
        final now = DateTime.now();
        if (!mounted ||
            (_lastAgentAccessNotice != null &&
                now.difference(_lastAgentAccessNotice!) <
                    const Duration(seconds: 5))) {
          return;
        }
        _lastAgentAccessNotice = now;
        _showShellNotice('智能体会话未连接', '请先以 event=Agent 建立连接。');
      },
    );
    _privateApiServer = server;
    await server.start();
    try {
      await _windowChannel.invokeMethod<void>('enableTray', true);
    } catch (_) {
      await server.stop();
      _privateApiServer = null;
      rethrow;
    }
  }

  Future<void> _forceDisconnectAgent() async {
    _privateApiServer?.disconnectAgent();
    if (mounted) _showShellNotice('已返回用户模式', 'MCP 与 HTTP 入口保持可用。');
  }

  void _openSettings() {
    setState(() {
      if (_selectedIndex != 2) _settingsReturnIndex = _selectedIndex;
      _selectedIndex = 2;
    });
  }

  void _returnFromSettings() {
    setState(() => _selectedIndex = _settingsReturnIndex);
  }

  void _selectDestination(int index) {
    if (index == 1 && _selectedIndex == 0) {
      unawaited(
        _teacherPageKey.currentState?._closeProject() ?? Future<void>.value(),
      );
    }
    setState(() {
      if (index == 2 && _selectedIndex != 2) {
        _settingsReturnIndex = _selectedIndex;
      }
      _selectedIndex = index;
    });
  }

  void _showShellNotice(String title, String message) {
    _noticeController.show(title: title, message: message);
  }

  Future<void> _showTaskCenter() async {
    final windowSize = MediaQuery.sizeOf(context);
    final dialogWidth = (windowSize.width * 0.618).clamp(640.0, 980.0);
    final dialogHeight = (windowSize.height * 0.72).clamp(480.0, 720.0);
    await showSpringDialog<void>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('转写任务'),
        constraints: BoxConstraints(
          minWidth: dialogWidth,
          maxWidth: dialogWidth,
          maxHeight: dialogHeight,
        ),
        content: SizedBox(
          width: dialogWidth,
          height: dialogHeight - 112,
          child: SingleChildScrollView(
            child: TranscriptionQueuePanel(
              queue: _transcriptionQueue,
              onTaskRemoved: (title) =>
                  _showShellNotice('任务已删除', '「$title」已从转写队列移除。'),
              onLoadSrt: (job) {
                Navigator.of(dialogContext).pop();
                setState(() => _selectedIndex = 1);
                Future<void>.delayed(
                  const Duration(milliseconds: 160),
                  () => _loadSrtRequests.add(job),
                );
              },
            ),
          ),
        ),
        actions: [
          Button(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: const Text('关闭'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    if (widget.initialPackagePath != null) {
      return StackedInfoBarScope(
        controller: _noticeController,
        child: Stack(
          children: [
            StudentPage(
              initialPackagePath: widget.initialPackagePath,
              compactPlayback: true,
              settings: _settings,
              libraryChanged: _libraryChanged.stream,
              openLessonRequests: _openLessonRequests.stream,
              transcriptionQueue: _transcriptionQueue,
              onOpenJob: _showTaskCenter,
            ),
            if (_agentActive) _agentBlockingOverlay(context),
            Positioned(
              right: 18,
              bottom: 18,
              width: (MediaQuery.sizeOf(context).width - 36).clamp(0.0, 380.0),
              child: StackedInfoBarHost(controller: _noticeController),
            ),
          ],
        ),
      );
    }
    final pages = [
      StudentPage(
        key: _studentPageKey,
        settings: _settings,
        onMediaOpened: () {
          if (_paneExpanded) setState(() => _paneExpanded = false);
        },
        initialPackagePath: widget.initialPackagePath,
        libraryChanged: _libraryChanged.stream,
        openLessonRequests: _openLessonRequests.stream,
        transcriptionQueue: _transcriptionQueue,
        onOpenJob: _showTaskCenter,
      ),
      TeacherPage(
        key: _teacherPageKey,
        onProjectOpened: () {
          if (_paneExpanded) setState(() => _paneExpanded = false);
        },
        onPackageCreated: _refreshStudentLibrary,
        onTranscriptionEnqueued: () =>
            unawaited(_applyCompletedTranscriptions()),
        settings: _settings,
        onOpenSettings: _openSettings,
        transcriptionQueue: _transcriptionQueue,
        loadSrtRequests: _loadSrtRequests.stream,
        externalProjectChanges: _projectChanged.stream,
        openProjectRequests: _openProjectRequests.stream,
      ),
      SettingsPage(
        key: _settingsPageKey,
        settings: _settings,
        onSettingsChanged: _saveSettings,
        onBack: _returnFromSettings,
        onShowEula: () async {
          await _showEula();
        },
      ),
    ];
    final navigation = NavigationView(
      transitionBuilder: (child, animation) =>
          FadeTransition(opacity: animation, child: child),
      pane: NavigationPane(
        selected: _selectedIndex,
        onChanged: _selectDestination,
        displayMode: _paneExpanded && MediaQuery.sizeOf(context).width >= 1200
            ? PaneDisplayMode.expanded
            : PaneDisplayMode.compact,
        toggleButtonPosition: PaneToggleButtonPreferredPosition.pane,
        toggleButton: PaneToggleButton(
          key: paneToggleButtonKey,
          onPressed: () => setState(() => _paneExpanded = !_paneExpanded),
        ),
        items: [
          PaneItem(
            icon: const Icon(FluentIcons.play),
            title: const Text('学生端'),
            body: pages[0],
          ),
          PaneItem(
            icon: const Icon(FluentIcons.education),
            title: const Text('教师端'),
            body: pages[1],
          ),
          PaneItem(
            icon: const Icon(FluentIcons.settings),
            title: const Text('设置'),
            body: pages[2],
          ),
        ],
        footerItems: [
          PaneItemSeparator(),
          PaneItemAction(
            icon: _TaskCenterIcon(queue: _transcriptionQueue),
            title: const Text('转写任务'),
            onTap: _showTaskCenter,
          ),
        ],
      ),
    );
    return StackedInfoBarScope(
      controller: _noticeController,
      child: Stack(
        children: [
          navigation,
          if (_agentActive) _agentBlockingOverlay(context),
          Positioned(
            right: 18,
            bottom: 18,
            width: (MediaQuery.sizeOf(context).width - 36).clamp(0.0, 380.0),
            child: StackedInfoBarHost(controller: _noticeController),
          ),
        ],
      ),
    );
  }

  Widget _agentBlockingOverlay(BuildContext context) => Positioned.fill(
    child: BackdropFilter(
      filter: ui.ImageFilter.blur(sigmaX: 12, sigmaY: 12),
      child: ColoredBox(
        color: Colors.black.withValues(alpha: 0.18),
        child: Center(
          child: Card(
            padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const ProgressRing(),
                const SizedBox(height: 18),
                Text(
                  '智能体正在工作',
                  style: FluentTheme.of(context).typography.subtitle,
                ),
                const SizedBox(height: 6),
                const Text('课程制作数据正在由 MCP 智能体处理。'),
                const SizedBox(height: 20),
                Button(
                  onPressed: _forceDisconnectAgent,
                  child: const Text('强制断开'),
                ),
              ],
            ),
          ),
        ),
      ),
    ),
  );
}

class _TaskCenterIcon extends StatelessWidget {
  const _TaskCenterIcon({required this.queue});

  final TranscriptionQueue queue;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: queue,
      builder: (context, _) {
        final active = queue.jobs.where((job) => job.isActive).length;
        return Stack(
          clipBehavior: Clip.none,
          children: [
            const Icon(FluentIcons.sync),
            if (active > 0)
              PositionedDirectional(
                end: -9,
                top: -9,
                child: InfoBadge(source: Text('$active')),
              ),
          ],
        );
      },
    );
  }
}

class StudentPage extends StatefulWidget {
  const StudentPage({
    super.key,
    this.initialPackagePath,
    this.compactPlayback = false,
    required this.settings,
    this.onMediaOpened,
    required this.libraryChanged,
    required this.openLessonRequests,
    required this.transcriptionQueue,
    required this.onOpenJob,
  });

  final String? initialPackagePath;
  final bool compactPlayback;
  final AppSettings settings;
  final VoidCallback? onMediaOpened;

  final Stream<void> libraryChanged;
  final Stream<ImportedLesson> openLessonRequests;
  final TranscriptionQueue transcriptionQueue;
  final VoidCallback onOpenJob;

  @override
  State<StudentPage> createState() => _StudentPageState();
}

class _StudentPageState extends State<StudentPage> {
  StreamSubscription<void>? _librarySubscription;
  StreamSubscription<ImportedLesson>? _openLessonSubscription;
  StreamSubscription<Duration>? _positionSubscription;
  StreamSubscription<Duration>? _durationSubscription;
  StreamSubscription<bool>? _playingSubscription;
  StreamSubscription<bool>? _completedSubscription;
  Timer? _progressSaveTimer;
  Player? _player;
  final _progressStore = const LessonProgressStore();
  var _lessonProgress = <String, LessonProgress>{};
  var _lessons = <ImportedLesson>[];
  ImportedLesson? _lesson;
  File? _standaloneAudio;
  var _position = Duration.zero;
  final _positionNotifier = ValueNotifier<Duration>(Duration.zero);
  var _duration = Duration.zero;
  var _playing = false;
  var _singleSentenceLoop = false;
  Duration? _sentenceLoopStartAt;
  Duration? _sentenceStopAt;
  var _handlingSentenceLoop = false;
  var _loading = true;
  var _busy = false;
  String? _openingLessonId;
  String? _deletingLessonId;
  var _handledInitialPackage = false;
  var _revealedCloze = <String>{};
  var _showAllCloze = false;
  var _showSubtitles = true;
  var _restoringLessonPosition = false;
  int? _preRollCueIndex;
  String? _selectedQuestionId;

  List<SrtCue> get _cues => _lesson?.cues ?? const [];

  int get _activeIndex {
    final target = _preRollCueIndex;
    if (target != null && target >= 0 && target < _cues.length) {
      final cue = _cues[target];
      if (_position >=
              cueNavigationPosition(cue) - const Duration(milliseconds: 80) &&
          _position < cue.start) {
        return target;
      }
    }
    return activeCueIndexForPosition(_cues, _position);
  }

  @override
  void initState() {
    super.initState();
    _librarySubscription = widget.libraryChanged.listen((_) => _loadLibrary());
    _openLessonSubscription = widget.openLessonRequests.listen((lesson) {
      if (mounted) unawaited(_openLesson(lesson));
    });
    unawaited(_loadLibrary());
  }

  @override
  void dispose() {
    _librarySubscription?.cancel();
    _openLessonSubscription?.cancel();
    _positionSubscription?.cancel();
    _durationSubscription?.cancel();
    _playingSubscription?.cancel();
    _completedSubscription?.cancel();
    _progressSaveTimer?.cancel();
    _positionNotifier.dispose();
    unawaited(_saveLessonProgress());
    _player?.dispose();
    super.dispose();
  }

  Future<Directory> _libraryDirectory() async {
    return resolveLessonLibraryDirectory();
  }

  Future<Player> _ensurePlayer() async {
    final current = _player;
    if (current != null) return current;
    final player = Player();
    _player = player;
    _positionSubscription = player.stream.position.listen((value) {
      final stopAt = _sentenceStopAt;
      final loopStart = _sentenceLoopStartAt;
      if (_singleSentenceLoop &&
          stopAt != null &&
          loopStart != null &&
          !_handlingSentenceLoop &&
          value >= stopAt) {
        _handlingSentenceLoop = true;
        unawaited(
          player
              .seek(loopStart)
              .then((_) => player.play())
              .whenComplete(() => _handlingSentenceLoop = false),
        );
      }
      if (!mounted) return;
      if (_restoringLessonPosition) return;
      if (_duration > Duration.zero &&
          value == Duration.zero &&
          _position > const Duration(milliseconds: 500)) {
        return;
      }
      if (_preRollCueIndex != null &&
          _preRollCueIndex! >= 0 &&
          _preRollCueIndex! < _cues.length) {
        if (value >= _cues[_preRollCueIndex!].start) {
          _preRollCueIndex = null;
        }
      }
      final diff = (value - _position).inMilliseconds.abs();
      final oldActive = _activeIndex;
      _position = value;
      final newActive = _activeIndex;
      if (diff >= 30 || oldActive != newActive || value == Duration.zero) {
        _positionNotifier.value = value;
        _scheduleProgressSave();
      }
      if (oldActive != newActive) setState(() {});
    });
    _durationSubscription = player.stream.duration.listen((value) {
      if (mounted && value > Duration.zero) setState(() => _duration = value);
    });
    _playingSubscription = player.stream.playing.listen((value) {
      if (mounted) setState(() => _playing = value);
    });
    _completedSubscription = player.stream.completed.listen((completed) {
      if (completed && mounted) setState(() => _playing = false);
    });
    return player;
  }

  Future<void> _loadLibrary() async {
    try {
      final directory = await _libraryDirectory();
      final results = await Future.wait([
        IlpLibrary(directory).load(),
        _progressStore.load(),
      ]);
      final result = results[0] as LibraryLoadResult;
      final progress = results[1] as Map<String, LessonProgress>;
      if (!mounted) return;
      setState(() {
        _lessons = [...result.lessons];
        _lessonProgress = progress;
        _loading = false;
      });
      final initialPath = widget.initialPackagePath;
      if (!_handledInitialPackage && initialPath != null) {
        _handledInitialPackage = true;
        await _importPackagePath(initialPath);
      }
    } catch (_) {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _openLesson(ImportedLesson lesson) async {
    widget.onMediaOpened?.call();
    setState(() => _openingLessonId = lesson.id);
    try {
      final verified = await IlpLibrary(await _libraryDirectory())
          .loadById(lesson.id);
      if (verified == null) {
        if (mounted) {
          await showResult(context, '无法打开课程', '课程文件缺失或完整性校验失败。');
        }
        return;
      }
      lesson = verified;
      final player = await _ensurePlayer();
      final progress = _lessonProgress[lesson.manifest.packageUuid];
      final savedPosition = clampDuration(
        progress?.position ?? Duration.zero,
        lesson.manifest.duration,
      );
      final firstQuestion = lesson.manifest.exercises.questions.firstOrNull;
      final firstCueIndex = firstQuestion == null
          ? null
          : lesson.manifest.exercises
                .materialForQuestion(firstQuestion.id)
                ?.cueIndexes
                .firstOrNull;
      final skipTo =
          widget.settings.skipOpeningPrompts &&
              progress == null &&
              firstCueIndex != null &&
              firstCueIndex >= 0 &&
              firstCueIndex < lesson.cues.length
          ? cueNavigationPosition(lesson.cues[firstCueIndex])
          : null;
      final resumeAt = skipTo ?? savedPosition;
      setState(() {
        _lesson = lesson;
        _standaloneAudio = null;
        _duration = lesson.manifest.duration;
        _position = resumeAt;
        _playing = false;
        _singleSentenceLoop = false;
        _sentenceLoopStartAt = null;
        _sentenceStopAt = null;
        _revealedCloze = {...?progress?.revealedCloze};
        _showAllCloze = false;
        _showSubtitles = true;
        _preRollCueIndex = skipTo == null ? null : firstCueIndex;
        _selectedQuestionId = null;
      });
      _positionNotifier.value = resumeAt;
      _restoringLessonPosition = true;
      try {
        await player.open(
          Media(
            File(lesson.audioPath).uri.toString(),
            start: resumeAt > Duration.zero ? resumeAt : null,
          ),
          play: false,
        );
        if (resumeAt > Duration.zero && resumeAt < lesson.manifest.duration) {
          await player.seek(resumeAt);
        }
        _position = resumeAt;
        _positionNotifier.value = resumeAt;
      } finally {
        _restoringLessonPosition = false;
      }
      _scheduleProgressSave(immediate: true);
    } finally {
      if (mounted) setState(() => _openingLessonId = null);
    }
  }

  void _scheduleProgressSave({bool immediate = false}) {
    if (_lesson == null) return;
    if (immediate) {
      _progressSaveTimer?.cancel();
      unawaited(_saveLessonProgress());
    } else {
      if (_progressSaveTimer?.isActive ?? false) return;
      _progressSaveTimer = Timer(
        const Duration(seconds: 5),
        () => unawaited(_saveLessonProgress()),
      );
    }
  }

  Future<void> _saveLessonProgress() async {
    final lesson = _lesson;
    if (lesson == null) return;
    final uuid = lesson.manifest.packageUuid;
    _lessonProgress[uuid] = LessonProgress(
      packageUuid: uuid,
      position: clampDuration(_position, _duration),
      lastOpenedAt: DateTime.now(),
      revealedCloze: {..._revealedCloze},
    );
    await _progressStore.save(_lessonProgress);
  }

  Future<void> _returnToStudentHome() async {
    await _saveLessonProgress();
    await _player?.pause();
    if (!mounted) return;
    setState(() {
      _lesson = null;
      _standaloneAudio = null;
      _position = Duration.zero;
      _duration = Duration.zero;
      _playing = false;
      _singleSentenceLoop = false;
      _sentenceLoopStartAt = null;
      _sentenceStopAt = null;
      _selectedQuestionId = null;
    });
    _positionNotifier.value = Duration.zero;
  }

  Future<void> _openStandaloneAudio() async {
    final selectedPath = await pickFileWith(
      allowedExtensions: standaloneAudioExtensions.toList(growable: false),
      dialogTitle: '打开音频',
    );
    if (selectedPath == null || !mounted) return;
    widget.onMediaOpened?.call();

    final audio = File(selectedPath);
    setState(() => _busy = true);
    try {
      final player = await _ensurePlayer();
      setState(() {
        _lesson = null;
        _standaloneAudio = audio;
        _duration = Duration.zero;
        _position = Duration.zero;
        _playing = false;
        _singleSentenceLoop = false;
        _sentenceLoopStartAt = null;
        _sentenceStopAt = null;
        _revealedCloze = {};
        _showAllCloze = false;
        _showSubtitles = true;
        _selectedQuestionId = null;
      });
      _positionNotifier.value = Duration.zero;
      await player.open(Media(audio.uri.toString()), play: false);
    } catch (error) {
      if (!mounted) return;
      setState(() => _standaloneAudio = null);
      await showResult(context, '无法打开音频', '文件格式不受支持或文件已经损坏。\n$error');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _importPackage() async {
    final selectedPath = await pickFileWith(
      allowedExtensions: const ['ilp'],
      dialogTitle: '选择精听包',
    );
    if (selectedPath == null || !mounted) return;

    await _importPackagePath(selectedPath);
  }

  Future<void> _importPackagePath(String selectedPath) async {
    final packageFile = File(selectedPath);
    setState(() => _busy = true);
    try {
      final directory = await _libraryDirectory();
      final library = IlpLibrary(directory);
      final importer = IlpImporter(directory);
      final manifest = await importer.readManifest(packageFile);
      final sameUuid = await library.findByUuid(manifest.packageUuid);
      if (sameUuid != null) {
        if (sameUuid.manifest.packageVersion == manifest.packageVersion) {
          final existing = await library.loadById(sameUuid.id);
          if (existing != null) await _openLesson(existing);
          if (mounted) _showStudentNotice('已打开课程', '本地已是相同版本。');
          return;
        }
        if (!mounted) return;
        final update = await confirmPackageUpdate(
          context: context,
          localVersion: sameUuid.manifest.packageVersion,
          incomingVersion: manifest.packageVersion,
        );
        if (!update) return;
        final lesson = await importer.importFile(
          packageFile,
          replaceExisting: true,
        );
        await _reloadAndOpen(lesson);
        if (mounted) _showStudentNotice('课程已更新', '播放记录与挖空记录已保留。');
        return;
      }

      final sameTitle = await library.findByTitle(manifest.title);
      String? titleOverride;
      LessonRef? lessonToReplace;
      if (sameTitle != null) {
        if (!mounted) return;
        final choice = await resolvePackageNameConflict(
          context: context,
          title: manifest.title,
        );
        if (choice == PackageNameConflictChoice.cancel) return;
        if (choice == PackageNameConflictChoice.replace) {
          // Keep the existing package intact until the incoming archive has
          // passed all extraction, hash, and SRT validation checks.
          lessonToReplace = sameTitle;
        } else {
          if (!mounted) return;
          titleOverride = await requestRenamedPackageTitle(
            context: context,
            initialTitle: _nextAvailableTitle(manifest.title),
          );
          if (titleOverride == null) return;
        }
      }

      final lesson = await importer.importFile(
        packageFile,
        titleOverride: titleOverride,
        replaceExisting: lessonToReplace != null,
      );
      if (lessonToReplace != null && lessonToReplace.id != lesson.id) {
        await library.remove(lessonToReplace.id);
        _lessonProgress.remove(lessonToReplace.manifest.packageUuid);
        await _progressStore.save(_lessonProgress);
      }
      await _reloadAndOpen(lesson);
      if (mounted) _showStudentNotice('导入完成', '课程已保存到主页。');
    } on IlpException catch (error) {
      if (mounted) await showResult(context, '无法导入', error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _reloadAndOpen(ImportedLesson lesson) async {
    final result = await IlpLibrary(await _libraryDirectory()).load();
    if (!mounted) return;
    setState(() => _lessons = [...result.lessons]);
    await _openLesson(lesson);
  }

  String _nextAvailableTitle(String title) {
    var suffix = 2;
    var candidate = '$title ($suffix)';
    final names = _lessons
        .map((lesson) => lesson.manifest.title.toLowerCase())
        .toSet();
    while (names.contains(candidate.toLowerCase())) {
      suffix += 1;
      candidate = '$title ($suffix)';
    }
    return candidate;
  }

  void _showStudentNotice(
    String title,
    String message, {
    InfoBarSeverity severity = InfoBarSeverity.success,
  }) {
    StackedInfoBarScope.of(context)
        .show(title: title, message: message, severity: severity);
  }

  Future<void> _removeLesson(ImportedLesson lesson) async {
    final confirmed = await confirmPermanentDelete(
      context: context,
      title: '移除课程？',
      message: '将永久删除「${lesson.manifest.title}」及其本地播放记录。',
    );
    if (!confirmed) return;
    setState(() => _deletingLessonId = lesson.id);
    try {
      await IlpLibrary(await _libraryDirectory()).remove(lesson.id);
      _lessonProgress.remove(lesson.manifest.packageUuid);
      await _progressStore.save(_lessonProgress);
      if (!mounted) return;
      setState(() => _lessons.removeWhere((item) => item.id == lesson.id));
      _showStudentNotice('课程已移除', lesson.manifest.title);
    } on FileSystemException catch (error) {
      if (!mounted) return;
      _showStudentNotice(
        '移除失败',
        error.message,
        severity: InfoBarSeverity.error,
      );
    } finally {
      if (mounted) setState(() => _deletingLessonId = null);
    }
  }

  Future<void> _togglePlayback() async {
    if (_lesson == null && _standaloneAudio == null) return;
    final player = await _ensurePlayer();
    if (_playing) {
      await player.pause();
    } else {
      if (_singleSentenceLoop && _activeIndex >= 0) {
        _configureSentenceLoop(_activeIndex);
        final cue = _cues[_activeIndex];
        if (_position < cue.start || _position >= cue.end) {
          await player.seek(cueSeekPosition(cue));
        }
      } else if (_position > Duration.zero &&
          (player.state.position - _position).inMilliseconds.abs() > 800) {
        await player.seek(_position);
      }
      await player.play();
    }
  }

  Future<void> _seek(
    Duration position, {
    int? targetCueIndex,
    bool pinDuringPreroll = false,
  }) async {
    if (_lesson == null && _standaloneAudio == null) return;
    final next = clampDuration(position, _duration);
    final nextCueIndex =
        targetCueIndex ?? activeCueIndexForPosition(_cues, next);
    if (_singleSentenceLoop && nextCueIndex >= 0) {
      _configureSentenceLoop(nextCueIndex);
    } else {
      _sentenceLoopStartAt = null;
      _sentenceStopAt = null;
    }
    setState(() {
      _preRollCueIndex = pinDuringPreroll ? targetCueIndex : null;
      _position = next;
    });
    _positionNotifier.value = next;
    await (await _ensurePlayer()).seek(next);
  }

  Future<void> _seekToCue(int cueIndex, {bool pinDuringPreroll = true}) =>
      _seek(
        cueNavigationPosition(_cues[cueIndex]),
        targetCueIndex: cueIndex,
        pinDuringPreroll: pinDuringPreroll,
      );

  void _configureSentenceLoop(int cueIndex) {
    if (cueIndex < 0 || cueIndex >= _cues.length) {
      _sentenceLoopStartAt = null;
      _sentenceStopAt = null;
      return;
    }
    final cue = _cues[cueIndex];
    _sentenceLoopStartAt = cueSeekPosition(cue);
    _sentenceStopAt = cue.end;
  }

  void _setSingleSentenceLoop(bool enabled) {
    setState(() => _singleSentenceLoop = enabled);
    if (enabled && _activeIndex >= 0) {
      _configureSentenceLoop(_activeIndex);
    } else {
      _sentenceLoopStartAt = null;
      _sentenceStopAt = null;
    }
  }

  Future<void> _stepSentence(int delta) async {
    final activeIndex = _activeIndex;
    if (activeIndex < 0) return;
    final target = (activeIndex + delta).clamp(0, _cues.length - 1);
    await _seekToCue(target, pinDuringPreroll: true);
  }

  int _currentQuestionIndex() {
    final lesson = _lesson;
    if (lesson == null || lesson.manifest.exercises.questions.isEmpty) {
      return -1;
    }
    final exercises = lesson.manifest.exercises;
    final material = exercises.materialForCue(_activeIndex);
    if (material == null) return -1;
    final questions = exercises.questionsForMaterial(material);
    if (questions.isEmpty) return -1;
    final selected = questions
        .where((question) => question.id == _selectedQuestionId)
        .firstOrNull;
    final question = selected ?? questions.first;
    return exercises.questions.indexWhere((item) => item.id == question.id);
  }

  List<String> _currentMaterialQuestions() {
    final lesson = _lesson;
    if (lesson == null) return const ['未设置'];
    final exercises = lesson.manifest.exercises;
    final material = exercises.materialForCue(_activeIndex);
    if (material == null) return const ['未设置'];
    final questions = exercises.questionsForMaterial(material);
    if (questions.isEmpty) return const ['未设置'];
    return questions
        .map(
          (question) =>
              '第 ${question.number} 题  ${question.title.trim().isEmpty ? '未设置' : question.title.trim()}',
        )
        .toList(growable: false);
  }

  Future<void> _stepQuestion(int delta) async {
    final lesson = _lesson;
    if (lesson == null) return;
    final questions = lesson.manifest.exercises.questions;
    if (questions.isEmpty) return;
    final current = _currentQuestionIndex();
    int target;
    if (current >= 0) {
      target = (current + delta).clamp(0, questions.length - 1);
    } else if (delta >= 0) {
      target = questions.indexWhere(
        (question) => question.cueIndexes.first > _activeIndex,
      );
      if (target < 0) target = questions.length - 1;
    } else {
      target = questions.lastIndexWhere(
        (question) => question.cueIndexes.last < _activeIndex,
      );
      if (target < 0) target = 0;
    }
    final targetQuestion = questions[target];
    final targetMaterial = lesson.manifest.exercises.materialForQuestion(
      targetQuestion.id,
    );
    if (targetMaterial == null || targetMaterial.cueIndexes.isEmpty) return;
    final currentMaterial = lesson.manifest.exercises.materialForCue(
      _activeIndex,
    );
    setState(() => _selectedQuestionId = targetQuestion.id);
    if (currentMaterial?.id != targetMaterial.id) {
      await _seekToCue(targetMaterial.cueIndexes.first);
    }
  }

  void _toggleCloze(int cueIndex, int wordIndex) {
    final key = '$cueIndex:$wordIndex';
    setState(() {
      if (!_revealedCloze.add(key)) _revealedCloze.remove(key);
    });
    _scheduleProgressSave();
  }

  void _setAllClozeVisible(bool visible) {
    setState(() => _showAllCloze = visible);
  }

  void _setSubtitlesVisible(bool visible) {
    setState(() => _showSubtitles = visible);
  }

  @override
  Widget build(BuildContext context) {
    final lesson = _lesson;
    final standaloneAudio = _standaloneAudio;
    final hasMedia = lesson != null || standaloneAudio != null;
    return ScaffoldPage(
      header: widget.compactPlayback
          ? null
          : PageHeader(
              leading: hasMedia
                  ? Padding(
                      padding: const EdgeInsets.only(left: 12, right: 8),
                      child: Tooltip(
                        message: '返回主页',
                        child: IconButton(
                          icon: const Icon(FluentIcons.back),
                          onPressed: _returnToStudentHome,
                        ),
                      ),
                    )
                  : null,
              title: const Text('学生端'),
              commandBar: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Button(
                    onPressed: _busy ? null : _openStandaloneAudio,
                    child: const Row(
                      children: [
                        Icon(FluentIcons.music_in_collection),
                        SizedBox(width: 8),
                        Text('打开音频'),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  Button(
                    onPressed: _lessons.isEmpty
                        ? null
                        : () => unawaited(
                            showLessonPicker(
                              context: context,
                              lessons: _lessons,
                              selected: _lesson,
                              onSelected: _openLesson,
                            ),
                          ),
                    child: const Row(
                      children: [
                        Icon(FluentIcons.open_file),
                        SizedBox(width: 8),
                        Text('打开课程'),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  FilledButton(
                    onPressed: _busy ? null : _importPackage,
                    child: Row(
                      children: [
                        if (_busy)
                          const SizedBox(
                            width: 16,
                            height: 16,
                            child: ProgressRing(strokeWidth: 2),
                          )
                        else
                          const Icon(FluentIcons.download),
                        const SizedBox(width: 8),
                        const Text('导入精听包'),
                      ],
                    ),
                  ),
                ],
              ),
            ),
      content: _loading || (widget.compactPlayback && _busy)
          ? const Center(child: ProgressRing())
          : widget.compactPlayback && !hasMedia
          ? const Center(child: Text('未能打开精听包'))
          : !hasMedia
          ? _StudentHome(
              lessons: _lessons,
              progress: _lessonProgress,
              onOpen: _openLesson,
              onRemove: _removeLesson,
              onOpenAudio: _openStandaloneAudio,
              onImport: _importPackage,
              busy: _busy,
              openingLessonId: _openingLessonId,
              deletingLessonId: _deletingLessonId,
            )
          : LayoutBuilder(
              builder: (context, bounds) {
                final player = ValueListenableBuilder<Duration>(
                  valueListenable: _positionNotifier,
                  builder: (context, position, _) => PlayerSection(
                    title:
                        lesson?.manifest.title ??
                        p.basenameWithoutExtension(standaloneAudio!.path),
                    cueCount: _cues.length,
                    hasTranscript: lesson != null,
                    questionTitles: _currentMaterialQuestions(),
                    manifest: lesson?.manifest,
                    sourcePath: lesson == null
                        ? standaloneAudio?.path
                        : p.join(
                            lesson.directoryPath,
                            lesson.manifest.audioPath,
                          ),
                    showSubtitles: _showSubtitles,
                    duration: _duration,
                    position: position,
                    playing: _playing,
                    singleSentenceLoop: _singleSentenceLoop,
                    onTogglePlayback: _togglePlayback,
                    onSeek: _seek,
                    onStepSentence: _stepSentence,
                    onStepQuestion:
                        lesson?.manifest.exercises.questions.isNotEmpty == true
                        ? _stepQuestion
                        : null,
                    onSingleSentenceLoopChanged: _setSingleSentenceLoop,
                    onShowSubtitlesChanged: _setSubtitlesVisible,
                    onShowAllCloze: lesson == null ? null : _setAllClozeVisible,
                    showAllCloze: _showAllCloze,
                  ),
                );
                if (lesson == null) return player;
                final transcript = TranscriptPane(
                  cues: _cues,
                  activeIndex: _activeIndex,
                  exercises: lesson.manifest.exercises,
                  revealedCloze: _revealedCloze,
                  showAllCloze: _showAllCloze,
                  showSubtitles: _showSubtitles,
                  onToggleCloze: _toggleCloze,
                  onShowAllCloze: _setAllClozeVisible,
                  fontSize: widget.settings.transcriptFontSize.toDouble(),
                  questionIndex: _currentQuestionIndex(),
                  onSelected: (index) =>
                      _seekToCue(index, pinDuringPreroll: true),
                );
                if (bounds.maxWidth < 900) {
                  return Column(
                    children: [
                      SizedBox(
                        height: 420,
                        child: RepaintBoundary(child: player),
                      ),
                      const Divider(),
                      Expanded(child: RepaintBoundary(child: transcript)),
                    ],
                  );
                }
                return Row(
                  children: [
                    Expanded(flex: 100, child: RepaintBoundary(child: player)),
                    const Divider(direction: Axis.vertical, size: 1),
                    Expanded(
                      flex: 168,
                      child: RepaintBoundary(child: transcript),
                    ),
                  ],
                );
              },
            ),
    );
  }
}

class TeacherPage extends StatefulWidget {
  const TeacherPage({
    super.key,
    this.onProjectOpened,
    required this.onPackageCreated,
    required this.onTranscriptionEnqueued,
    required this.settings,
    required this.onOpenSettings,
    required this.transcriptionQueue,
    required this.loadSrtRequests,
    required this.externalProjectChanges,
    required this.openProjectRequests,
  });

  final VoidCallback onPackageCreated;
  final VoidCallback? onProjectOpened;
  final VoidCallback onTranscriptionEnqueued;
  final AppSettings settings;
  final VoidCallback onOpenSettings;
  final TranscriptionQueue transcriptionQueue;
  final Stream<TranscriptionJob> loadSrtRequests;
  final Stream<void> externalProjectChanges;
  final Stream<String> openProjectRequests;

  @override
  State<TeacherPage> createState() => _TeacherPageState();
}

List<SrtCue> _prepareProjectCues(CourseProject project) {
  if (!project.hasTranscript) return const [];
  try {
    return SrtParser.parse(
      utf8.encode(project.transcript),
      project.audioDuration ?? const Duration(days: 7),
    );
  } on IlpException {
    return const [];
  }
}

LessonExercises _planProjectQuestions(CourseProject project) {
  final cues = _prepareProjectCues(project);
  return const SrtQuestionPlanner().plan(
    cues,
    clozeWordIndexes: project.exercises.clozeWordIndexes,
  );
}

class _TeacherPageState extends State<TeacherPage> {
  final _store = const CourseProjectStore();
  final _titleController = TextEditingController();
  final _srtController = TextEditingController();
  StreamSubscription<TranscriptionJob>? _loadSrtSubscription;
  StreamSubscription<void>? _externalProjectSubscription;
  StreamSubscription<String>? _openProjectSubscription;
  Timer? _saveTimer;
  var _projects = <CourseProject>[];
  CourseProject? _project;
  var _loading = true;
  String? _busyOperation;
  var _creatingPackage = false;
  var _addingToLibrary = false;
  var _creatingStandalonePlayer = false;
  var _transcriptionSubmitting = false;
  var _projectLoadGeneration = 0;
  var _projectLoading = false;
  List<SrtCue> _preparedReviewCues = const [];
  String? _preparedTranscript;
  TeacherNotice? _notice;
  var _selectedReviewCues = <int>{};
  final _preparingQuestionPlans = <String>{};
  Timer? _queueRefreshTimer;

  Future<T> _withBusy<T>(String message, Future<T> Function() action) async {
    setState(() => _busyOperation = message);
    try {
      await WidgetsBinding.instance.endOfFrame;
      return await action();
    } finally {
      if (mounted) setState(() => _busyOperation = null);
    }
  }

  @override
  void initState() {
    super.initState();
    widget.transcriptionQueue.addListener(_onQueueChanged);
    _loadSrtSubscription = widget.loadSrtRequests.listen(_loadJobTranscript);
    _externalProjectSubscription = widget.externalProjectChanges.listen(
      (_) => unawaited(_reloadProjectsFromPrivateApi()),
    );
    _openProjectSubscription = widget.openProjectRequests.listen(
      (projectId) => unawaited(_openProjectFromPrivateApi(projectId)),
    );
    unawaited(_loadProjects());
  }

  @override
  void dispose() {
    _saveTimer?.cancel();
    _queueRefreshTimer?.cancel();
    widget.transcriptionQueue.removeListener(_onQueueChanged);
    _loadSrtSubscription?.cancel();
    _externalProjectSubscription?.cancel();
    _openProjectSubscription?.cancel();
    _titleController.dispose();
    _srtController.dispose();
    super.dispose();
  }

  Future<void> _loadProjects() async {
    final projects = await _store.loadAll();
    if (!mounted) return;
    setState(() {
      _projects = projects;
      _loading = false;
    });
  }

  Future<void> _reloadProjectsFromPrivateApi() async {
    final selectedId = _project?.id;
    final previousUpdatedAt = _project?.updatedAt;
    final projects = await _store.loadAll();
    if (!mounted) return;
    final selected = selectedId == null
        ? null
        : projects.where((project) => project.id == selectedId).firstOrNull;
    setState(() {
      _projects = projects;
      if (selectedId != null && selected == null) _project = null;
    });
    if (selected != null && selected.updatedAt != previousUpdatedAt) {
      _openProject(selected);
    }
    _showTeacherNotice('AI 操作已同步', '课程项目已更新。');
  }

  Future<void> _openProjectFromPrivateApi(String projectId) async {
    final projects = await _store.loadAll();
    if (!mounted) return;
    setState(() => _projects = projects);
    final project = projects.where((item) => item.id == projectId).firstOrNull;
    if (project != null) _openProject(project);
  }

  void _openProject(CourseProject project) {
    unawaited(_openProjectAsync(project));
  }

  Future<void> _openProjectAsync(CourseProject project) async {
    widget.onProjectOpened?.call();
    _saveTimer?.cancel();
    final generation = ++_projectLoadGeneration;
    setState(() {
      _project = project;
      _projectLoading = true;
      _notice = null;
      _selectedReviewCues = {};
    });
    _titleController.text = project.title;
    _srtController.text = project.transcript;
    try {
      await WidgetsBinding.instance.endOfFrame;
      final cues = await compute(_prepareProjectCues, project);
      if (!mounted || generation != _projectLoadGeneration) return;
      setState(() {
        _preparedReviewCues = cues;
        _preparedTranscript = project.transcript;
        _projectLoading = false;
      });
      if (project.hasTranscript &&
          !project.automaticQuestionPlanApplied &&
          !project.autoQuestionPlanDeferred) {
        unawaited(_ensureAutomaticQuestionPlan(project));
      }
    } catch (error) {
      if (!mounted || generation != _projectLoadGeneration) return;
      setState(() => _projectLoading = false);
      _showTeacherNotice('项目加载失败', '$error');
    }
  }

  Future<void> _ensureAutomaticQuestionPlan(CourseProject project) async {
    if (!project.hasTranscript ||
        project.automaticQuestionPlanApplied ||
        project.autoQuestionPlanDeferred) {
      return;
    }
    if (!_preparingQuestionPlans.add(project.id)) return;
    try {
      var exercises = project.exercises;
      var createdAutomatically = false;
      if (exercises.questions.isEmpty) {
        try {
          exercises = await compute(_planProjectQuestions, project);
          createdAutomatically = exercises.questions.isNotEmpty;
        } on IlpException {
          return;
        }
      }
      final latest = await _store.loadById(project.id);
      if (latest == null ||
          latest.transcript != project.transcript ||
          latest.automaticQuestionPlanApplied ||
          latest.autoQuestionPlanDeferred ||
          latest.updatedAt != project.updatedAt) {
        return;
      }
      final updated = latest.copyWith(
        exercises: exercises,
        automaticQuestionPlanApplied: true,
      );
      await _store.save(updated);
      if (!mounted) return;
      final selected = _project?.id == project.id;
      _replaceProject(updated, select: selected);
      if (selected && createdAutomatically) {
        final repeatedCount = exercises.effectiveMaterials
            .where((material) => material.repeatedCueIndexes.isNotEmpty)
            .length;
        _showTeacherNotice(
          '题目已自动整理',
          '已识别 ${exercises.effectiveMaterials.length} 段材料、${exercises.questions.length} 道题和 $repeatedCount 段重复朗读。',
        );
      }
    } finally {
      _preparingQuestionPlans.remove(project.id);
    }
  }

  Future<void> _closeProject() async {
    _saveTimer?.cancel();
    ++_projectLoadGeneration;
    if (_projectLoading) {
      setState(() {
        _project = null;
        _projectLoading = false;
        _preparedReviewCues = const [];
        _preparedTranscript = null;
        _selectedReviewCues = {};
      });
      _titleController.clear();
      _srtController.clear();
      return;
    }
    final project = _project;
    if (project == null) return;
    final transcript = _srtController.text;
    final updated = project.copyWith(
      title: _titleController.text.trim().isEmpty
          ? '未命名项目'
          : _titleController.text.trim(),
      transcript: transcript,
    );
    setState(() {
      _project = null;
      _preparedReviewCues = const [];
      _preparedTranscript = null;
      _selectedReviewCues = {};
    });
    _titleController.clear();
    _srtController.clear();
    await _store.save(updated);
    if (!mounted) return;
    setState(() {
      _projects = [
        updated,
        for (final item in _projects)
          if (item.id != updated.id) item,
      ];
    });
  }

  Future<void> _deleteProject(CourseProject project) async {
    final confirmed = await confirmPermanentDelete(
      context: context,
      title: '删除课程项目？',
      message: '将永久删除「${project.title}」的音频、字幕和制作记录。',
    );
    if (!confirmed) return;
    await _withBusy('正在删除项目…', () async {
      final jobId = project.transcriptionJobId;
      if (jobId != null) widget.transcriptionQueue.delete(jobId);
      await _store.delete(project.id);
      if (!mounted) return;
      setState(() {
        _projects.removeWhere((item) => item.id == project.id);
        if (_project?.id == project.id) _project = null;
      });
      _showTeacherNotice('项目已删除', project.title);
    });
  }

  void _showTeacherNotice(String title, String message) {
    StackedInfoBarScope.of(context).show(title: title, message: message);
  }

  Future<void> _createProject({required bool selectAudio}) async {
    File? audio;
    if (selectAudio) {
      final selected = await pickFileWith(
        allowedExtensions: IlpManifest.supportedAudioExtensions.toList(),
        dialogTitle: '为新项目选择音频',
      );
      if (selected == null) return;
      audio = File(selected);
    }
    await _withBusy(audio == null ? '正在创建项目…' : '正在复制音频并分析时长…', () async {
      var project = await _store.create(audio: audio);
      if (project.audioPath != null) {
        final duration = await (debugAudioDurationProbe ?? probeAudioDuration)(
          project.audioPath!,
        );
        project = project.copyWith(audioDuration: duration);
        await _store.save(project);
      }
      if (!mounted) return;
      setState(() => _projects = [project, ..._projects]);
      _openProject(project);
      _showTeacherNotice(
        '项目已创建',
        audio == null ? '可以开始选择课程音频。' : '音频已复制到项目目录。',
      );
    });
  }

  Future<void> _showCreateProjectDialog() async {
    await showSpringDialog<void>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('新建项目'),
        content: const Text('可以先创建空白项目，也可以选择音频并直接进入转写步骤。'),
        actions: [
          Button(
            onPressed: () {
              Navigator.pop(dialogContext);
              unawaited(_createProject(selectAudio: false));
            },
            child: const Text('空白项目'),
          ),
          FilledButton(
            onPressed: () {
              Navigator.pop(dialogContext);
              unawaited(_createProject(selectAudio: true));
            },
            child: const Text('选择音频'),
          ),
        ],
      ),
    );
  }

  Future<void> _importProjectArchive() async {
    final selected = await pickFileWith(
      allowedExtensions: const ['zip'],
      dialogTitle: '导入制作工程',
    );
    if (selected == null) return;
    await _withBusy('正在解压并导入工程…', () async {
      try {
        final project = await _store.importZip(File(selected));
        final projects = await _store.loadAll();
        if (!mounted) return;
        setState(() => _projects = projects);
        _openProject(project);
        _showTeacherNotice('工程已导入', '音频、字幕和全部制作数据已恢复。');
      } on CourseProjectArchiveException catch (error) {
        if (!mounted) return;
        setState(() => _notice = TeacherNotice.error('无法导入工程', error.message));
      }
    });
  }

  Future<void> _exportProjectArchive() async {
    final current = _project;
    if (current == null) return;
    final project = current.copyWith(
      title: _titleController.text.trim().isEmpty
          ? '未命名项目'
          : _titleController.text.trim(),
      transcript: _srtController.text,
    );
    await _store.save(project);
    await _withBusy('正在打包并导出工程…', () async {
      try {
        final output = await FilePicker.saveFile(
          dialogTitle: '导出制作工程',
          fileName: '${safeFileName(project.title)}-工程.zip',
          bytes: Uint8List(0),
          type: FileType.custom,
          allowedExtensions: const ['zip'],
        );
        if (output == null || !mounted) return;
        await _store.exportZipToFile(project, File(output.toFilePath()));
        _replaceProject(project, select: true);
        _showTeacherNotice('工程已导出', 'ZIP 包含音频、字幕和全部制作数据。');
      } on CourseProjectArchiveException catch (error) {
        if (!mounted) return;
        setState(() => _notice = TeacherNotice.error('无法导出工程', error.message));
      }
    });
  }

  Future<void> _importExamDocument() async {
    final project = _project;
    if (project == null) return;
    final selected = await pickFileWith(
      allowedExtensions: const ['docx'],
      dialogTitle: '导入 DOCX 试卷',
    );
    if (selected == null) return;
    await _withBusy('正在解析 DOCX 试卷…', () async {
      try {
        final document = await _store.importExamDocument(
          project,
          File(selected),
        );
        if (!mounted) return;
        _replaceProject(document.project, select: true);
        _showTeacherNotice(
          '试卷已导入',
          '已提取 ${document.paragraphCount} 个段落和 ${document.tableCount} 个表格。',
        );
      } on CourseProjectDocumentException catch (error) {
        if (!mounted) return;
        setState(() => _notice = TeacherNotice.error('无法读取试卷', error.message));
      }
    });
  }

  Future<void> _exportExamText() async {
    final project = _project;
    if (project == null) return;
    await _withBusy('正在导出试卷文本…', () async {
      final document = await _store.readExamDocument(project);
      if (document == null) {
        if (mounted) {
          setState(
            () => _notice = TeacherNotice.error('尚未导入试卷', '请先选择 DOCX 试卷。'),
          );
        }
        return;
      }
      final output = await FilePicker.saveFile(
        dialogTitle: '导出试卷文本',
        fileName: '${safeFileName(project.title)}-试卷.txt',
        bytes: Uint8List.fromList(utf8.encode(document.text)),
        type: FileType.custom,
        allowedExtensions: const ['txt'],
      );
      if (output != null && mounted) {
        _showTeacherNotice('试卷文本已导出', '已保存为 UTF-8 TXT。');
      }
    });
  }

  Future<void> _bindAudio() async {
    final project = _project;
    if (project == null) return;
    final selected = await pickFileWith(
      allowedExtensions: IlpManifest.supportedAudioExtensions.toList(),
      dialogTitle: '选择课程音频',
    );
    if (selected == null) return;
    await _withBusy('正在绑定音频并分析时长…', () async {
      final source = File(selected);
      final duration = await (debugAudioDurationProbe ?? probeAudioDuration)(
        source.path,
      );
      final updated = await _store.bindAudio(
        project,
        source,
        duration: duration,
      );
      if (!mounted) return;
      _replaceProject(updated, select: true);
      _showTeacherNotice('音频已更新', p.basename(updated.audioPath!));
    });
  }

  Future<void> _importSrt() async {
    final project = _project;
    if (project == null || !project.hasAudio) return;
    final selected = await pickFileWith(
      allowedExtensions: const ['srt'],
      dialogTitle: '选择 SRT 字幕',
    );
    if (selected == null) return;
    final transcript = await File(selected).readAsString();
    late final List<SrtCue> importedCues;
    try {
      importedCues = SrtParser.parse(
        utf8.encode(transcript),
        project.audioDuration ?? const Duration(days: 7),
      );
    } on IlpException catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导入字幕', error.message));
      }
      return;
    }
    List<SrtCue>? existingCues;
    try {
      existingCues = project.hasTranscript
          ? SrtParser.parse(
              utf8.encode(project.transcript),
              project.audioDuration ?? const Duration(days: 7),
            )
          : null;
    } on IlpException {
      existingCues = null;
    }
    final preservesExercises =
        existingCues != null &&
        existingCues.length == importedCues.length &&
        List.generate(
          importedCues.length,
          (index) =>
              existingCues![index].start == importedCues[index].start &&
              existingCues[index].end == importedCues[index].end,
        ).every((matches) => matches);
    _retireProjectTranscription(project);
    final updated = project.copyWith(
      transcript: transcript,
      transcriptionJobId: null,
      step: CourseProjectStep.review,
      reviewPhase: ReviewPhase.grouping,
      exercises: preservesExercises
          ? project.exercises
          : const LessonExercises(),
      automaticQuestionPlanApplied: preservesExercises,
      autoQuestionPlanDeferred: false,
    );
    await _store.save(updated);
    if (!mounted) return;
    _replaceProject(updated, select: true);
    _srtController.text = transcript;
    if (!preservesExercises) unawaited(_ensureAutomaticQuestionPlan(updated));
    _showTeacherNotice('字幕已导入', '现在可以进行题目与挖空审阅。');
  }

  void _retireProjectTranscription(CourseProject project) {
    final jobId = project.transcriptionJobId;
    if (jobId == null) return;
    final job = widget.transcriptionQueue.jobById(jobId);
    if (job == null) return;
    if (job.isActive || job.status == TranscriptionJobStatus.interrupted) {
      widget.transcriptionQueue.cancel(jobId);
    }
    widget.transcriptionQueue.markSrtConsumed(jobId);
  }

  Future<void> _startTranscription() async {
    final project = _project;
    if (project == null || !project.hasAudio || _transcriptionSubmitting) {
      return;
    }
    if (!widget.settings.cloudReady) {
      widget.onOpenSettings();
      return;
    }
    setState(() => _transcriptionSubmitting = true);
    String? jobId;
    try {
      jobId = widget.transcriptionQueue.enqueue(
        projectId: project.id,
        title: project.title,
        audio: File(project.audioPath!),
        audioDuration: project.audioDuration,
      );
      final updated = project.copyWith(
        step: CourseProjectStep.transcription,
        transcriptionJobId: jobId,
        autoQuestionPlanDeferred: false,
      );
      await _store.save(updated);
      if (!mounted) return;
      _replaceProject(updated, select: true);
      widget.onTranscriptionEnqueued();
      _showTeacherNotice('已加入转写队列', '可以继续使用应用，并从任务中心查看进度。');
    } catch (error) {
      if (jobId != null) widget.transcriptionQueue.cancel(jobId);
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法开始转写', '$error'));
      }
    } finally {
      if (mounted) setState(() => _transcriptionSubmitting = false);
    }
  }

  void _onQueueChanged() {
    if (!mounted) return;
    // Throttle UI refreshes (progress bar updates) to avoid rebuilding the
    // entire teacher workspace on every queue notification.
    if (_queueRefreshTimer?.isActive ?? false) return;
    _queueRefreshTimer = Timer(const Duration(milliseconds: 500), () {
      if (mounted) setState(() {});
    });
  }

  void _loadJobTranscript(TranscriptionJob job) {
    final projectId = job.projectId;
    if (projectId == null) return;
    final match = _projects.where((project) => project.id == projectId);
    if (match.isNotEmpty) _openProject(match.first);
  }

  void _scheduleDraftSave() {
    _saveTimer?.cancel();
    _saveTimer = Timer(const Duration(milliseconds: 350), () async {
      final project = _project;
      if (project == null) return;
      final transcript = _srtController.text;
      final step = project.step == CourseProjectStep.completed
          ? CourseProjectStep.completed
          : transcript.trim().isNotEmpty
          ? CourseProjectStep.review
          : project.hasAudio
          ? CourseProjectStep.transcription
          : CourseProjectStep.audio;
      final updated = project.copyWith(
        title: _titleController.text.trim().isEmpty
            ? '未命名项目'
            : _titleController.text.trim(),
        transcript: transcript,
        step: step,
      );
      await _store.save(updated);
      if (mounted) _replaceProject(updated, select: true);
    });
  }

  Future<void> _finishReview() async {
    final project = _project;
    if (project == null || _srtController.text.trim().isEmpty) return;
    final updated = project.copyWith(
      transcript: _srtController.text,
      step: CourseProjectStep.completed,
    );
    await _store.save(updated);
    if (mounted) {
      _replaceProject(updated, select: true);
      _showTeacherNotice('审阅完成', '现在可以加入播放或导出课程。');
    }
  }

  List<SrtCue> _reviewCues(CourseProject project) {
    if (_preparedTranscript == _srtController.text) {
      return _preparedReviewCues;
    }
    try {
      final cues = SrtParser.parse(
        utf8.encode(_srtController.text),
        project.audioDuration ?? const Duration(days: 7),
      );
      _preparedTranscript = _srtController.text;
      _preparedReviewCues = cues;
      return cues;
    } on IlpException {
      return const [];
    }
  }

  Future<void> _saveExercises(
    LessonExercises exercises, {
    ReviewPhase? phase,
  }) async {
    final project = _project;
    if (project == null) return;
    final updated = project.copyWith(
      exercises: exercises,
      reviewPhase: phase,
      transcript: _srtController.text,
      automaticQuestionPlanApplied: true,
    );
    await _store.save(updated);
    if (mounted) _replaceProject(updated, select: true);
  }

  Future<void> _createQuestion() async {
    final project = _project;
    if (project == null || _selectedReviewCues.isEmpty) return;
    final assigned = {
      for (final material in project.exercises.effectiveMaterials)
        ...material.cueIndexes,
    };
    final indexes =
        _selectedReviewCues.where((index) => !assigned.contains(index)).toList()
          ..sort();
    if (indexes.isEmpty) return;
    final stamp = '${DateTime.now().microsecondsSinceEpoch}';
    final number = project.exercises.questions.isEmpty
        ? 1
        : project.exercises.questions
                  .map((question) => question.number)
                  .reduce((left, right) => left > right ? left : right) +
              1;
    final question = LessonQuestion(
      id: 'question-$stamp',
      title: '第 $number 题',
      number: number,
      materialId: 'material-$stamp',
      cueIndexes: indexes,
    );
    final questions = [...project.exercises.questions, question];
    final materials =
        [
          ...project.exercises.effectiveMaterials,
          LessonMaterial(
            id: question.materialId,
            prompt: '',
            cueIndexes: indexes,
            questionIds: [question.id],
          ),
        ]..sort(
          (left, right) =>
              left.cueIndexes.first.compareTo(right.cueIndexes.first),
        );
    setState(() => _selectedReviewCues = {});
    await _saveExercises(
      LessonExercises(
        materials: materials,
        questions: questions,
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
    );
    if (mounted) _showTeacherNotice('题目已创建', '已加入 ${indexes.length} 个句子。');
  }

  Future<void> _removeQuestion(String id) async {
    final project = _project;
    if (project == null) return;
    final sourceMaterial = project.exercises.materialForQuestion(id);
    final releasesMaterial = sourceMaterial?.questionIds.length == 1;
    final materials = <LessonMaterial>[];
    for (final material in project.exercises.effectiveMaterials) {
      final questionIds = material.questionIds
          .where((questionId) => questionId != id)
          .toList(growable: false);
      if (questionIds.isEmpty) continue;
      materials.add(
        LessonMaterial(
          id: material.id,
          prompt: material.prompt,
          cueIndexes: material.cueIndexes,
          repeatedCueIndexes: material.repeatedCueIndexes,
          questionIds: questionIds,
        ),
      );
    }
    await _saveExercises(
      LessonExercises(
        materials: materials,
        questions: project.exercises.questions
            .where((question) => question.id != id)
            .toList(growable: false),
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
    );
    if (mounted) {
      _showTeacherNotice(
        '题目已删除',
        releasesMaterial ? '该材料已移除，相关句子恢复为题前提示。' : '听力材料中的其他小题已保留。',
      );
    }
  }

  Future<void> _addQuestionToMaterial(String materialId) async {
    final project = _project;
    if (project == null) return;
    final material = project.exercises.effectiveMaterials
        .where((item) => item.id == materialId)
        .firstOrNull;
    if (material == null) return;
    final number = project.exercises.questions.isEmpty
        ? 1
        : project.exercises.questions
                  .map((question) => question.number)
                  .reduce((left, right) => left > right ? left : right) +
              1;
    final id = 'question-${DateTime.now().microsecondsSinceEpoch}';
    final questions = [
      ...project.exercises.questions,
      LessonQuestion(
        id: id,
        title: '第 $number 题',
        cueIndexes: material.cueIndexes,
        repeatedCueIndexes: material.repeatedCueIndexes,
        materialId: material.id,
        number: number,
      ),
    ];
    final materials = [
      for (final item in project.exercises.effectiveMaterials)
        if (item.id == materialId)
          LessonMaterial(
            id: item.id,
            prompt: item.prompt,
            cueIndexes: item.cueIndexes,
            repeatedCueIndexes: item.repeatedCueIndexes,
            questionIds: [...item.questionIds, id],
          )
        else
          item,
    ];
    await _saveExercises(
      LessonExercises(
        materials: materials,
        questions: questions,
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
    );
    if (mounted) _showTeacherNotice('小题已添加', '已加入第 $number 题。');
  }

  Future<void> _renameQuestion(String id, String title) async {
    final project = _project;
    if (project == null) return;
    final trimmed = title.trim();
    await _saveExercises(
      LessonExercises(
        materials: project.exercises.effectiveMaterials,
        questions: [
          for (final question in project.exercises.questions)
            if (question.id == id)
              LessonQuestion(
                id: question.id,
                title: trimmed.isEmpty ? '未命名题目' : trimmed,
                cueIndexes: question.cueIndexes,
                repeatedCueIndexes: question.repeatedCueIndexes,
                materialId: question.materialId,
                number: question.number,
              )
            else
              question,
        ],
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
    );
  }

  Future<void> _moveQuestion(String id, int targetNumber) async {
    final project = _project;
    if (project == null || targetNumber < 1) return;
    final current = project.exercises.questions
        .where((question) => question.id == id)
        .firstOrNull;
    if (current == null || current.number == targetNumber) return;
    final oldNumber = current.number;
    final questions = [
      for (final question in project.exercises.questions)
        LessonQuestion(
          id: question.id,
          title: question.title,
          cueIndexes: question.cueIndexes,
          repeatedCueIndexes: question.repeatedCueIndexes,
          materialId: question.materialId,
          number: question.id == id
              ? targetNumber
              : question.number == targetNumber
              ? oldNumber
              : question.number,
        ),
    ]..sort((left, right) => left.number.compareTo(right.number));
    await _saveExercises(
      LessonExercises(
        materials: project.exercises.effectiveMaterials,
        questions: questions,
        clozeWordIndexes: project.exercises.clozeWordIndexes,
      ),
    );
  }

  Future<void> _setReviewPhase(ReviewPhase phase) async {
    final project = _project;
    if (project == null) return;
    await _saveExercises(project.exercises, phase: phase);
  }

  Future<void> _toggleReviewCloze(int cueIndex, int wordIndex) async {
    final project = _project;
    if (project == null) return;
    final cloze = toggleSynchronizedClozeWord(
      current: project.exercises.clozeWordIndexes,
      cues: _reviewCues(project),
      exercises: project.exercises,
      cueIndex: cueIndex,
      wordIndex: wordIndex,
    );
    await _saveExercises(
      LessonExercises(
        materials: project.exercises.effectiveMaterials,
        questions: project.exercises.questions,
        clozeWordIndexes: cloze,
      ),
    );
  }

  void _updateCueText(CourseProject project, int cueIndex, String text) {
    final cues = [..._reviewCues(project)];
    if (cueIndex < 0 || cueIndex >= cues.length) return;
    _retireProjectTranscription(project);
    if (project.transcriptionJobId != null) {
      _replaceProject(project.copyWith(transcriptionJobId: null), select: true);
    }
    cues[cueIndex] = SrtCue(
      start: cues[cueIndex].start,
      end: cues[cueIndex].end,
      text: text,
    );
    _srtController.text = serializeSrt(cues);
    _scheduleDraftSave();
  }

  Future<CourseProject?> _deliveryProject() async {
    final project = _project;
    if (project == null) return null;
    final title = _titleController.text.trim();
    final updated = project.copyWith(
      title: title.isEmpty ? '未命名项目' : title,
      transcript: _srtController.text,
      step: CourseProjectStep.completed,
    );
    if (!updated.hasAudio || !updated.hasTranscript) return null;
    await _store.save(updated);
    if (mounted) _replaceProject(updated, select: true);
    return updated;
  }

  Future<void> _addToPlayback() async {
    final project = await _deliveryProject();
    if (project == null || !mounted) return;
    setState(() {
      _addingToLibrary = true;
      _busyOperation = '正在添加到播放…';
    });
    try {
      await WidgetsBinding.instance.endOfFrame;
      final result = await const ProjectDelivery().addToLibrary(
        project,
        await resolveLessonLibraryDirectory(),
      );
      final updated = project.copyWith(
        packageVersion: result.packageVersion,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      if (!mounted) return;
      _replaceProject(updated, select: true);
      widget.onPackageCreated();
      _showTeacherNotice('已添加到播放', '版本 ${updated.packageVersion} 已加入学生端课程列表。');
    } on IlpException catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法添加课程', error.message));
      }
    } catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法添加课程', '$error'));
      }
    } finally {
      if (mounted) {
        setState(() {
          _addingToLibrary = false;
          _busyOperation = null;
        });
      }
    }
  }

  Future<void> _exportPackage() async {
    final project = await _deliveryProject();
    if (project == null || !mounted) return;
    setState(() => _creatingPackage = true);
    try {
      final savedPath = await FilePicker.saveFile(
        dialogTitle: '导出精听包',
        fileName: '${safeFileName(project.title)}.ilp',
        bytes: Uint8List(0),
        type: FileType.custom,
        allowedExtensions: const ['ilp'],
      );
      if (savedPath == null) return;
      setState(() => _busyOperation = '正在生成精听包…');
      await WidgetsBinding.instance.endOfFrame;
      final packageVersion = project.packageVersion + 1;
      final package = await const ProjectDelivery().createIlp(
        project,
        File(savedPath.toFilePath()),
        packageVersion: packageVersion,
      );
      final updated = project.copyWith(
        lastExportPath: package.path,
        packageVersion: packageVersion,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      if (!mounted) return;
      _replaceProject(updated, select: true);
      widget.onPackageCreated();
      _showTeacherNotice(
        '导出完成',
        '版本 ${updated.packageVersion} 已保存，可以继续编辑后再次导出。',
      );
    } on IlpException catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导出', error.message));
      }
    } catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导出', '$error'));
      }
    } finally {
      if (mounted) {
        setState(() {
          _creatingPackage = false;
          _busyOperation = null;
        });
      }
    }
  }

  Future<void> _exportStandalonePlayer() async {
    final project = await _deliveryProject();
    if (project == null || !mounted) return;
    if (!Platform.isWindows) {
      setState(
        () => _notice = TeacherNotice.error('无法导出', '独立播放器需要在 Windows 版本中生成。'),
      );
      return;
    }
    setState(() => _creatingStandalonePlayer = true);
    Directory? temporaryDirectory;
    try {
      final savedPath = await FilePicker.saveFile(
        dialogTitle: '导出独立精听包',
        fileName: '${safeFileName(project.title)}.exe',
        bytes: Uint8List(0),
        type: FileType.custom,
        allowedExtensions: const ['exe'],
      );
      if (savedPath == null) return;
      setState(() => _busyOperation = '正在生成独立精听包…');
      await WidgetsBinding.instance.endOfFrame;
      temporaryDirectory = await Directory.systemTemp.createTemp(
        'intensive-listening-standalone-export-',
      );
      final packageVersion = project.packageVersion + 1;
      final ilp = await const ProjectDelivery().createIlp(
        project,
        File(p.join(temporaryDirectory.path, 'lesson.ilp')),
        packageVersion: packageVersion,
      );
      final output = await const StandaloneLessonExporter().create(
        ilpFile: ilp,
        outputFile: File(savedPath.toFilePath()),
        packageUuid: project.packageUuid,
        packageVersion: packageVersion,
        title: project.title,
      );
      final updated = project.copyWith(
        lastExportPath: output.path,
        packageVersion: packageVersion,
        step: CourseProjectStep.completed,
      );
      await _store.save(updated);
      if (!mounted) return;
      _replaceProject(updated, select: true);
      _showTeacherNotice(
        '独立精听包已导出',
        '版本 $packageVersion 已写入 ${p.basename(output.path)}。',
      );
    } on IlpException catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导出', error.message));
      }
    } on StandaloneLessonExportException catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导出', error.message));
      }
    } catch (error) {
      if (mounted) {
        setState(() => _notice = TeacherNotice.error('无法导出', '$error'));
      }
    } finally {
      if (temporaryDirectory != null && await temporaryDirectory.exists()) {
        await temporaryDirectory.delete(recursive: true);
      }
      if (mounted) {
        setState(() {
          _creatingStandalonePlayer = false;
          _busyOperation = null;
        });
      }
    }
  }

  void _replaceProject(CourseProject updated, {required bool select}) {
    setState(() {
      _projects = [
        updated,
        for (final project in _projects)
          if (project.id != updated.id) project,
      ];
      if (select) _project = updated;
    });
  }

  TranscriptionJob? _jobFor(CourseProject project) {
    final id = project.transcriptionJobId;
    return id == null ? null : widget.transcriptionQueue.jobById(id);
  }

  String _projectStatus(CourseProject project) {
    final job = _jobFor(project);
    if (job?.status == TranscriptionJobStatus.running ||
        job?.status == TranscriptionJobStatus.queued) {
      return '正在转写 ${_percentage(job!)}%';
    }
    if (job?.status == TranscriptionJobStatus.interrupted) return '转写已中断';
    if (job?.status == TranscriptionJobStatus.failed) return '转写失败';
    return switch (project.step) {
      CourseProjectStep.audio => '等待音频',
      CourseProjectStep.transcription => '等待转写',
      CourseProjectStep.review => '待审阅',
      CourseProjectStep.completed => '已完成',
    };
  }

  int _percentage(TranscriptionJob job) =>
      ((job.fraction ?? 0) * 100).clamp(0, 100).round();

  @override
  Widget build(BuildContext context) {
    final compactCommands = MediaQuery.sizeOf(context).width < 1220;
    return ScaffoldPage(
      header: PageHeader(
        leading: _project == null
            ? null
            : Padding(
                padding: const EdgeInsets.only(left: 12, right: 8),
                child: Tooltip(
                  message: '返回项目列表',
                  child: IconButton(
                    icon: const Icon(FluentIcons.back),
                    onPressed: _closeProject,
                  ),
                ),
              ),
        title: const Text('课程项目'),
        commandBar: compactCommands
            ? Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  DropDownButton(
                    title: const Text('工程操作'),
                    items: [
                      for (final project in _projects)
                        MenuFlyoutItem(
                          text: Text('打开：${project.title}'),
                          onPressed: () => _openProject(project),
                        ),
                      MenuFlyoutItem(
                        text: const Text('导入工程'),
                        onPressed: _importProjectArchive,
                      ),
                      MenuFlyoutItem(
                        text: const Text('导出当前工程'),
                        onPressed: _project == null
                            ? null
                            : _exportProjectArchive,
                      ),
                      MenuFlyoutItem(
                        text: const Text('导入 DOCX 试卷'),
                        onPressed: _project == null
                            ? null
                            : _importExamDocument,
                      ),
                      MenuFlyoutItem(
                        text: const Text('导出试卷文本'),
                        onPressed: _project == null ? null : _exportExamText,
                      ),
                    ],
                  ),
                  const SizedBox(width: 8),
                  FilledButton(
                    onPressed: _showCreateProjectDialog,
                    child: const Text('新建项目'),
                  ),
                  const SizedBox(width: 8),
                  IconButton(
                    icon: const Icon(FluentIcons.settings),
                    onPressed: widget.onOpenSettings,
                  ),
                ],
              )
            : Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (_projects.isNotEmpty)
                    DropDownButton(
                      title: const Text('选择工程'),
                      items: [
                        for (final project in _projects)
                          MenuFlyoutItem(
                            text: Text(project.title),
                            onPressed: () => _openProject(project),
                          ),
                      ],
                    ),
                  if (_projects.isNotEmpty) const SizedBox(width: 8),
                  Button(
                    onPressed: _importProjectArchive,
                    child: const Text('导入工程'),
                  ),
                  const SizedBox(width: 8),
                  Button(
                    onPressed: _project == null ? null : _exportProjectArchive,
                    child: const Text('导出工程'),
                  ),
                  const SizedBox(width: 8),
                  if (_project != null) ...[
                    DropDownButton(
                      title: const Text('试卷'),
                      items: [
                        MenuFlyoutItem(
                          text: const Text('导入 DOCX'),
                          onPressed: _importExamDocument,
                        ),
                        MenuFlyoutItem(
                          text: const Text('导出 TXT'),
                          onPressed: _exportExamText,
                        ),
                      ],
                    ),
                    const SizedBox(width: 8),
                  ],
                  FilledButton(
                    onPressed: _showCreateProjectDialog,
                    child: const Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(FluentIcons.add),
                        SizedBox(width: 8),
                        Text('新建项目'),
                      ],
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton(
                    icon: const Icon(FluentIcons.settings),
                    onPressed: widget.onOpenSettings,
                  ),
                ],
              ),
      ),
      content: Stack(
        children: [
          _loading
              ? const Center(child: ProgressRing())
              : Row(
                  children: [
                    SizedBox(
                      width: compactCommands ? 240 : 300,
                      child: _ProjectList(
                        projects: _projects,
                        selected: _project,
                        statusFor: _projectStatus,
                        jobFor: _jobFor,
                        onSelected: _openProject,
                        onRemove: _deleteProject,
                      ),
                    ),
                    const Divider(
                      key: teacherWorkspaceDividerKey,
                      direction: Axis.vertical,
                    ),
                    Expanded(
                      child: _SpringReveal(
                        transitionKey: _projectLoading
                            ? 'loading-${_project?.id}'
                            : '${_project?.id}-${_project?.step.name}',
                        child: _projectLoading
                            ? const Center(
                                child: Column(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    ProgressRing(),
                                    SizedBox(height: 12),
                                    Text('正在打开课程项目…'),
                                  ],
                                ),
                              )
                            : _project == null
                            ? const _NoProject()
                            : _buildWorkspace(context, _project!),
                      ),
                    ),
                  ],
                ),
          Positioned.fill(
            child: AnimatedSwitcher(
              duration: const Duration(milliseconds: 220),
              child: _busyOperation == null
                  ? const SizedBox.shrink()
                  : ColoredBox(
                      key: ValueKey(_busyOperation),
                      color: FluentTheme.of(context).micaBackgroundColor
                          .withValues(alpha: 0.65),
                      child: Center(
                        child: Card(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 24,
                            vertical: 16,
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const SizedBox(
                                width: 20,
                                height: 20,
                                child: ProgressRing(strokeWidth: 2.5),
                              ),
                              const SizedBox(width: 14),
                              Text(
                                _busyOperation!,
                                style: const TextStyle(
                                  fontWeight: FontWeight.w600,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildWorkspace(BuildContext context, CourseProject project) {
    final job = _jobFor(project);
    final isTranscriptEditor =
        project.hasTranscript &&
        (project.step == CourseProjectStep.review ||
            project.step == CourseProjectStep.completed);
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 4, 24, 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          AnimatedInlineInfoBar(
            noticeId: _notice,
            title: _notice?.title,
            message: _notice?.message,
            severity: _notice?.severity,
            onClose: () => setState(() => _notice = null),
          ),
          Align(
            alignment: Alignment.topLeft,
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 760),
              child: SizedBox(
                width: double.infinity,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    TextBox(
                      controller: _titleController,
                      style: FluentTheme.of(context).typography.subtitle,
                      placeholder: '项目名称',
                      onChanged: (_) => _scheduleDraftSave(),
                    ),
                    const SizedBox(height: 16),
                    _ProjectSteps(step: project.step),
                  ],
                ),
              ),
            ),
          ),
          const SizedBox(height: 20),
          Expanded(
            child: isTranscriptEditor
                ? _buildStep(context, project, job)
                : Align(
                    alignment: Alignment.topLeft,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 760),
                      child: SizedBox(
                        width: double.infinity,
                        child: _buildStep(context, project, job),
                      ),
                    ),
                  ),
          ),
        ],
      ),
    );
  }

  Widget _buildStep(
    BuildContext context,
    CourseProject project,
    TranscriptionJob? job,
  ) {
    if (!project.hasAudio) {
      return _StepSurface(
        title: '选择音频',
        description: '音频会复制到项目目录，之后可以随时退出并恢复制作。',
        child: Align(
          alignment: Alignment.topLeft,
          child: FilledButton(onPressed: _bindAudio, child: const Text('选择音频')),
        ),
      );
    }
    if (project.step == CourseProjectStep.transcription &&
        !project.hasTranscript) {
      final active = job?.isActive == true;
      final running = job?.status == TranscriptionJobStatus.running;
      final percent = job == null ? 0 : _percentage(job);
      return _StepSurface(
        title: '转写',
        description: p.basename(project.audioPath!),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (job != null) ...[
              Text(job.message),
              const SizedBox(height: 10),
              if (running) ...[
                Row(
                  children: [
                    Expanded(
                      child: SpringProgressBar(value: percent.toDouble()),
                    ),
                    const SizedBox(width: 12),
                    Text('$percent%'),
                  ],
                ),
                const SizedBox(height: 18),
              ],
            ],
            Row(
              children: [
                FilledButton(
                  key: transcribeButtonKey,
                  onPressed: active || _transcriptionSubmitting
                      ? null
                      : _startTranscription,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (_transcriptionSubmitting) ...[
                        const SizedBox(
                          width: 14,
                          height: 14,
                          child: ProgressRing(strokeWidth: 2),
                        ),
                        const SizedBox(width: 8),
                      ],
                      Text(
                        job?.status == TranscriptionJobStatus.failed
                            ? '重新转写'
                            : '开始转写',
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 8),
                Button(onPressed: _importSrt, child: const Text('导入 SRT')),
                const SizedBox(width: 8),
                Button(
                  onPressed: running ? null : _bindAudio,
                  child: const Text('更换音频'),
                ),
              ],
            ),
          ],
        ),
      );
    }
    final completed = project.step == CourseProjectStep.completed;
    final cues = _reviewCues(project);
    return _ReviewWorkspace(
      project: project,
      cues: cues,
      completed: completed,
      exporting: _creatingPackage,
      addingToLibrary: _addingToLibrary,
      exportingStandalone: _creatingStandalonePlayer,
      selectedCueIndexes: _selectedReviewCues,
      onReplaceCueSelection: (selection) =>
          setState(() => _selectedReviewCues = selection),
      onCreateQuestion: _createQuestion,
      onAddQuestionToMaterial: _addQuestionToMaterial,
      onRemoveQuestion: _removeQuestion,
      onRenameQuestion: _renameQuestion,
      onMoveQuestion: _moveQuestion,
      onChangePhase: _setReviewPhase,
      onToggleCloze: _toggleReviewCloze,
      onCueTextChanged: (index, text) => _updateCueText(project, index, text),
      onComplete: _finishReview,
      onExport: _exportPackage,
      onAddToPlayback: _addToPlayback,
      onExportStandalone: _exportStandalonePlayer,
    );
  }
}

class _ReviewWorkspace extends StatefulWidget {
  const _ReviewWorkspace({
    required this.project,
    required this.cues,
    required this.completed,
    required this.exporting,
    required this.addingToLibrary,
    required this.exportingStandalone,
    required this.selectedCueIndexes,
    required this.onReplaceCueSelection,
    required this.onCreateQuestion,
    required this.onAddQuestionToMaterial,
    required this.onRemoveQuestion,
    required this.onRenameQuestion,
    required this.onMoveQuestion,
    required this.onChangePhase,
    required this.onToggleCloze,
    required this.onCueTextChanged,
    required this.onComplete,
    required this.onExport,
    required this.onAddToPlayback,
    required this.onExportStandalone,
  });

  final CourseProject project;
  final List<SrtCue> cues;
  final bool completed;
  final bool exporting;
  final bool addingToLibrary;
  final bool exportingStandalone;
  final Set<int> selectedCueIndexes;
  final ValueChanged<Set<int>> onReplaceCueSelection;
  final Future<void> Function() onCreateQuestion;
  final ValueChanged<String> onAddQuestionToMaterial;
  final ValueChanged<String> onRemoveQuestion;
  final void Function(String id, String title) onRenameQuestion;
  final void Function(String id, int targetNumber) onMoveQuestion;
  final ValueChanged<ReviewPhase> onChangePhase;
  final void Function(int cueIndex, int wordIndex) onToggleCloze;
  final void Function(int cueIndex, String text) onCueTextChanged;
  final Future<void> Function() onComplete;
  final Future<void> Function() onExport;
  final Future<void> Function() onAddToPlayback;
  final Future<void> Function() onExportStandalone;

  @override
  State<_ReviewWorkspace> createState() => _ReviewWorkspaceState();
}

enum _TranscriptItemKind { section, group, cue, repeat }

class _TranscriptItem {
  const _TranscriptItem({
    required this.kind,
    this.section,
    this.cueIndex,
    this.material,
    this.selectedDraft = false,
    this.count = 0,
    this.repeatId,
  });

  final _TranscriptItemKind kind;
  final SrtTranscriptSection? section;
  final int? cueIndex;
  final LessonMaterial? material;
  final bool selectedDraft;
  final int count;
  final String? repeatId;
}

class _ReviewWorkspaceState extends State<_ReviewWorkspace> {
  bool? _dragSelectionValue;
  int? _lastDragCueIndex;
  int? _dragSectionIndex;
  bool _showQuestionOverview = false;
  bool _manualOperation = false;
  final _cueSelectionKeys = <int, GlobalKey>{};
  final _dragCueBounds = <int, Rect>{};
  final _transcriptScrollController = ScrollController();
  SrtTranscriptStructure? _selectionStructure;
  List<SrtCue>? _structureCues;
  bool? _structureAutomatic;
  final _expandedRepeatIds = <String>{};
  Map<int, int> _transcriptItemIndexByCue = const {};
  int _transcriptItemCount = 0;

  @override
  void didUpdateWidget(covariant _ReviewWorkspace oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.project.id != widget.project.id) {
      _manualOperation = false;
      _showQuestionOverview = false;
      _dragSelectionValue = null;
      _lastDragCueIndex = null;
      _dragSectionIndex = null;
      _dragCueBounds.clear();
      _expandedRepeatIds.clear();
      _selectionStructure = null;
      _structureCues = null;
    }
  }

  GlobalKey _cueSelectionKey(int cueIndex) =>
      _cueSelectionKeys.putIfAbsent(cueIndex, GlobalKey.new);

  void _beginCueSelection(int cueIndex) {
    if (widget.project.exercises.questionIndexForCue(cueIndex) != null) return;
    final structure = _selectionStructure;
    if (structure == null) return;
    final sectionIndex = structure.sectionIndexForCue(cueIndex);
    if (sectionIndex == null) return;
    _dragCueBounds.clear();
    for (final entry in _cueSelectionKeys.entries) {
      final renderBox =
          entry.value.currentContext?.findRenderObject() as RenderBox?;
      if (renderBox == null || !renderBox.hasSize) continue;
      _dragCueBounds[entry.key] =
          renderBox.localToGlobal(Offset.zero) & renderBox.size;
    }
    final selected = !widget.selectedCueIndexes.contains(cueIndex);
    setState(() {
      _dragSelectionValue = selected;
      _lastDragCueIndex = cueIndex;
      _dragSectionIndex = sectionIndex;
    });
    final updated = {...widget.selectedCueIndexes};
    if (selected) {
      updated.add(cueIndex);
    } else {
      updated.remove(cueIndex);
    }
    widget.onReplaceCueSelection(updated);
  }

  void _continueCueSelectionTo(int cueIndex) {
    final selected = _dragSelectionValue;
    final previousIndex = _lastDragCueIndex;
    if (selected == null || previousIndex == null) return;
    final structure = _selectionStructure;
    if (structure == null) return;
    if (structure.sectionIndexForCue(cueIndex) != _dragSectionIndex) return;
    final first = previousIndex < cueIndex ? previousIndex : cueIndex;
    final last = previousIndex > cueIndex ? previousIndex : cueIndex;
    final updated = {...widget.selectedCueIndexes};
    var changed = false;
    for (var index = first; index <= last; index++) {
      if (structure.sectionIndexForCue(index) != _dragSectionIndex ||
          widget.project.exercises.questionIndexForCue(index) != null) {
        continue;
      }
      changed =
          (selected ? updated.add(index) : updated.remove(index)) || changed;
    }
    if (changed) widget.onReplaceCueSelection(updated);
    _lastDragCueIndex = cueIndex;
  }

  void _updateCueSelectionAt(Offset globalPosition) {
    if (_dragSelectionValue == null) return;
    for (final entry in _dragCueBounds.entries) {
      final bounds = entry.value;
      if (globalPosition.dy >= bounds.top &&
          globalPosition.dy <= bounds.bottom) {
        _continueCueSelectionTo(entry.key);
        return;
      }
    }
  }

  void _endCueSelection() {
    if (_dragSelectionValue == null) return;
    _dragCueBounds.clear();
    setState(() {
      _dragSelectionValue = null;
      _lastDragCueIndex = null;
      _dragSectionIndex = null;
    });
  }

  void _selectSection(SrtTranscriptSection section) {
    final available = section.cueIndexes
        .where(
          (cueIndex) =>
              widget.project.exercises.questionIndexForCue(cueIndex) == null,
        )
        .toSet();
    widget.onReplaceCueSelection(available);
  }

  String _materialRange(LessonMaterial material) {
    final validIndexes =
        material.cueIndexes
            .where((index) => index >= 0 && index < widget.cues.length)
            .toList()
          ..sort();
    if (validIndexes.isEmpty) return '未关联字幕';
    final first = widget.cues[validIndexes.first];
    final last = widget.cues[validIndexes.last];
    return '${formatDuration(first.start)} – ${formatDuration(last.end)}';
  }

  void _locateMaterial(LessonMaterial material) {
    final cueIndex = material.cueIndexes.firstOrNull;
    if (cueIndex == null) return;
    setState(() => _showQuestionOverview = false);
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted || !_transcriptScrollController.hasClients) return;
      final position = _transcriptScrollController.position;
      final itemIndex = _transcriptItemIndexByCue[cueIndex] ?? cueIndex;
      final ratio = _transcriptItemCount <= 1
          ? 0.0
          : itemIndex / (_transcriptItemCount - 1);
      await _transcriptScrollController.animateTo(
        position.maxScrollExtent * ratio,
        duration: const Duration(milliseconds: 260),
        curve: Curves.easeOutCubic,
      );
      for (var attempt = 0; attempt < 4 && mounted; attempt++) {
        final cueContext = _cueSelectionKey(cueIndex).currentContext;
        if (cueContext != null && cueContext.mounted) {
          await Scrollable.ensureVisible(
            cueContext,
            alignment: 0.42,
            duration: const Duration(milliseconds: 180),
            curve: Curves.easeOutCubic,
          );
          return;
        }
        final visible = _cueSelectionKeys.entries.where(
          (entry) =>
              entry.value.currentContext != null &&
              _transcriptItemIndexByCue.containsKey(entry.key),
        );
        if (visible.isEmpty || !_transcriptScrollController.hasClients) return;
        final nearest = visible.reduce((left, right) {
          final leftDistance =
              (_transcriptItemIndexByCue[left.key]! - itemIndex).abs();
          final rightDistance =
              (_transcriptItemIndexByCue[right.key]! - itemIndex).abs();
          return leftDistance <= rightDistance ? left : right;
        });
        final renderBox =
            nearest.value.currentContext?.findRenderObject() as RenderBox?;
        if (renderBox == null || !renderBox.hasSize) return;
        final step = itemIndex - _transcriptItemIndexByCue[nearest.key]!;
        final scrollPosition = _transcriptScrollController.position;
        final destination =
            (scrollPosition.pixels + step * (renderBox.size.height + 4)).clamp(
              0.0,
              scrollPosition.maxScrollExtent,
            );
        if ((destination - scrollPosition.pixels).abs() < 8) return;
        await _transcriptScrollController.animateTo(
          destination,
          duration: const Duration(milliseconds: 160),
          curve: Curves.easeOutCubic,
        );
      }
    });
  }

  Widget _buildQuestionOverview(
    BuildContext context,
    SrtTranscriptStructure structure,
  ) {
    final theme = FluentTheme.of(context);
    final exercises = widget.project.exercises;
    final materials = exercises.effectiveMaterials;
    if (materials.isEmpty) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(FluentIcons.edit_create, size: 30),
            const SizedBox(height: 12),
            Text('还没有题目', style: theme.typography.bodyStrong),
            const SizedBox(height: 4),
            Text('返回原文，选择连续字幕后创建题目。', style: theme.typography.caption),
            const SizedBox(height: 14),
            Button(
              onPressed: () => setState(() => _showQuestionOverview = false),
              child: const Text('返回原文'),
            ),
          ],
        ),
      );
    }
    return ListView.separated(
      padding: const EdgeInsets.only(top: 4, bottom: 8),
      itemCount: materials.length,
      separatorBuilder: (_, _) => const SizedBox(height: 8),
      itemBuilder: (context, index) {
        final material = materials[index];
        final questions = exercises.questionsForMaterial(material);
        final firstCueIndex = material.cueIndexes.firstOrNull;
        final sectionIndex = firstCueIndex == null
            ? null
            : structure.sectionIndexForCue(firstCueIndex);
        final sectionLabel = sectionIndex == null
            ? '原文'
            : structure.sections[sectionIndex].label;
        final questionNumbers = questions
            .map((question) => question.number)
            .where((number) => number > 0)
            .join('、');
        final materialLabel = questionNumbers.isEmpty
            ? '听力材料'
            : '第 $questionNumbers 题';
        return Card(
          padding: EdgeInsets.zero,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(14, 12, 8, 10),
                child: Row(
                  children: [
                    const Icon(FluentIcons.music_note, size: 17),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            materialLabel,
                            style: theme.typography.bodyStrong,
                          ),
                          const SizedBox(height: 3),
                          Text(
                            '${_materialRange(material)}  ·  '
                            '${material.cueIndexes.length} 句  ·  '
                            '${questions.length} 小题',
                            style: theme.typography.caption,
                          ),
                        ],
                      ),
                    ),
                    Button(
                      onPressed: () =>
                          widget.onAddQuestionToMaterial(material.id),
                      child: const Text('添加小题'),
                    ),
                    const SizedBox(width: 4),
                    Tooltip(
                      message: '在原文中查看',
                      child: IconButton(
                        icon: const Icon(FluentIcons.search, size: 14),
                        onPressed: () => _locateMaterial(material),
                      ),
                    ),
                  ],
                ),
              ),
              const Divider(
                style: DividerThemeData(horizontalMargin: EdgeInsets.zero),
              ),
              for (
                var questionIndex = 0;
                questionIndex < questions.length;
                questionIndex++
              ) ...[
                if (questionIndex > 0)
                  const Divider(
                    style: DividerThemeData(horizontalMargin: EdgeInsets.zero),
                  ),
                _QuestionQuickEditor(
                  key: ValueKey(questions[questionIndex].id),
                  number: questions[questionIndex].number > 0
                      ? questions[questionIndex].number
                      : exercises.questions.indexWhere(
                              (item) => item.id == questions[questionIndex].id,
                            ) +
                            1,
                  question: questions[questionIndex],
                  questionCount: exercises.questions.length,
                  rangeLabel: _materialRange(material),
                  sectionLabel: sectionLabel,
                  embedded: true,
                  onNumberChanged: (number) => widget.onMoveQuestion(
                    questions[questionIndex].id,
                    number,
                  ),
                  onTitleChanged: (title) => widget.onRenameQuestion(
                    questions[questionIndex].id,
                    title,
                  ),
                  onRemove: () =>
                      widget.onRemoveQuestion(questions[questionIndex].id),
                  onLocate: () => _locateMaterial(material),
                ),
              ],
            ],
          ),
        );
      },
    );
  }

  Widget _buildReviewCueRow(BuildContext context, int cueIndex, bool grouping) {
    final theme = FluentTheme.of(context);
    final project = widget.project;
    final cue = widget.cues[cueIndex];
    final material = project.exercises.materialForCue(cueIndex);
    final materialQuestions = material == null
        ? const <LessonQuestion>[]
        : project.exercises.questionsForMaterial(material);
    final questionLabel = materialQuestions.isEmpty
        ? '未归题'
        : materialQuestions
              .map(
                (question) => question.number > 0
                    ? '${question.number}'
                    : '${project.exercises.questions.indexOf(question) + 1}',
              )
              .join('、');
    return Container(
      key: _cueSelectionKey(cueIndex),
      constraints: const BoxConstraints(minHeight: 54),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          SizedBox(
            width: 72,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(formatDuration(cue.start), style: timeStyle),
                const SizedBox(height: 2),
                Text(
                  material == null ? '未归题' : '第 $questionLabel 题',
                  style: theme.typography.caption,
                ),
              ],
            ),
          ),
          if (grouping) ...[
            _CueSelectionHandle(
              key: reviewCueSelectionHandleKey(cueIndex),
              selected: widget.selectedCueIndexes.contains(cueIndex),
              locked: material != null,
              onPointerDown: () => _beginCueSelection(cueIndex),
            ),
            const SizedBox(width: 8),
          ],
          Expanded(
            child: grouping
                ? _CueTextEditor(
                    text: cue.text,
                    onChanged: (value) =>
                        widget.onCueTextChanged(cueIndex, value),
                  )
                : _ReviewClozeWords(
                    text: cue.text,
                    selected:
                        project.exercises.clozeWordIndexes[cueIndex] ??
                        const {},
                    onToggle: (wordIndex) =>
                        widget.onToggleCloze(cueIndex, wordIndex),
                  ),
          ),
        ],
      ),
    );
  }

  Color _materialFrameColor(LessonMaterial material) {
    const palette = <Color>[
      Color(0xFF2563EB),
      Color(0xFF7C3AED),
      Color(0xFF0F766E),
      Color(0xFFB45309),
      Color(0xFFBE185D),
      Color(0xFF3F6212),
    ];
    final materials = widget.project.exercises.effectiveMaterials;
    final index = materials.indexWhere((item) => item.id == material.id);
    return palette[(index < 0 ? material.id.hashCode : index) % palette.length];
  }

  List<_TranscriptItem> _transcriptItems(
    SrtTranscriptStructure structure,
    bool grouping,
  ) {
    final items = <_TranscriptItem>[];
    final itemIndexByCue = <int, int>{};
    final materials = widget.project.exercises.effectiveMaterials;
    final materialByCue = <int, LessonMaterial>{};
    final repeatedByCue = <int, LessonMaterial>{};
    for (final material in materials) {
      for (final cueIndex in material.cueIndexes) {
        materialByCue[cueIndex] = material;
      }
      if (!_manualOperation) {
        for (final cueIndex in material.repeatedCueIndexes) {
          repeatedByCue[cueIndex] = material;
        }
      }
    }

    void addCue(int cueIndex, {LessonMaterial? material}) {
      itemIndexByCue[cueIndex] = items.length;
      items.add(
        _TranscriptItem(
          kind: _TranscriptItemKind.cue,
          cueIndex: cueIndex,
          material: material,
        ),
      );
    }

    for (
      var sectionIndex = 0;
      sectionIndex < structure.sections.length;
      sectionIndex++
    ) {
      final section = structure.sections[sectionIndex];
      items.add(
        _TranscriptItem(kind: _TranscriptItemKind.section, section: section),
      );
      if (grouping) {
        var start = 0;
        while (start < section.cueIndexes.length) {
          final firstCue = section.cueIndexes[start];
          final material = materialByCue[firstCue];
          final selectedDraft =
              material == null && widget.selectedCueIndexes.contains(firstCue);
          var end = start + 1;
          while (end < section.cueIndexes.length) {
            final cueIndex = section.cueIndexes[end];
            final nextMaterial = materialByCue[cueIndex];
            if (nextMaterial?.id != material?.id) {
              break;
            }
            end++;
          }
          final run = section.cueIndexes.sublist(start, end);
          items.add(
            _TranscriptItem(
              kind: _TranscriptItemKind.group,
              material: material,
              selectedDraft: selectedDraft,
              count: run.length,
            ),
          );
          final repeated = <int>[];
          for (final cueIndex in run) {
            if (material != null &&
                repeatedByCue[cueIndex]?.id == material.id) {
              repeated.add(cueIndex);
            } else {
              addCue(cueIndex, material: material);
            }
          }
          if (repeated.isNotEmpty) {
            final repeatId = '$sectionIndex-${material!.id}-$start';
            items.add(
              _TranscriptItem(
                kind: _TranscriptItemKind.repeat,
                material: material,
                count: repeated.length,
                repeatId: repeatId,
              ),
            );
            if (_expandedRepeatIds.contains(repeatId)) {
              for (final cueIndex in repeated) {
                addCue(cueIndex, material: material);
              }
            }
          }
          start = end;
        }
      } else {
        final emittedRepeats = <String>{};
        for (final cueIndex in section.cueIndexes) {
          final material = repeatedByCue[cueIndex];
          if (material == null) {
            addCue(cueIndex);
            continue;
          }
          if (!emittedRepeats.add(material.id)) continue;
          final repeated = section.cueIndexes
              .where((index) => repeatedByCue[index]?.id == material.id)
              .toList(growable: false);
          final repeatId = '$sectionIndex-${material.id}';
          items.add(
            _TranscriptItem(
              kind: _TranscriptItemKind.repeat,
              material: material,
              count: repeated.length,
              repeatId: repeatId,
            ),
          );
          if (_expandedRepeatIds.contains(repeatId)) {
            for (final repeatedCue in repeated) {
              addCue(repeatedCue);
            }
          }
        }
      }
    }
    _transcriptItemIndexByCue = itemIndexByCue;
    _transcriptItemCount = items.length;
    return items;
  }

  Widget _buildTranscriptItem(
    BuildContext context,
    _TranscriptItem item,
    bool grouping,
  ) {
    final theme = FluentTheme.of(context);
    switch (item.kind) {
      case _TranscriptItemKind.section:
        final section = item.section!;
        return Padding(
          padding: const EdgeInsets.fromLTRB(2, 10, 2, 7),
          child: Row(
            children: [
              Expanded(
                child: Text(section.label, style: theme.typography.bodyStrong),
              ),
              Text(
                '${section.cueIndexes.length} 句',
                style: theme.typography.caption,
              ),
              if (grouping) ...[
                const SizedBox(width: 8),
                Button(
                  onPressed: () => _selectSection(section),
                  child: const Text('选择整段'),
                ),
              ],
            ],
          ),
        );
      case _TranscriptItemKind.group:
        final material = item.material;
        final accent = material == null
            ? item.selectedDraft
                  ? theme.accentColor
                  : theme.inactiveColor
            : _materialFrameColor(material);
        final numbers = material == null
            ? ''
            : widget.project.exercises
                  .questionsForMaterial(material)
                  .map((question) => question.number)
                  .where((number) => number > 0)
                  .join('、');
        final title = material == null
            ? item.selectedDraft
                  ? '待新建题目'
                  : '未归题'
            : numbers.isEmpty
            ? '听力材料'
            : '第 $numbers 题';
        return Container(
          margin: const EdgeInsets.only(top: 8),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          decoration: BoxDecoration(
            color: accent.withValues(alpha: 0.1),
            border: Border(left: BorderSide(color: accent, width: 4)),
          ),
          child: Row(
            children: [
              Expanded(child: Text(title, style: theme.typography.bodyStrong)),
              Text('${item.count} 句', style: theme.typography.caption),
            ],
          ),
        );
      case _TranscriptItemKind.repeat:
        final repeatId = item.repeatId!;
        final expanded = _expandedRepeatIds.contains(repeatId);
        return Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Button(
            onPressed: () => setState(() {
              if (expanded) {
                _expandedRepeatIds.remove(repeatId);
              } else {
                _expandedRepeatIds.add(repeatId);
              }
            }),
            child: Row(
              children: [
                Icon(
                  expanded
                      ? FluentIcons.chevron_down
                      : FluentIcons.chevron_right,
                  size: 14,
                ),
                const SizedBox(width: 8),
                const Expanded(child: Text('重复朗读')),
                Text('${item.count} 句'),
              ],
            ),
          ),
        );
      case _TranscriptItemKind.cue:
        final cueIndex = item.cueIndex!;
        final row = _buildReviewCueRow(context, cueIndex, grouping);
        if (!grouping) {
          return Card(padding: EdgeInsets.zero, child: row);
        }
        final accent = item.material == null
            ? theme.inactiveColor
            : _materialFrameColor(item.material!);
        return DecoratedBox(
          decoration: BoxDecoration(
            color: accent.withValues(alpha: 0.025),
            border: Border(left: BorderSide(color: accent, width: 2)),
          ),
          child: row,
        );
    }
  }

  Widget _buildTranscript(
    BuildContext context,
    SrtTranscriptStructure structure,
    bool grouping,
  ) {
    final items = _transcriptItems(structure, grouping);
    return Listener(
      behavior: HitTestBehavior.translucent,
      onPointerMove: (event) => _updateCueSelectionAt(event.position),
      onPointerUp: (_) => _endCueSelection(),
      onPointerCancel: (_) => _endCueSelection(),
      child: ListView.builder(
        controller: _transcriptScrollController,
        physics: _dragSelectionValue == null
            ? null
            : const NeverScrollableScrollPhysics(),
        itemCount: items.length,
        itemBuilder: (context, index) {
          final item = items[index];
          return Padding(
            key: item.cueIndex == null
                ? null
                : ValueKey('review-cue-${item.cueIndex}'),
            padding: const EdgeInsets.only(bottom: 4),
            child: _buildTranscriptItem(context, item, grouping),
          );
        },
      ),
    );
  }

  @override
  void dispose() {
    _transcriptScrollController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final project = widget.project;
    final grouping = project.reviewPhase == ReviewPhase.grouping;
    final automatic = !_manualOperation;
    if (!identical(_structureCues, widget.cues) ||
        _structureAutomatic != automatic ||
        _selectionStructure == null) {
      _selectionStructure = SrtTranscriptStructure.fromCues(
        widget.cues,
        automatic: automatic,
      );
      _structureCues = widget.cues;
      _structureAutomatic = automatic;
    }
    final structure = _selectionStructure!;
    final repeatedQuestionCount = project.exercises.effectiveMaterials
        .where((material) => material.repeatedCueIndexes.isNotEmpty)
        .length;
    final deliveryBusy =
        widget.exporting ||
        widget.addingToLibrary ||
        widget.exportingStandalone;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Text(
              widget.completed ? '编辑练习' : '审阅与练习',
              style: theme.typography.subtitle,
            ),
            const SizedBox(width: 16),
            Text(
              grouping ? '分题' : '设置挖空',
              style: theme.typography.bodyStrong?.copyWith(
                color: theme.accentColor,
              ),
            ),
            if (grouping) ...[
              const SizedBox(width: 12),
              Button(
                onPressed: () => setState(() {
                  _manualOperation = !_manualOperation;
                  _showQuestionOverview = false;
                }),
                child: Text(_manualOperation ? '恢复自动处理' : '手动操作'),
              ),
            ],
            const Spacer(),
            if (grouping) ...[
              Button(
                key: reviewQuestionOverviewButtonKey,
                onPressed: () => setState(
                  () => _showQuestionOverview = !_showQuestionOverview,
                ),
                child: Text(
                  _showQuestionOverview
                      ? '返回原文'
                      : '题目总览 (${project.exercises.questions.length})',
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(
                onPressed:
                    _showQuestionOverview || widget.selectedCueIndexes.isEmpty
                    ? null
                    : () => widget.onCreateQuestion(),
                child: Text('新建题目 (${widget.selectedCueIndexes.length})'),
              ),
            ],
          ],
        ),
        const SizedBox(height: 4),
        Text(
          grouping
              ? (_showQuestionOverview
                    ? '集中编辑题号、题目内容与题目顺序。'
                    : _manualOperation
                    ? '当前显示全部 SRT 内容，可直接编辑并手动选择连续字幕。'
                    : project.exercises.questions.isNotEmpty
                    ? '已整理 ${project.exercises.effectiveMaterials.length} 段材料、${project.exercises.questions.length} 道题；$repeatedQuestionCount 段包含重复朗读。'
                    : structure.hasMarkers
                    ? '进入页面时已自动识别并隔离 ${structure.markerCueIndexes.length} 条提示，形成 ${structure.sections.length} 个可选区段。'
                    : '未检测到提示句，按连续原文显示；拖动选择连续字幕后新建题目。')
              : '点击英文单词设置挖空；标点不参与分词。',
          style: theme.typography.caption,
        ),
        const SizedBox(height: 10),
        Expanded(
          child: widget.cues.isEmpty
              ? const Center(child: Text('字幕格式有误，无法生成时间卡片。'))
              : grouping && _showQuestionOverview
              ? _buildQuestionOverview(context, structure)
              : _buildTranscript(context, structure, grouping),
        ),
        const SizedBox(height: 12),
        Row(
          children: [
            if (!grouping)
              Button(
                onPressed: () => widget.onChangePhase(ReviewPhase.grouping),
                child: const Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(FluentIcons.back),
                    SizedBox(width: 8),
                    Text('返回题目'),
                  ],
                ),
              ),
            const SizedBox(width: 8),
            Expanded(
              child: Align(
                alignment: Alignment.centerRight,
                child: grouping
                    ? FilledButton(
                        onPressed: () =>
                            widget.onChangePhase(ReviewPhase.cloze),
                        child: const Text('下一步：设置挖空'),
                      )
                    : !widget.completed
                    ? FilledButton(
                        onPressed: () => widget.onComplete(),
                        child: const Text('完成审阅'),
                      )
                    : Wrap(
                        alignment: WrapAlignment.end,
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          FilledButton(
                            key: createPackageButtonKey,
                            style: ButtonStyle(
                              backgroundColor: WidgetStateProperty.resolveWith((
                                states,
                              ) {
                                if (states.contains(WidgetState.disabled)) {
                                  return Colors.grey[60];
                                }
                                if (states.contains(WidgetState.pressed)) {
                                  return Colors.red.darker;
                                }
                                if (states.contains(WidgetState.hovered)) {
                                  return Colors.red.lighter;
                                }
                                return Colors.red;
                              }),
                            ),
                            onPressed: deliveryBusy
                                ? null
                                : () => widget.onExport(),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                if (widget.exporting) ...[
                                  const SizedBox(
                                    width: 14,
                                    height: 14,
                                    child: ProgressRing(strokeWidth: 2),
                                  ),
                                  const SizedBox(width: 8),
                                ],
                                Text(widget.exporting ? '正在导出 .ilp' : '导出为精听包'),
                              ],
                            ),
                          ),
                          Button(
                            onPressed: deliveryBusy
                                ? null
                                : () => widget.onExportStandalone(),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                if (widget.exportingStandalone) ...[
                                  const SizedBox(
                                    width: 14,
                                    height: 14,
                                    child: ProgressRing(strokeWidth: 2),
                                  ),
                                  const SizedBox(width: 8),
                                ],
                                Text(
                                  widget.exportingStandalone
                                      ? '正在生成 .exe'
                                      : '导出为独立精听包',
                                ),
                              ],
                            ),
                          ),
                          Button(
                            onPressed: deliveryBusy
                                ? null
                                : () => widget.onAddToPlayback(),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                if (widget.addingToLibrary) ...[
                                  const SizedBox(
                                    width: 14,
                                    height: 14,
                                    child: ProgressRing(strokeWidth: 2),
                                  ),
                                  const SizedBox(width: 8),
                                ],
                                Text(widget.addingToLibrary ? '正在添加' : '添加到播放'),
                              ],
                            ),
                          ),
                        ],
                      ),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _CueSelectionHandle extends StatelessWidget {
  const _CueSelectionHandle({
    super.key,
    required this.selected,
    required this.locked,
    required this.onPointerDown,
  });

  final bool selected;
  final bool locked;
  final VoidCallback onPointerDown;

  @override
  Widget build(BuildContext context) {
    return Listener(
      behavior: HitTestBehavior.opaque,
      onPointerDown: (_) {
        if (!locked) onPointerDown();
      },
      child: SizedBox(
        width: 30,
        height: 34,
        child: Center(
          child: locked
              ? Tooltip(
                  message: '已归入题目',
                  child: Icon(
                    FluentIcons.lock,
                    size: 14,
                    color: FluentTheme.of(context).inactiveColor,
                  ),
                )
              : IgnorePointer(
                  child: Checkbox(checked: selected, onChanged: (_) {}),
                ),
        ),
      ),
    );
  }
}

class _QuestionQuickEditor extends StatefulWidget {
  const _QuestionQuickEditor({
    super.key,
    required this.number,
    required this.question,
    required this.questionCount,
    required this.rangeLabel,
    required this.sectionLabel,
    required this.onNumberChanged,
    required this.onTitleChanged,
    required this.onRemove,
    required this.onLocate,
    this.embedded = false,
  });

  final int number;
  final LessonQuestion question;
  final int questionCount;
  final String rangeLabel;
  final String sectionLabel;
  final ValueChanged<int> onNumberChanged;
  final ValueChanged<String> onTitleChanged;
  final VoidCallback onRemove;
  final VoidCallback onLocate;
  final bool embedded;

  @override
  State<_QuestionQuickEditor> createState() => _QuestionQuickEditorState();
}

class _QuestionQuickEditorState extends State<_QuestionQuickEditor> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.question.title,
  );
  Timer? _commitTimer;

  @override
  void didUpdateWidget(covariant _QuestionQuickEditor oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.question.title != oldWidget.question.title &&
        widget.question.title != _controller.text) {
      _controller.text = widget.question.title;
    }
  }

  void _scheduleTitleCommit(String value) {
    _commitTimer?.cancel();
    _commitTimer = Timer(
      const Duration(milliseconds: 280),
      () => widget.onTitleChanged(value),
    );
  }

  @override
  void dispose() {
    _commitTimer?.cancel();
    if (_controller.text != widget.question.title) {
      widget.onTitleChanged(_controller.text);
    }
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final editor = Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SizedBox(
          width: 104,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('题号', style: theme.typography.caption),
              const SizedBox(height: 5),
              NumberBox(
                value: widget.number.toDouble(),
                min: 1,
                max: 999,
                smallChange: 1,
                mode: SpinButtonPlacementMode.compact,
                onChanged: (value) {
                  if (value != null) widget.onNumberChanged(value.round());
                },
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('题目', style: theme.typography.caption),
              const SizedBox(height: 5),
              TextBox(
                controller: _controller,
                placeholder: '输入题目内容',
                minLines: 2,
                maxLines: 6,
                onChanged: _scheduleTitleCommit,
                onSubmitted: widget.onTitleChanged,
              ),
              const SizedBox(height: 7),
              Text(
                '${widget.sectionLabel}  ·  ${widget.rangeLabel}  ·  '
                '${widget.question.cueIndexes.length} 句',
                style: theme.typography.caption,
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        Tooltip(
          message: '在原文中查看',
          child: IconButton(
            icon: const Icon(FluentIcons.search, size: 14),
            onPressed: widget.onLocate,
          ),
        ),
        const SizedBox(width: 4),
        Tooltip(
          message: '删除题目',
          child: IconButton(
            icon: const Icon(FluentIcons.delete, size: 14),
            onPressed: widget.onRemove,
          ),
        ),
      ],
    );
    if (widget.embedded) {
      return Padding(
        padding: const EdgeInsets.fromLTRB(14, 10, 8, 12),
        child: editor,
      );
    }
    return Card(padding: const EdgeInsets.all(12), child: editor);
  }
}

class _CueTextEditor extends StatefulWidget {
  const _CueTextEditor({required this.text, required this.onChanged});

  final String text;
  final ValueChanged<String> onChanged;

  @override
  State<_CueTextEditor> createState() => _CueTextEditorState();
}

class _CueTextEditorState extends State<_CueTextEditor> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.text,
  );

  @override
  void didUpdateWidget(covariant _CueTextEditor oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.text != oldWidget.text && widget.text != _controller.text) {
      _controller.text = widget.text;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return TextBox(
      controller: _controller,
      minLines: 1,
      maxLines: 3,
      onChanged: widget.onChanged,
    );
  }
}

class _ReviewClozeWords extends StatelessWidget {
  const _ReviewClozeWords({
    required this.text,
    required this.selected,
    required this.onToggle,
  });

  final String text;
  final Set<int> selected;
  final ValueChanged<int> onToggle;

  @override
  Widget build(BuildContext context) {
    return Wrap(
      spacing: 2,
      runSpacing: 6,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        for (final part in tokenizeLessonText(text))
          if (!part.isWord)
            Text(part.text)
          else
            ToggleButton(
              checked: selected.contains(part.wordIndex),
              onChanged: (_) => onToggle(part.wordIndex!),
              child: Text(part.text),
            ),
      ],
    );
  }
}

class _ProjectList extends StatelessWidget {
  const _ProjectList({
    required this.projects,
    required this.selected,
    required this.statusFor,
    required this.jobFor,
    required this.onSelected,
    required this.onRemove,
  });

  final List<CourseProject> projects;
  final CourseProject? selected;
  final String Function(CourseProject project) statusFor;
  final TranscriptionJob? Function(CourseProject project) jobFor;
  final ValueChanged<CourseProject> onSelected;
  final ValueChanged<CourseProject> onRemove;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 4, 12, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: projects.isEmpty
                ? Center(
                    child: Text(
                      '暂无项目',
                      style: theme.typography.caption?.copyWith(
                        color: theme.inactiveColor,
                      ),
                    ),
                  )
                : ListView.separated(
                    itemCount: projects.length,
                    separatorBuilder: (_, _) => const SizedBox(height: 4),
                    itemBuilder: (context, index) {
                      final project = projects[index];
                      final job = jobFor(project);
                      final progress = job?.fraction;
                      return ListTile.selectable(
                        selected: selected?.id == project.id,
                        title: Text(
                          project.title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        subtitle: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            const SizedBox(height: 3),
                            Text(statusFor(project)),
                            if (job?.status ==
                                TranscriptionJobStatus.running) ...[
                              const SizedBox(height: 6),
                              SpringProgressBar(
                                value: ((progress ?? 0) * 100).clamp(0, 100),
                              ),
                            ],
                          ],
                        ),
                        trailing: Tooltip(
                          message: '删除项目',
                          child: IconButton(
                            icon: const Icon(FluentIcons.delete),
                            onPressed: () => onRemove(project),
                          ),
                        ),
                        onPressed: () => onSelected(project),
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }
}

class _NoProject extends StatelessWidget {
  const _NoProject();

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 360),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              FluentIcons.open_folder_horizontal,
              size: 40,
              color: theme.inactiveColor,
            ),
            const SizedBox(height: 16),
            Text('还没有课程项目', style: theme.typography.subtitle),
            const SizedBox(height: 6),
            Text(
              '创建项目后即可绑定音频、转写字幕并导出精听包。',
              textAlign: TextAlign.center,
              style: theme.typography.body?.copyWith(
                color: theme.inactiveColor,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ProjectSteps extends StatelessWidget {
  const _ProjectSteps({required this.step});

  final CourseProjectStep step;

  @override
  Widget build(BuildContext context) {
    const labels = ['音频', '转写', '审阅', '完成'];
    final index = step.index;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SpringProgressBar(value: (index / (labels.length - 1)) * 100),
        const SizedBox(height: 8),
        Row(
          children: [
            for (var current = 0; current < labels.length; current++)
              Expanded(
                child: Text(
                  labels[current],
                  textAlign: current == 0
                      ? TextAlign.start
                      : current == labels.length - 1
                      ? TextAlign.end
                      : TextAlign.center,
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight: current == index
                        ? FontWeight.w500
                        : FontWeight.normal,
                    color: current <= index
                        ? FluentTheme.of(context).accentColor
                        : FluentTheme.of(context).typography.caption?.color,
                  ),
                ),
              ),
          ],
        ),
      ],
    );
  }
}

class _StepSurface extends StatelessWidget {
  const _StepSurface({
    required this.title,
    required this.description,
    required this.child,
  });

  final String title;
  final String description;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(title, style: theme.typography.subtitle),
        const SizedBox(height: 4),
        Text(description, style: theme.typography.caption),
        const SizedBox(height: 18),
        Expanded(child: child),
      ],
    );
  }
}

class _SpringReveal extends StatelessWidget {
  const _SpringReveal({required this.transitionKey, required this.child});

  final String transitionKey;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 280),
      reverseDuration: const Duration(milliseconds: 360),
      switchInCurve: Curves.easeOutCubic,
      switchOutCurve: Curves.easeInCubic,
      transitionBuilder: (child, animation) {
        return SpringMotionTransition(
          animation: animation,
          beginOffset: const Offset(0.012, 0),
          beginScale: 1,
          child: child,
        );
      },
      child: KeyedSubtree(key: ValueKey(transitionKey), child: child),
    );
  }
}

class LegacyTeacherPage extends StatefulWidget {
  const LegacyTeacherPage({
    super.key,
    required this.onPackageCreated,
    required this.settings,
    required this.onOpenSettings,
    required this.transcriptionQueue,
    required this.loadSrtRequests,
  });

  final VoidCallback onPackageCreated;
  final AppSettings settings;
  final VoidCallback onOpenSettings;
  final TranscriptionQueue transcriptionQueue;
  final Stream<TranscriptionJob> loadSrtRequests;

  @override
  State<LegacyTeacherPage> createState() => LegacyTeacherPageState();
}

class LegacyTeacherPageState extends State<LegacyTeacherPage> {
  final _titleController = TextEditingController();
  final _srtController = TextEditingController();
  File? _audioFile;
  Duration? _audioDuration;
  File? _srtFile;
  var _creating = false;
  var _queueBusy = false;
  TeacherNotice? _notice;
  StreamSubscription<TranscriptionJob>? _loadSrtSubscription;

  bool get _isBusy => _creating;

  @override
  void initState() {
    super.initState();
    _queueBusy = widget.transcriptionQueue.isBusy;
    widget.transcriptionQueue.addListener(_onQueueChanged);
    _loadSrtSubscription = widget.loadSrtRequests.listen((job) {
      final srt = job.srt;
      if (srt != null && srt.isNotEmpty) _applySrt(srt, job.id);
    });
  }

  @override
  void dispose() {
    widget.transcriptionQueue.removeListener(_onQueueChanged);
    _loadSrtSubscription?.cancel();
    _titleController.dispose();
    _srtController.dispose();
    super.dispose();
  }

  void _onQueueChanged() {
    if (!mounted) return;
    final busy = widget.transcriptionQueue.isBusy;
    if (busy != _queueBusy) setState(() => _queueBusy = busy);
    unawaited(_adoptCompletedSrt());
  }

  Future<void> _adoptCompletedSrt() async {
    if (!mounted || _srtController.text.trim().isNotEmpty) return;
    final job = widget.transcriptionQueue.latestUnconsumedSrt;
    final srt = job?.srt;
    if (job == null || srt == null || srt.isEmpty) return;
    _applySrt(srt, job.id);
  }

  void _applySrt(String srt, String jobId) {
    setState(() {
      _srtFile = null;
      _srtController.text = srt;
      _notice = TeacherNotice.success('转写完成', '字幕已写入 SRT，审查环节已准备。');
    });
    widget.transcriptionQueue.markSrtConsumed(jobId);
  }

  Future<Directory> _libraryDirectory() async {
    return resolveLessonLibraryDirectory();
  }

  Future<void> _pickAudio() async {
    final selectedPath = await pickFileWith(
      allowedExtensions: IlpManifest.supportedAudioExtensions.toList(),
      dialogTitle: '选择课程音频',
    );
    if (selectedPath == null) return;
    final file = File(selectedPath);
    setState(() {
      _audioFile = file;
      _audioDuration = null;
      if (_titleController.text.trim().isEmpty) {
        _titleController.text = p.basenameWithoutExtension(selectedPath);
      }
      _notice = TeacherNotice.success('音频已选择', p.basename(selectedPath));
    });
    final duration = await probeAudioDuration(file.path);
    if (!mounted || _audioFile?.path != file.path) return;
    setState(() => _audioDuration = duration);
  }

  Future<void> _pickSrt() async {
    final selectedPath = await pickFileWith(
      allowedExtensions: const ['srt'],
      dialogTitle: '选择 SRT 字幕',
    );
    if (selectedPath == null) return;
    final file = File(selectedPath);
    final text = await file.readAsString();
    setState(() {
      _srtFile = file;
      _srtController.text = text;
      _notice = TeacherNotice.success('SRT 已导入', p.basename(selectedPath));
    });
  }

  Future<void> _createPackage() async {
    final audio = _audioFile;
    if (audio == null) {
      setState(() {
        _notice = TeacherNotice.error('需要音频', '请选择课程音频文件。');
      });
      return;
    }
    if (_srtFile == null && _srtController.text.trim().isEmpty) {
      setState(() {
        _notice = TeacherNotice.error('需要字幕', '请选择或填写 SRT 字幕。');
      });
      return;
    }

    setState(() {
      _creating = true;
      _notice = TeacherNotice.info('正在生成', '正在校验音频、SRT 并创建 .ilp 文件。');
    });
    Directory? tempDirectory;
    try {
      tempDirectory = await Directory.systemTemp.createTemp('ilp-');
      final transcript = File(p.join(tempDirectory.path, 'transcript.srt'));
      await transcript.writeAsString(_srtController.text.trim(), flush: true);
      final package = await const IlpCreator().create(
        title: _titleController.text,
        audioFile: audio,
        transcriptFile: transcript,
        outputFile: File(p.join(tempDirectory.path, 'lesson.ilp')),
        packageUuid: '00000000-0000-4000-8000-000000000001',
        packageVersion: 1,
      );
      final savedUri = await FilePicker.saveFile(
        dialogTitle: '保存精听包',
        fileName: '${safeFileName(_titleController.text)}.ilp',
        bytes: await package.readAsBytes(),
        type: FileType.custom,
        allowedExtensions: const ['ilp'],
      );
      if (savedUri == null || !mounted) {
        if (mounted) {
          setState(() {
            _notice = TeacherNotice.warning('已取消保存', '精听包没有写入目标位置。');
          });
        }
        return;
      }
      final libraryDirectory = await _libraryDirectory();
      final importer = IlpImporter(libraryDirectory);
      var notice = TeacherNotice.success('已创建', '课程已生成并保存为 .ilp。');
      try {
        final lesson = await importer.importFile(package);
        notice = await _sharedAudioNotice(libraryDirectory, lesson) ?? notice;
      } on IlpException catch (error) {
        if (error.code != IlpError.duplicatePackage) rethrow;
        notice = TeacherNotice.warning('已存在相同课程', '相同音频的精听包已在学生端，未重复导入。');
      }
      widget.onPackageCreated();
      if (mounted) {
        setState(() {
          _notice = notice;
        });
      }
    } on IlpException catch (error) {
      if (mounted) {
        setState(() {
          _notice = TeacherNotice.error('无法创建', error.message);
        });
      }
    } finally {
      final directory = tempDirectory;
      if (directory != null && await directory.exists()) {
        await directory.delete(recursive: true);
      }
      if (mounted) setState(() => _creating = false);
    }
  }

  Future<TeacherNotice?> _sharedAudioNotice(
    Directory libraryDirectory,
    ImportedLesson lesson,
  ) async {
    final refs = await IlpLibrary(libraryDirectory).scanManifests();
    for (final ref in refs) {
      if (ref.id != lesson.id &&
          ref.manifest.audioSha256 == lesson.manifest.audioSha256) {
        return TeacherNotice.warning(
          '发现相同音频',
          '学生端已有「${ref.manifest.title}」使用同一段音频，仍可继续导入。',
        );
      }
    }
    return null;
  }

  void _startTranscriptionTask() {
    final audio = _audioFile;
    if (audio == null) {
      setState(() {
        _notice = TeacherNotice.error('需要音频', '请选择课程音频文件。');
      });
      return;
    }

    final config = widget.settings.cloudAsrConfig;
    if (!config.isComplete) {
      setState(() {
        _notice = TeacherNotice.error(
          '需要 API 配置',
          '请在设置中填写服务地址、模型和密钥，保存后再开始转写。',
        );
      });
      widget.onOpenSettings();
      return;
    }

    widget.transcriptionQueue.enqueue(
      title: _titleController.text.trim().isEmpty
          ? p.basenameWithoutExtension(audio.path)
          : _titleController.text.trim(),
      audio: audio,
      audioDuration: _audioDuration,
    );
    setState(() {
      _notice = TeacherNotice.info('已加入转写队列', '可以继续使用应用，完成后会自动提示并写入 SRT。');
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final audio = _audioFile;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('课程制作'),
        commandBar: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Button(
              onPressed: _isBusy ? null : _pickAudio,
              child: const Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(FluentIcons.music_in_collection),
                  SizedBox(width: 8),
                  Text('选择音频'),
                ],
              ),
            ),
            const SizedBox(width: 8),
            Button(
              onPressed: _isBusy ? null : _pickSrt,
              child: const Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(FluentIcons.text_document),
                  SizedBox(width: 8),
                  Text('导入 SRT'),
                ],
              ),
            ),
            const SizedBox(width: 8),
            IconButton(
              icon: const Icon(FluentIcons.settings),
              onPressed: widget.onOpenSettings,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(20, 4, 20, 20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            AnimatedInlineInfoBar(
              noticeId: _notice,
              title: _notice?.title,
              message: _notice?.message,
              severity: _notice?.severity,
              onClose: () => setState(() => _notice = null),
            ),
            Card(
              padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
              child: Row(
                children: [
                  Icon(
                    FluentIcons.music_note,
                    size: 26,
                    color: theme.accentColor,
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          audio == null ? '选择一段课程音频' : p.basename(audio.path),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: theme.typography.bodyStrong,
                        ),
                        const SizedBox(height: 3),
                        Text(
                          audio == null
                              ? '支持 mp3、m4a、wav，转写会在后台任务中心运行'
                              : '${_audioDuration == null ? '正在读取时长' : formatDuration(_audioDuration!)} · 英语识别 · 120 秒 VAD 切片',
                          style: theme.typography.caption,
                        ),
                      ],
                    ),
                  ),
                  if (_queueBusy) ...[
                    const ProgressRing(strokeWidth: 2),
                    const SizedBox(width: 10),
                    const Text('后台转写中'),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 12),
            Expanded(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Expanded(
                    child: Card(
                      padding: const EdgeInsets.fromLTRB(18, 16, 18, 18),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Row(
                            children: [
                              Text('字幕时间轴', style: theme.typography.subtitle),
                              const Spacer(),
                              Text(
                                _srtFile == null
                                    ? '可直接编辑 SRT'
                                    : p.basename(_srtFile!.path),
                                style: theme.typography.caption,
                              ),
                            ],
                          ),
                          const SizedBox(height: 12),
                          Expanded(
                            child: TextBox(
                              controller: _srtController,
                              expands: true,
                              maxLines: null,
                              minLines: null,
                              textAlignVertical: TextAlignVertical.top,
                              placeholder: '转写完成后字幕会出现在这里，也可以导入已有 SRT。\n\n00:00:00,000 --> 00:00:02,000\nEnglish transcript',
                              onChanged: (_) {
                                if (_srtFile != null) {
                                  setState(() => _srtFile = null);
                                }
                              },
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  SizedBox(
                    width: 320,
                    child: Card(
                      padding: const EdgeInsets.all(18),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Text('课程属性', style: theme.typography.subtitle),
                          const SizedBox(height: 16),
                          InfoLabel(
                            label: '课程标题',
                            child: TextBox(
                              controller: _titleController,
                              placeholder: '使用音频文件名',
                            ),
                          ),
                          const SizedBox(height: 16),
                          Text('转写', style: theme.typography.bodyStrong),
                          const SizedBox(height: 6),
                          Text(
                            '${widget.settings.cloudModel}\n${widget.settings.cloudConcurrency} 分段并发 · 英语输出',
                            style: theme.typography.caption,
                          ),
                          const SizedBox(height: 16),
                          Text('审查', style: theme.typography.bodyStrong),
                          const SizedBox(height: 6),
                          Text(
                            _srtController.text.trim().isEmpty
                                ? '等待字幕'
                                : '字幕已就绪，审查功能待接入',
                            style: theme.typography.caption,
                          ),
                          const Spacer(),
                          ValueListenableBuilder<TextEditingValue>(
                            valueListenable: _srtController,
                            builder: (context, value, _) {
                              final hasTranscript = value.text
                                  .trim()
                                  .isNotEmpty;
                              return Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [
                                  if (!hasTranscript)
                                    FilledButton(
                                      key: transcribeButtonKey,
                                      onPressed: _isBusy
                                          ? null
                                          : _startTranscriptionTask,
                                      child: const Text('开始后台转写'),
                                    )
                                  else
                                    Button(
                                      key: transcribeButtonKey,
                                      onPressed: _isBusy
                                          ? null
                                          : _startTranscriptionTask,
                                      child: const Text('重新转写'),
                                    ),
                                  const SizedBox(height: 8),
                                  if (hasTranscript)
                                    FilledButton(
                                      key: createPackageButtonKey,
                                      style: ButtonStyle(
                                        backgroundColor:
                                            WidgetStateProperty.resolveWith((
                                              states,
                                            ) {
                                              if (states.contains(
                                                WidgetState.disabled,
                                              )) {
                                                return Colors.grey[60];
                                              }
                                              if (states.contains(
                                                WidgetState.pressed,
                                              )) {
                                                return Colors.red.darker;
                                              }
                                              if (states.contains(
                                                WidgetState.hovered,
                                              )) {
                                                return Colors.red.lighter;
                                              }
                                              return Colors.red;
                                            }),
                                      ),
                                      onPressed: _isBusy
                                          ? null
                                          : _createPackage,
                                      child: Text(_creating ? '正在生成' : '生成精听包'),
                                    )
                                  else
                                    Button(
                                      key: createPackageButtonKey,
                                      onPressed: null,
                                      child: const Text('生成精听包'),
                                    ),
                                ],
                              );
                            },
                          ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

@visibleForTesting
const transcribeButtonKey = Key('transcribe-audio-button');

@visibleForTesting
const createPackageButtonKey = Key('create-ilp-button');

class TeacherNotice {
  const TeacherNotice._(this.severity, this.title, this.message);

  factory TeacherNotice.info(String title, String message) =>
      TeacherNotice._(InfoBarSeverity.info, title, message);

  factory TeacherNotice.success(String title, String message) =>
      TeacherNotice._(InfoBarSeverity.success, title, message);

  factory TeacherNotice.warning(String title, String message) =>
      TeacherNotice._(InfoBarSeverity.warning, title, message);

  factory TeacherNotice.error(String title, String message) =>
      TeacherNotice._(InfoBarSeverity.error, title, message);

  final InfoBarSeverity severity;
  final String title;
  final String message;
}

class SettingsPage extends StatefulWidget {
  const SettingsPage({
    super.key,
    required this.settings,
    required this.onSettingsChanged,
    required this.onBack,
    required this.onShowEula,
  });

  final AppSettings settings;
  final Future<void> Function(AppSettings settings) onSettingsChanged;
  final VoidCallback onBack;
  final Future<void> Function() onShowEula;

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends State<SettingsPage> {
  late AppSettings _settings;
  late final TextEditingController _cloudBaseUrlController;
  late final TextEditingController _cloudEndpointController;
  late final TextEditingController _cloudModelController;
  late final TextEditingController _cloudApiKeyController;
  late final TextEditingController _cloudTimeoutController;
  late final TextEditingController _cloudConcurrencyController;
  var _saving = false;
  var _updating = false;
  var _clearingCache = false;
  var _exportingApi = false;
  var _importingApi = false;

  @override
  void initState() {
    super.initState();
    _settings = widget.settings;
    _cloudBaseUrlController = TextEditingController();
    _cloudEndpointController = TextEditingController();
    _cloudModelController = TextEditingController();
    _cloudApiKeyController = TextEditingController();
    _cloudTimeoutController = TextEditingController();
    _cloudConcurrencyController = TextEditingController();
    _syncControllers(widget.settings);
  }

  @override
  void didUpdateWidget(covariant SettingsPage oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.settings != widget.settings) {
      _settings = widget.settings;
      _syncControllers(widget.settings);
    }
  }

  @override
  void dispose() {
    _cloudBaseUrlController.dispose();
    _cloudEndpointController.dispose();
    _cloudModelController.dispose();
    _cloudApiKeyController.dispose();
    _cloudTimeoutController.dispose();
    _cloudConcurrencyController.dispose();
    super.dispose();
  }

  void _syncControllers(AppSettings settings) {
    _cloudBaseUrlController.text = settings.cloudBaseUrl;
    _cloudEndpointController.text = settings.cloudEndpoint;
    _cloudModelController.text = settings.cloudModel;
    _cloudApiKeyController.text = settings.cloudApiKey;
    _cloudTimeoutController.text = settings.cloudTimeoutSeconds.toString();
    _cloudConcurrencyController.text = settings.cloudConcurrency.toString();
  }

  AppSettings _settingsFromControllers() {
    final timeoutSeconds =
        int.tryParse(_cloudTimeoutController.text.trim()) ?? 180;
    final concurrency =
        (int.tryParse(_cloudConcurrencyController.text.trim()) ?? 10).clamp(
          1,
          10,
        );
    return _settings.copyWith(
      asrProvider: AsrProviderKind.cloud,
      cloudBaseUrl: _cloudBaseUrlController.text.trim(),
      cloudEndpoint: _cloudEndpointController.text.trim(),
      cloudModel: _cloudModelController.text.trim(),
      cloudApiKey: _cloudApiKeyController.text.trim(),
      cloudTimeoutSeconds: timeoutSeconds.clamp(30, 1800),
      cloudConcurrency: concurrency,
      cloudLanguage: 'en',
      translateChineseToEnglish: true,
    );
  }

  void _showSettingsNotice(
    String title,
    String message, {
    InfoBarSeverity severity = InfoBarSeverity.success,
  }) {
    StackedInfoBarScope.of(context)
        .show(title: title, message: message, severity: severity);
  }

  Future<String?> _requestPassword({required bool confirm}) async {
    final password = TextEditingController();
    final repeated = TextEditingController();
    String? error;
    final result = await showSpringDialog<String>(
      context: context,
      builder: (dialogContext) => StatefulBuilder(
        builder: (context, setDialogState) => ContentDialog(
          title: Text(confirm ? '设置配置包密码' : '输入配置包密码'),
          content: SizedBox(
            width: 360,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                PasswordBox(
                  controller: password,
                  placeholder: '密码',
                  autofocus: true,
                ),
                if (confirm) ...[
                  const SizedBox(height: 12),
                  PasswordBox(controller: repeated, placeholder: '再次输入密码'),
                ],
                if (error != null) ...[
                  const SizedBox(height: 10),
                  Align(
                    alignment: Alignment.centerLeft,
                    child: Text(error!, style: TextStyle(color: Colors.red)),
                  ),
                ],
              ],
            ),
          ),
          actions: [
            Button(
              onPressed: () => Navigator.pop(dialogContext),
              child: const Text('取消'),
            ),
            FilledButton(
              onPressed: () {
                if (password.text.isEmpty) {
                  setDialogState(() => error = '密码不能为空');
                  return;
                }
                if (confirm && password.text != repeated.text) {
                  setDialogState(() => error = '两次输入的密码不一致');
                  return;
                }
                Navigator.pop(dialogContext, password.text);
              },
              child: Text(confirm ? '导出' : '导入'),
            ),
          ],
        ),
      ),
    );
    password.dispose();
    repeated.dispose();
    return result;
  }

  Future<void> _exportApiConfiguration() async {
    final password = await _requestPassword(confirm: true);
    if (password == null || !mounted) return;
    setState(() => _exportingApi = true);
    try {
      final bytes = const ApiConfigurationArchive().export(
        _settingsFromControllers(),
        password: password,
      );
      final output = await FilePicker.saveFile(
        dialogTitle: '导出 API 配置',
        fileName: 'Intensive-Listening-API-配置.zip',
        bytes: bytes,
        type: FileType.custom,
        allowedExtensions: const ['zip'],
      );
      if (output != null && mounted) {
        _showSettingsNotice('API 配置已导出', '配置包已使用 AES-256 加密。');
      }
    } on ApiConfigurationArchiveException catch (error) {
      if (mounted) {
        _showSettingsNotice(
          '无法导出配置',
          error.message,
          severity: InfoBarSeverity.error,
        );
      }
    } finally {
      if (mounted) setState(() => _exportingApi = false);
    }
  }

  Future<void> _importApiConfiguration() async {
    final selected = await pickFileWith(
      allowedExtensions: const ['zip'],
      dialogTitle: '导入 API 配置',
    );
    if (selected == null || !mounted) return;
    final password = await _requestPassword(confirm: false);
    if (password == null || !mounted) return;
    setState(() => _importingApi = true);
    try {
      final settings = const ApiConfigurationArchive().import(
        await File(selected).readAsBytes(),
        password: password,
        current: _settings,
      );
      await widget.onSettingsChanged(settings);
      if (!mounted) return;
      setState(() {
        _settings = settings;
        _syncControllers(settings);
      });
      _showSettingsNotice('API 配置已导入', '全部 API 字段已保存到 AppData。');
    } on ApiConfigurationArchiveException catch (error) {
      if (mounted) {
        _showSettingsNotice(
          '无法导入配置',
          error.message,
          severity: InfoBarSeverity.error,
        );
      }
    } finally {
      if (mounted) setState(() => _importingApi = false);
    }
  }

  Future<Map<String, dynamic>> _mcpDiscovery() async {
    final directory = await intensiveListeningDataDirectory();
    final file = File(p.join(directory.path, 'mcp', 'app-private-api.json'));
    if (!await file.exists()) {
      throw const AppPrivateApiException('mcp_not_running', '请先启用 MCP 并保存设置。');
    }
    final value = jsonDecode(await file.readAsString());
    if (value is! Map<String, dynamic> ||
        value['mcpUrl'] is! String ||
        value['token'] is! String ||
        value['toolCallUrl'] is! String) {
      throw const AppPrivateApiException(
        'mcp_discovery_invalid',
        'MCP 连接信息不可用。',
      );
    }
    return value;
  }

  Future<String> _mcpConfiguration() async {
    final discovery = await _mcpDiscovery();
    return const JsonEncoder.withIndent('  ').convert({
      'mcpServers': {
        'intensive-listening': {'type': 'http', 'url': discovery['mcpUrl']},
      },
    });
  }

  Future<String> _agentPrompt() async {
    final discovery = await _mcpDiscovery();
    final configuration = await _mcpConfiguration();
    return '''# Intensive Listening 课程制作智能体

你的任务是把用户提供的试卷 Word 与听力音频整理成可直接学习的精听课程。应用负责保存工程、ASR、SRT 解析与词索引；你负责理解试卷、组织听力材料与小题、设置挖空并完成课程。

## 连接

1. 保持 Intensive Listening 运行，并在设置中启用 MCP。
2. 优先使用下面的标准 MCP 配置。连接初始化会携带 `event=Agent`，应用随后进入“智能体正在工作”状态。
3. 连接成功后，必须先读取初始化响应中 `instructions` 指向的 `HELP.md`，再调用任何课程制作工具。
4. 按照 `HELP.md` 的当前版本执行，并首先调用 `intensive_listening_status`。返回 `event=Agent` 后开始制作；若会话回到 `event=User`，停止写入并提示用户重新连接。

```json
$configuration
```

## 制作依据

- 试卷 Word 是题号、题目文本、选项内容和题目顺序的主要依据。
- `get_course_project` / `read_project_srt` 返回的 cue、时间、`isMarker` 和 `words` 是音频定位与索引的唯一依据。轮询只读取工程摘要；字幕使用分页读取，避免反复传输整份数据。
- 如有听力原文，逐段对照原文和 SRT 的段落、播报、对话与重复朗读结构；校正有依据的断词、粘词、空格和误识别，使每个词有意义，连起来符合正常语义与语流。原文缺失或与音频冲突时依据实际音频，不凭题目补造台词。
- 需要初稿时显式调用 `auto_plan_questions`；结合试卷和原文检查后再保留、移动、合并或替换。
- 数据采用“听力材料 → 多道小题”两层结构。一段对话或独白的 cue 只归属一个材料，同一材料可包含一道或多道小题；与任何材料无关的内容保持未归题，在播放器中作为题前提示出现。

## 标准制作流程

### 1. 整理输入

识别用户提供的 DOCX 试卷和音频绝对路径。创建或恢复工程后，调用 `import_exam_document` 将 DOCX 复制到工程并生成 UTF-8 文本，再分页调用 `read_exam_text` 读取试卷内容。依据返回文本形成有序题目清单，至少记录题号和完整题目内容；选项影响理解时，将必要选项一并写入题目文本。以试卷标题或音频文件名生成清晰的课程名称。

### 2. 选择工程

调用 `list_course_projects`。存在与本次试卷、音频对应的工程时，调用 `get_course_project` 继续该工程；仅在没有对应工程时调用 `create_course_project`。如果 `examDocument` 为空，调用 `import_exam_document`；已有试卷时优先调用 `read_exam_text` 复用项目文本。删除工程必须来自用户的明确要求。

### 3. 准备转写

读取工程状态：

- `hasAudio=false`：调用 `import_project_media` 绑定音频。
- `hasTranscript=true`：直接复用现有字幕。
- 已进入转写阶段：轮询 `get_course_project(fields: [])`，等待 `hasTranscript=true`。
- 尚未开始且没有字幕：调用一次 `start_project_asr`，随后轮询工程状态。

转写在应用后台执行，完成后 SRT 自动写入工程。轮询建议使用逐步增加的间隔；同一工程已有转写任务时不重复提交。

### 4. 读取应用分析结果

字幕就绪后分页调用 `read_project_srt(offset, limit, includeSrt: false)`，读取每个 cue 的：

- `index`、`startMs`、`endMs`、`text`
- `sectionIndex`、`isMarker`
- `words[]` 中由应用生成的单词 `index` 与 `text`

后续写入只能使用这次读取到的索引。项目发生变化或接口报告索引无效时，重新读取后再规划。
逐句检查英文词是否完整、词间空格是否正确、相邻句能否自然连读，以及两遍朗读的对应句是否完全一致。单句转写有误时使用 `set_cue_text` 修正并重新读取受影响的词索引；整份 SRT 更新使用 `import_project_srt(mode: auto)`，并检查返回的 `preserved` 与 `cleared` 统计。

### 5. 校正材料与小题

按试卷顺序，将题目与原文的语义、时间顺序和答案信息对齐：

- 小题标题写入试卷中的真实题目内容，保持题号顺序。
- 以一段完整对话或独白建立材料并选择对应 cue；例如“听下面一段对话，回答第 6 和第 7 小题”应建立一个材料，其下包含第 6、7 两道小题。
- “听下面两段录音……”一类播报保留在原始 cue 流或材料元数据中，不单独建立页面标题、章节或题目。
- 自动规划会规范 `1 2 - 1 3`、全角数字和中文数字；最终题号必须以试卷为准。
- `isMarker=true`、`Text XXX`、考试说明、章节播报、倒计时和纯旁白保持未归题。
- 同一句不能跨材料重复分配。同一材料内的小题共享整段音频，切换小题不应改变播放位置。
- 对单题材料尤其检查是否播放两遍：两遍归属同一材料和同一道题，在 `apply_question_plan` 的该材料中用 `repeatedCueIndexes` 标记第二遍 cue。对应的两句字幕文本应完全一致；先修正 SRT 再提交题目计划。音频只播放一遍时保持该字段为空。

需要应用初稿时调用 `auto_plan_questions`；有试卷时优先一次调用 `apply_question_plan`，保证题目顺序和 cue 归属原子更新。仅做局部修订时使用 `add_question_group`、`edit_question_group` 或 `delete_question_group`。

### 6. 设置挖空

使用 `words[]` 返回的索引选择挖空。优先选择能训练听辨且承载信息的内容词、数字、专有名词和关键短语，保持句子仍可理解；标点不参与索引。无法确定索引时先调用 `tokenize_lesson_text`。重复出现的同一句采用一致的挖空词。

完整设置使用 `apply_cloze_plan`，小范围修改使用 `set_sentence_cloze`。提交前核对每个 `wordIndexes` 都属于对应 cue。

### 7. 复核与交付

写入后重新调用 `get_course_project`，逐项确认：

- 题目数量、文本和顺序与试卷一致。
- 材料 cue 不重复，提示内容未进入材料。
- 重复朗读的第二遍 cue 已标记，且两遍对应句的字幕完全一致。
- 挖空索引对应预期单词。

随后调用 `validate_course_project`。`valid=true` 后，默认调用 `add_project_to_playback`，让课程直接出现在学生端。用户要求文件交付并给出输出路径时，选择 `export_ilp`；需要可独立运行的 Windows 课程时选择 `export_standalone_player`。

只有在试卷内容缺失、多个分组方案同样成立且会实质改变课程时才向用户确认。其余情况依据试卷、时间顺序和语义完成制作。

## 完成报告

简要报告课程名称、工程 ID、题目数量、设置挖空的句子数和交付结果。存在无法可靠对齐的题目时，列出题号并说明需要用户复核的位置。

## HTTP Tool Call 备选方式

标准 MCP 不可用时，可调用同一主进程提供的 HTTP Tool Call：

- 请求头：`Authorization: Bearer ${discovery['token']}`
- 建立会话：`POST http://127.0.0.1:$appMcpPort/v1/agent/connect`
```json
{"event":"Agent"}
```
- 读取工具：`GET http://127.0.0.1:$appMcpPort/v1/tools`
- 调用工具：`POST ${discovery['toolCallUrl']}`
```json
{"name":"list_course_projects","arguments":{}}
```
- 结束会话：`POST http://127.0.0.1:$appMcpPort/v1/agent/disconnect`，请求体为 `{}`。

标准 MCP 工作完成后，请总结已完成的项目变更，并提示用户在应用内点击“强制断开”以返回 User 状态。
''';
  }

  Future<void> _copyMcpCommand() async {
    try {
      await Clipboard.setData(ClipboardData(text: await _mcpConfiguration()));
      if (mounted) _showSettingsNotice('MCP 配置已复制', '可粘贴到 AI CLI 配置中。');
    } catch (error) {
      if (mounted) {
        _showSettingsNotice(
          'MCP 配置不可用',
          '$error',
          severity: InfoBarSeverity.error,
        );
      }
    }
  }

  Future<void> _copyAgentPrompt() async {
    try {
      await Clipboard.setData(ClipboardData(text: await _agentPrompt()));
      if (mounted) _showSettingsNotice('提示.md 已复制', '可直接发送给 AI CLI。');
    } catch (error) {
      if (mounted) {
        _showSettingsNotice('提示不可用', '$error', severity: InfoBarSeverity.error);
      }
    }
  }

  Future<void> _exportAgentPrompt() async {
    String prompt;
    try {
      prompt = await _agentPrompt();
    } catch (error) {
      if (mounted) {
        _showSettingsNotice('提示不可用', '$error', severity: InfoBarSeverity.error);
      }
      return;
    }
    final output = await FilePicker.saveFile(
      dialogTitle: '生成提示.md',
      fileName: '提示.md',
      bytes: Uint8List.fromList(utf8.encode(prompt)),
      type: FileType.custom,
      allowedExtensions: const ['md'],
    );
    if (output != null && mounted) {
      _showSettingsNotice('提示.md 已生成', '文件可提供给 AI CLI 使用。');
    }
  }

  Future<void> _saveSettings() async {
    final settings = _settingsFromControllers();
    setState(() => _saving = true);
    try {
      await widget.onSettingsChanged(settings);
      if (!mounted) return;
      setState(() {
        _settings = settings;
        _cloudTimeoutController.text = settings.cloudTimeoutSeconds.toString();
        _cloudConcurrencyController.text = settings.cloudConcurrency.toString();
      });
      _showSettingsNotice('设置已保存', '应用功能和教师端转写配置已经更新。');
    } catch (error) {
      if (mounted) {
        _showSettingsNotice(
          '设置未完全应用',
          '$error',
          severity: InfoBarSeverity.error,
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _chooseUpdate() async {
    final selected = await pickFileWith(
      allowedExtensions: const ['zip'],
      dialogTitle: '选择新版本更新包',
    );
    if (selected == null || !mounted) return;
    setState(() => _updating = true);
    try {
      final service = const AppUpdateService();
      final update = await service.stage(File(selected));
      if (!mounted) return;
      final accepted = await showSpringDialog<bool>(
        context: context,
        builder: (dialogContext) => ContentDialog(
          title: Text('更新到 ${update.version}'),
          content: const Text('应用将关闭并打开新版本安装程序。请先完成正在进行的转写任务。'),
          actions: [
            Button(
              onPressed: () => Navigator.pop(dialogContext, false),
              child: const Text('取消'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(dialogContext, true),
              child: const Text('开始更新'),
            ),
          ],
        ),
      );
      if (accepted != true) return;
      await service.install(update);
      exit(0);
    } catch (error) {
      if (mounted) {
        _showSettingsNotice('更新未完成', '$error', severity: InfoBarSeverity.error);
      }
    } finally {
      if (mounted) setState(() => _updating = false);
    }
  }

  Future<void> _showAbout() async {
    await showSpringDialog<void>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('关于 Intensive Listening'),
        content: Text(
          '版本 ${const AppUpdateService().currentVersion}\n'
          '数据格式 $appDataSchemaVersion\n'
          'By Luyii',
        ),
        actions: [
          Button(
            onPressed: () async {
              Navigator.pop(dialogContext);
              await widget.onShowEula();
            },
            child: const Text('用户协议'),
          ),
          Button(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('关闭'),
          ),
        ],
      ),
    );
  }

  Future<void> _openDataDirectory() async {
    try {
      final directory = await intensiveListeningDataDirectory();
      await directory.create(recursive: true);
      if (Platform.isWindows) {
        await Process.start('explorer.exe', [directory.path]);
      } else if (Platform.isMacOS) {
        await Process.start('open', [directory.path]);
      }
      if (mounted) _showSettingsNotice('数据目录已打开', directory.path);
    } catch (error) {
      if (mounted) {
        _showSettingsNotice(
          '无法打开数据目录',
          '$error',
          severity: InfoBarSeverity.error,
        );
      }
    }
  }

  Future<void> _clearCache() async {
    setState(() => _clearingCache = true);
    try {
      final bytes = await clearIntensiveListeningCache();
      if (mounted) {
        _showSettingsNotice(
          '缓存已清理',
          '已释放 ${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB。',
        );
      }
    } catch (error) {
      if (mounted) {
        _showSettingsNotice(
          '缓存清理失败',
          '$error',
          severity: InfoBarSeverity.error,
        );
      }
    } finally {
      if (mounted) setState(() => _clearingCache = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return ScaffoldPage(
      header: PageHeader(
        leading: Padding(
          padding: const EdgeInsets.only(left: 12, right: 8),
          child: Tooltip(
            message: '返回',
            child: IconButton(
              key: settingsBackButtonKey,
              icon: const Icon(FluentIcons.back),
              onPressed: widget.onBack,
            ),
          ),
        ),
        title: const Text('设置'),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 4, 24, 24),
        child: Align(
          alignment: Alignment.topCenter,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 1040),
            child: ListView(
              children: [
                Text('应用与集成', style: theme.typography.subtitle),
                const SizedBox(height: 12),
                Column(
                  children: [
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.color),
                        title: const Text('外观主题'),
                        subtitle: const Text('控制应用界面的明暗外观。'),
                        trailing: ComboBox<String>(
                          value: _settings.themeMode,
                          items: const [
                            ComboBoxItem(value: 'system', child: Text('跟随系统')),
                            ComboBoxItem(value: 'light', child: Text('浅色模式')),
                            ComboBoxItem(value: 'dark', child: Text('深色模式')),
                          ],
                          onChanged: (value) {
                            if (value != null) {
                              final updated = _settings.copyWith(
                                themeMode: value,
                              );
                              setState(() => _settings = updated);
                              unawaited(widget.onSettingsChanged(updated));
                            }
                          },
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: _SettingsToggleRow(
                        icon: FluentIcons.open_file,
                        title: '关联文件格式',
                        description: '双击 .ilp 精听包直接进入播放界面。',
                        checked: _settings.fileAssociationEnabled,
                        onChanged: (value) => setState(
                          () => _settings = _settings.copyWith(
                            fileAssociationEnabled: value,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: _SettingsToggleRow(
                        icon: FluentIcons.robot,
                        title: 'MCP',
                        description: '允许本机智能体连接课程制作工具；连接后需要在应用内断开。',
                        checked: _settings.mcpEnabled,
                        onChanged: (value) => setState(
                          () =>
                              _settings = _settings.copyWith(mcpEnabled: value),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: _SettingsToggleRow(
                        icon: FluentIcons.forward,
                        title: '跳过题前提示',
                        description: '首次打开课程时定位到第一题前并暂停，保留已有播放进度。',
                        checked: _settings.skipOpeningPrompts,
                        onChanged: (value) => setState(
                          () => _settings = _settings.copyWith(
                            skipOpeningPrompts: value,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.font),
                        title: const Text('字幕字体大小'),
                        subtitle: const Text('调整播放页逐句列表的文字大小。'),
                        trailing: SizedBox(
                          width: 240,
                          child: Row(
                            children: [
                              Expanded(
                                child: Slider(
                                  value: _settings.transcriptFontSize
                                      .toDouble(),
                                  min: 14,
                                  max: 28,
                                  divisions: 14,
                                  onChanged: (value) => setState(
                                    () => _settings = _settings.copyWith(
                                      transcriptFontSize: value.round(),
                                    ),
                                  ),
                                ),
                              ),
                              const SizedBox(width: 12),
                              Text('${_settings.transcriptFontSize}'),
                            ],
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.folder_open),
                        title: const Text('数据目录'),
                        subtitle: const Text('打开保存课程、制作工程和个人设置的文件夹。'),
                        trailing: Button(
                          onPressed: _openDataDirectory,
                          child: const Text('打开'),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.delete),
                        title: const Text('缓存'),
                        subtitle: const Text('释放更新包及临时文件占用的空间；课程和制作数据保留。'),
                        trailing: Button(
                          onPressed: _clearingCache || _updating
                              ? null
                              : _clearCache,
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              if (_clearingCache) ...[
                                const SizedBox(
                                  width: 14,
                                  height: 14,
                                  child: ProgressRing(strokeWidth: 2),
                                ),
                                const SizedBox(width: 8),
                              ],
                              Text(_clearingCache ? '清理中…' : '清理'),
                            ],
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.download),
                        title: const Text('应用更新'),
                        subtitle: const Text('选择官方更新 ZIP，校验后启动安装程序。'),
                        trailing: Button(
                          onPressed: _updating || _clearingCache
                              ? null
                              : _chooseUpdate,
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              if (_updating) ...[
                                const SizedBox(
                                  width: 14,
                                  height: 14,
                                  child: ProgressRing(strokeWidth: 2),
                                ),
                                const SizedBox(width: 8),
                              ],
                              Text(_updating ? '准备中…' : '选择更新包'),
                            ],
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    _SettingsCard(
                      child: ListTile(
                        leading: const Icon(FluentIcons.info),
                        title: const Text('关于'),
                        subtitle: const Text('查看应用版本、作者与最终用户许可协议。'),
                        trailing: Button(
                          onPressed: _showAbout,
                          child: const Text('查看'),
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 20),
                Expander(
                  initiallyExpanded: false,
                  header: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 10),
                    child: Row(
                      children: [
                        const Icon(FluentIcons.cloud, size: 21),
                        const SizedBox(width: 14),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'API 设置',
                                style: theme.typography.bodyStrong,
                              ),
                              const SizedBox(height: 2),
                              Text(
                                '配置云端转写服务、请求并发和超时；密钥保存在本机。',
                                style: theme.typography.caption,
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  content: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Align(
                        alignment: Alignment.centerRight,
                        child: Wrap(
                          spacing: 8,
                          runSpacing: 8,
                          children: [
                            Button(
                              onPressed: _importingApi || _exportingApi
                                  ? null
                                  : _importApiConfiguration,
                              child: Text(_importingApi ? '导入中…' : '导入'),
                            ),
                            Button(
                              onPressed: _exportingApi || _importingApi
                                  ? null
                                  : _exportApiConfiguration,
                              child: Text(_exportingApi ? '导出中…' : '导出'),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(height: 12),
                      InfoLabel(
                        label: 'API Endpoint',
                        child: TextBox(controller: _cloudBaseUrlController),
                      ),
                      const SizedBox(height: 12),
                      InfoLabel(
                        label: 'Path',
                        child: TextBox(controller: _cloudEndpointController),
                      ),
                      const SizedBox(height: 12),
                      InfoLabel(
                        label: 'Name',
                        child: TextBox(controller: _cloudModelController),
                      ),
                      const SizedBox(height: 12),
                      InfoLabel(
                        label: 'Key',
                        child: PasswordBox(
                          controller: _cloudApiKeyController,
                          placeholder: '输入云端转写密钥',
                        ),
                      ),
                      const SizedBox(height: 12),
                      Row(
                        children: [
                          Expanded(
                            child: InfoLabel(
                              label: '分段并发',
                              child: NumberBox(
                                value: double.tryParse(
                                  _cloudConcurrencyController.text,
                                ),
                                min: 1,
                                max: 10,
                                mode: SpinButtonPlacementMode.compact,
                                onChanged: (value) {
                                  if (value != null) {
                                    _cloudConcurrencyController.text = value
                                        .round()
                                        .toString();
                                  }
                                },
                              ),
                            ),
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: InfoLabel(
                              label: '单段超时（秒）',
                              child: TextBox(
                                controller: _cloudTimeoutController,
                              ),
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 12),
                      const InfoBar(
                        title: Text('音频处理策略'),
                        content: Text(
                          'VAD 在静音处切片，每段最长 120 秒，确保请求低于 10 MB；并发与请求启动速率均限制为 10 QPS。',
                        ),
                        severity: InfoBarSeverity.info,
                        isLong: true,
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 20),
                Text('MCP 与智能体', style: theme.typography.subtitle),
                const SizedBox(height: 4),
                Text(
                  '标准 MCP 与 HTTP Tool Call 共用应用内的 Agent 会话和制作操作。',
                  style: theme.typography.caption,
                ),
                const SizedBox(height: 12),
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  children: [
                    Button(
                      onPressed: _copyMcpCommand,
                      child: const Text('复制 MCP 配置'),
                    ),
                    Button(
                      onPressed: _copyAgentPrompt,
                      child: const Text('复制 提示.md'),
                    ),
                    Button(
                      onPressed: _exportAgentPrompt,
                      child: const Text('生成 提示.md'),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Expander(
                  initiallyExpanded: false,
                  header: Row(
                    children: [
                      const Icon(FluentIcons.help, size: 16),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          '快速使用指南',
                          style: theme.typography.bodyStrong,
                        ),
                      ),
                      Text('3 步', style: theme.typography.caption),
                    ],
                  ),
                  content: const Column(
                    children: [
                      ListTile(
                        leading: Icon(FluentIcons.toggle_right),
                        title: Text('1. 启用服务'),
                        subtitle: Text('打开上方 MCP 开关，然后保存设置。'),
                      ),
                      ListTile(
                        leading: Icon(FluentIcons.copy),
                        title: Text('2. 连接 AI 客户端'),
                        subtitle: Text('复制 MCP 配置，粘贴到 WorkBuddy 或 AI CLI。'),
                      ),
                      ListTile(
                        leading: Icon(FluentIcons.robot),
                        title: Text('3. 开始制作'),
                        subtitle: Text('把试卷 Word 和音频交给智能体；完成后课程可直接加入学生端播放。'),
                      ),
                      Padding(
                        padding: EdgeInsets.fromLTRB(12, 4, 12, 8),
                        child: Align(
                          alignment: Alignment.centerLeft,
                          child: Text('连接期间应用进入智能体工作状态；强制断开后恢复手动操作。'),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 24),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    FilledButton(
                      key: saveSettingsButtonKey,
                      onPressed: _saving ? null : _saveSettings,
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          if (_saving)
                            const SizedBox(
                              width: 16,
                              height: 16,
                              child: ProgressRing(strokeWidth: 2),
                            )
                          else
                            const Icon(FluentIcons.save),
                          const SizedBox(width: 8),
                          const Text('保存设置'),
                        ],
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _SettingsCard extends StatelessWidget {
  const _SettingsCard({required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) => Card(
    padding: EdgeInsets.zero,
    child: ConstrainedBox(
      constraints: const BoxConstraints(minHeight: 76),
      child: child,
    ),
  );
}

class _SettingsToggleRow extends StatelessWidget {
  const _SettingsToggleRow({
    required this.icon,
    required this.title,
    required this.description,
    required this.checked,
    required this.onChanged,
  });

  final IconData icon;
  final String title;
  final String description;
  final bool checked;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
      child: Row(
        children: [
          Icon(icon, size: 22),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: theme.typography.bodyStrong),
                const SizedBox(height: 2),
                Text(description, style: theme.typography.caption),
              ],
            ),
          ),
          const SizedBox(width: 20),
          ToggleSwitch(checked: checked, onChanged: onChanged),
        ],
      ),
    );
  }
}

@visibleForTesting
const saveSettingsButtonKey = Key('save-settings-button');

@visibleForTesting
const settingsBackButtonKey = Key('settings-back-button');

class _StudentHome extends StatelessWidget {
  const _StudentHome({
    required this.lessons,
    required this.progress,
    required this.onOpen,
    required this.onRemove,
    required this.onOpenAudio,
    required this.onImport,
    this.busy = false,
    this.openingLessonId,
    this.deletingLessonId,
  });

  final List<ImportedLesson> lessons;
  final Map<String, LessonProgress> progress;
  final ValueChanged<ImportedLesson> onOpen;
  final ValueChanged<ImportedLesson> onRemove;
  final VoidCallback onOpenAudio;
  final VoidCallback onImport;
  final bool busy;
  final String? openingLessonId;
  final String? deletingLessonId;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    if (lessons.isEmpty) {
      return EmptyState(
        icon: FluentIcons.open_file,
        title: '打开音频或精听包',
        message: '直接播放常见音频，或导入带有题目与挖空练习的 .ilp 精听包。',
        action: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            FilledButton(
              onPressed: busy ? null : onOpenAudio,
              child: const Text('打开音频'),
            ),
            const SizedBox(width: 8),
            Button(
              onPressed: busy ? null : onImport,
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (busy) ...[
                    const SizedBox(
                      width: 14,
                      height: 14,
                      child: ProgressRing(strokeWidth: 2),
                    ),
                    const SizedBox(width: 8),
                  ],
                  const Text('导入精听包'),
                ],
              ),
            ),
          ],
        ),
      );
    }

    final ordered = [...lessons]
      ..sort((left, right) {
        final leftDate = progress[left.manifest.packageUuid]?.lastOpenedAt;
        final rightDate = progress[right.manifest.packageUuid]?.lastOpenedAt;
        return (rightDate ?? DateTime.fromMillisecondsSinceEpoch(0)).compareTo(
          leftDate ?? DateTime.fromMillisecondsSinceEpoch(0),
        );
      });
    return Padding(
      padding: const EdgeInsets.fromLTRB(24, 4, 24, 24),
      child: Align(
        alignment: Alignment.topCenter,
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 1200),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text('最近课程', style: theme.typography.subtitle),
              const SizedBox(height: 12),
              Expanded(
                child: ListView.separated(
                  itemCount: ordered.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 8),
                  itemBuilder: (context, index) {
                    final lesson = ordered[index];
                    final record = progress[lesson.manifest.packageUuid];
                    final fraction = lesson.manifest.duration > Duration.zero
                        ? (record?.position.inMilliseconds ?? 0) /
                              lesson.manifest.duration.inMilliseconds
                        : 0.0;
                    return Card(
                      padding: EdgeInsets.zero,
                      child: Row(
                        children: [
                          Expanded(
                            child: ListTile(
                              leading: openingLessonId == lesson.id
                                  ? const SizedBox(
                                      width: 18,
                                      height: 18,
                                      child: ProgressRing(strokeWidth: 2),
                                    )
                                  : const Icon(FluentIcons.music_note),
                              title: Text(lesson.manifest.title),
                              subtitle: Column(
                                crossAxisAlignment: CrossAxisAlignment.stretch,
                                children: [
                                  const SizedBox(height: 4),
                                  Text(
                                    '${formatDuration(record?.position ?? Duration.zero)} / '
                                    '${formatDuration(lesson.manifest.duration)} · '
                                    '版本 ${lesson.manifest.packageVersion}',
                                  ),
                                  const SizedBox(height: 7),
                                  SpringProgressBar(
                                    value: (fraction * 100).clamp(0, 100),
                                  ),
                                ],
                              ),
                              onPressed:
                                  openingLessonId != null ||
                                      deletingLessonId != null
                                  ? null
                                  : () => onOpen(lesson),
                            ),
                          ),
                          const Divider(direction: Axis.vertical, size: 40),
                          Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 8),
                            child: Tooltip(
                              message: '移除课程',
                              child: IconButton(
                                key: studentLessonRemoveButtonKey(lesson.id),
                                icon: deletingLessonId == lesson.id
                                    ? const SizedBox(
                                        width: 14,
                                        height: 14,
                                        child: ProgressRing(strokeWidth: 2),
                                      )
                                    : const Icon(FluentIcons.delete),
                                onPressed:
                                    deletingLessonId != null ||
                                        openingLessonId != null
                                    ? null
                                    : () => onRemove(lesson),
                              ),
                            ),
                          ),
                        ],
                      ),
                    );
                  },
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class PlayerSection extends StatelessWidget {
  const PlayerSection({
    super.key,
    required this.title,
    required this.cueCount,
    required this.hasTranscript,
    this.questionTitles = const [],
    this.manifest,
    this.sourcePath,
    required this.showSubtitles,
    required this.duration,
    required this.position,
    required this.playing,
    this.singleSentenceLoop = false,
    required this.onTogglePlayback,
    required this.onSeek,
    required this.onStepSentence,
    this.onStepQuestion,
    required this.onSingleSentenceLoopChanged,
    required this.onShowSubtitlesChanged,
    this.showAllCloze = false,
    this.onShowAllCloze,
  });

  final String title;
  final int cueCount;
  final bool hasTranscript;
  final List<String> questionTitles;
  final IlpManifest? manifest;
  final String? sourcePath;
  final bool showSubtitles;
  final Duration duration;
  final Duration position;
  final bool playing;
  final bool singleSentenceLoop;
  final Future<void> Function() onTogglePlayback;
  final Future<void> Function(Duration position) onSeek;
  final Future<void> Function(int delta) onStepSentence;
  final Future<void> Function(int delta)? onStepQuestion;
  final ValueChanged<bool> onSingleSentenceLoopChanged;
  final ValueChanged<bool> onShowSubtitlesChanged;
  final bool showAllCloze;
  final ValueChanged<bool>? onShowAllCloze;

  Future<void> _showFileInfo(BuildContext context) async {
    final file = sourcePath == null ? null : File(sourcePath!);
    final size = file != null && await file.exists()
        ? await file.length()
        : null;
    if (!context.mounted) return;
    final details = <String>[
      '名称：$title',
      '类型：${manifest == null ? '普通音频' : 'ILP 精听包'}',
      '时长：${formatDuration(duration)}',
      if (manifest != null) ...[
        '精听包版本：${manifest!.packageVersion}',
        '格式版本：${manifest!.formatVersion}',
        '包 UUID：${manifest!.packageUuid}',
        '字幕：$cueCount 句',
      ],
      if (size != null) '音频大小：${(size / (1024 * 1024)).toStringAsFixed(2)} MB',
      if (sourcePath != null) '文件位置：$sourcePath',
    ];
    await showSpringDialog<void>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: const Text('文件信息'),
        content: SizedBox(
          width: 500,
          child: SelectableText(details.join('\n')),
        ),
        actions: [
          Button(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('关闭'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    return LayoutBuilder(
      builder: (context, bounds) => SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 20),
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: bounds.maxHeight - 36),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 640),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Card(
                    padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(
                        minHeight: 120,
                        maxHeight: 300,
                      ),
                      child: SingleChildScrollView(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            Text(
                              questionTitles.length == 1 &&
                                      questionTitles.first == '未设置'
                                  ? '当前题目'
                                  : '当前材料 · ${questionTitles.length} 题',
                              style: theme.typography.caption,
                            ),
                            const SizedBox(height: 12),
                            for (
                              var index = 0;
                              index < questionTitles.length;
                              index++
                            ) ...[
                              if (index > 0) const SizedBox(height: 12),
                              Text(
                                questionTitles[index],
                                style: theme.typography.subtitle?.copyWith(
                                  fontSize: 20,
                                  height: 1.4,
                                ),
                              ),
                            ],
                          ],
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(height: 24),
                  Text(
                    title,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    textAlign: TextAlign.center,
                    style: theme.typography.title,
                  ),
                  const SizedBox(height: 6),
                  Text(
                    hasTranscript
                        ? '${formatDuration(duration)} · $cueCount 句'
                        : '${formatDuration(duration)} · 普通音频',
                    textAlign: TextAlign.center,
                    style: theme.typography.caption,
                  ),
                  const SizedBox(height: 8),
                  Center(
                    child: Button(
                      onPressed: () => unawaited(_showFileInfo(context)),
                      child: const Text('文件信息'),
                    ),
                  ),
                  const SizedBox(height: 20),
                  PlaybackControls(
                    duration: duration,
                    position: position,
                    playing: playing,
                    singleSentenceLoop: singleSentenceLoop,
                    onTogglePlayback: onTogglePlayback,
                    onSeek: onSeek,
                    onStepSentence: hasTranscript ? onStepSentence : null,
                    onStepQuestion: onStepQuestion,
                    onSingleSentenceLoopChanged: hasTranscript
                        ? onSingleSentenceLoopChanged
                        : null,
                    showSubtitles: showSubtitles,
                    onShowSubtitlesChanged: hasTranscript
                        ? onShowSubtitlesChanged
                        : null,
                  ),
                  if (onShowAllCloze != null) ...[
                    const SizedBox(height: 12),
                    Center(
                      child: Button(
                        onPressed: () => onShowAllCloze!(!showAllCloze),
                        child: Text(showAllCloze ? '隐藏全部挖空' : '显示全部挖空'),
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class PlaybackControls extends StatelessWidget {
  const PlaybackControls({
    super.key,
    required this.duration,
    required this.position,
    required this.playing,
    this.singleSentenceLoop = false,
    required this.onTogglePlayback,
    required this.onSeek,
    this.onStepSentence,
    this.onStepQuestion,
    this.onSingleSentenceLoopChanged,
    this.showSubtitles = true,
    this.onShowSubtitlesChanged,
  });

  final Duration duration;
  final Duration position;
  final bool playing;
  final bool singleSentenceLoop;
  final Future<void> Function() onTogglePlayback;
  final Future<void> Function(Duration position) onSeek;
  final Future<void> Function(int delta)? onStepSentence;
  final Future<void> Function(int delta)? onStepQuestion;
  final ValueChanged<bool>? onSingleSentenceLoopChanged;
  final bool showSubtitles;
  final ValueChanged<bool>? onShowSubtitlesChanged;

  @override
  Widget build(BuildContext context) {
    final safeDuration = duration > Duration.zero
        ? duration
        : const Duration(seconds: 1);
    final safePosition = clampDuration(position, duration);
    final progress = safePosition.inMilliseconds / safeDuration.inMilliseconds;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Slider(
          value: progress.clamp(0, 1),
          min: 0,
          max: 1,
          onChanged: duration <= Duration.zero
              ? null
              : (value) => unawaited(
                  onSeek(
                    Duration(
                      milliseconds: (duration.inMilliseconds * value).round(),
                    ),
                  ),
                ),
        ),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 2),
          child: Row(
            children: [
              Text(formatDuration(safePosition), style: timeStyle),
              const Spacer(),
              Text(formatDuration(duration), style: timeStyle),
            ],
          ),
        ),
        const SizedBox(height: 20),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Button(
              onPressed: onStepSentence == null
                  ? null
                  : () => unawaited(onStepSentence!(-1)),
              child: const Text('上一句'),
            ),
            const SizedBox(width: 24),
            Tooltip(
              message: playing ? '暂停' : '播放',
              child: FilledButton(
                style: ButtonStyle(
                  padding: WidgetStateProperty.all(
                    const EdgeInsets.symmetric(horizontal: 22, vertical: 15),
                  ),
                ),
                onPressed: () => unawaited(onTogglePlayback()),
                child: Icon(
                  playing ? FluentIcons.pause : FluentIcons.play_solid,
                  size: 22,
                ),
              ),
            ),
            const SizedBox(width: 24),
            Button(
              onPressed: onStepSentence == null
                  ? null
                  : () => unawaited(onStepSentence!(1)),
              child: const Text('下一句'),
            ),
          ],
        ),
        if (onStepQuestion != null) ...[
          const SizedBox(height: 10),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Button(
                onPressed: () => unawaited(onStepQuestion!(-1)),
                child: const Text('上一题'),
              ),
              const SizedBox(width: 8),
              Button(
                onPressed: () => unawaited(onStepQuestion!(1)),
                child: const Text('下一题'),
              ),
            ],
          ),
        ],
        if (onSingleSentenceLoopChanged != null ||
            onShowSubtitlesChanged != null) ...[
          const SizedBox(height: 16),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              if (onSingleSentenceLoopChanged != null)
                Checkbox(
                  checked: singleSentenceLoop,
                  onChanged: (value) =>
                      onSingleSentenceLoopChanged!(value == true),
                  content: const Text('单句循环'),
                ),
              if (onSingleSentenceLoopChanged != null &&
                  onShowSubtitlesChanged != null)
                const SizedBox(width: 18),
              if (onShowSubtitlesChanged != null)
                Checkbox(
                  checked: !showSubtitles,
                  onChanged: (value) => onShowSubtitlesChanged!(value != true),
                  content: const Text('隐藏字幕'),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

class TranscriptPane extends StatefulWidget {
  const TranscriptPane({
    super.key,
    required this.cues,
    required this.activeIndex,
    required this.onSelected,
    required this.exercises,
    required this.revealedCloze,
    required this.showAllCloze,
    required this.showSubtitles,
    required this.onToggleCloze,
    required this.onShowAllCloze,
    required this.questionIndex,
    this.fontSize = 18,
  });

  final List<SrtCue> cues;
  final int activeIndex;
  final ValueChanged<int> onSelected;
  final LessonExercises exercises;
  final Set<String> revealedCloze;
  final bool showAllCloze;
  final bool showSubtitles;
  final void Function(int cueIndex, int wordIndex) onToggleCloze;
  final ValueChanged<bool> onShowAllCloze;
  final int questionIndex;
  final double fontSize;

  @override
  State<TranscriptPane> createState() => _TranscriptPaneState();
}

class _TranscriptDisplayGroup {
  _TranscriptDisplayGroup({required this.materialIndex});

  final int? materialIndex;
  final cueIndexes = <int>[];
}

class _TranscriptPaneState extends State<TranscriptPane> {
  final _itemScrollController = ItemScrollController();
  final _cueKeys = <int, GlobalKey>{};
  final _unassignedKeys = <int, GlobalKey<ExpanderState>>{};
  final _repeatedKeys = <String, GlobalKey<ExpanderState>>{};
  Timer? _centerTimer;
  Timer? _followTimer;
  Timer? _resumeFollowTimer;
  int _scrollGeneration = 0;
  bool _followingPaused = false;

  @override
  void initState() {
    super.initState();
    _followTimer = Timer.periodic(const Duration(seconds: 2), (_) {
      _centerActiveCue();
    });
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _centerActiveCue();
    });
  }

  @override
  void didUpdateWidget(covariant TranscriptPane oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.activeIndex != widget.activeIndex) {
      final generation = ++_scrollGeneration;
      _centerTimer?.cancel();
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (!mounted || generation != _scrollGeneration) return;
        _syncPlaybackExpanders();
        _centerTimer = Timer(const Duration(milliseconds: 420), () {
          if (generation == _scrollGeneration) _centerActiveCue();
        });
      });
    }
  }

  void _syncPlaybackExpanders() {
    for (final group in _displayGroups()) {
      final active = group.cueIndexes.contains(widget.activeIndex);
      if (group.materialIndex == null) {
        _unassignedKeys[group.cueIndexes.first]?.currentState?.isExpanded =
            active;
      } else {
        final material =
            widget.exercises.effectiveMaterials[group.materialIndex!];
        final repeatedActive =
            active && material.containsRepeatedCue(widget.activeIndex);
        _repeatedKeys['${material.id}:${group.cueIndexes.first}']
                ?.currentState
                ?.isExpanded =
            repeatedActive;
      }
    }
  }

  void _centerActiveCue({int remainingAttempts = 2}) {
    if (!mounted || _followingPaused) return;
    if (widget.activeIndex < 0 || widget.activeIndex >= widget.cues.length) {
      return;
    }
    final cueContext = _cueKeys[widget.activeIndex]?.currentContext;
    if (cueContext == null) {
      if (remainingAttempts <= 0 || !_itemScrollController.isAttached) return;
      final groupIndex = _displayGroups().indexWhere(
        (group) => group.cueIndexes.contains(widget.activeIndex),
      );
      if (groupIndex < 0) return;
      _itemScrollController.jumpTo(index: groupIndex);
      _syncPlaybackExpanders();
      _centerTimer?.cancel();
      _centerTimer = Timer(const Duration(milliseconds: 420), () {
        _centerActiveCue(remainingAttempts: remainingAttempts - 1);
      });
      return;
    }
    final renderObject = cueContext.findRenderObject();
    if (renderObject == null) return;
    final scrollable = Scrollable.of(cueContext);
    final viewport = RenderAbstractViewport.of(renderObject);
    final target = viewport
        .getOffsetToReveal(renderObject, 0.5)
        .offset
        .clamp(
          scrollable.position.minScrollExtent,
          scrollable.position.maxScrollExtent,
        );
    if ((scrollable.position.pixels - target).abs() < 8) return;
    unawaited(
      scrollable.position.animateTo(
        target,
        duration: const Duration(milliseconds: 300),
        curve: Curves.easeOutCubic,
      ),
    );
  }

  void _pauseFollowingForUserScroll() {
    _scrollGeneration += 1;
    _centerTimer?.cancel();
    _followingPaused = true;
    _resumeFollowTimer?.cancel();
    _resumeFollowTimer = Timer(const Duration(seconds: 5), () {
      _followingPaused = false;
      _centerActiveCue();
    });
  }

  bool _handleScroll(ScrollNotification notification) {
    if ((notification is ScrollStartNotification &&
            notification.dragDetails != null) ||
        (notification is ScrollUpdateNotification &&
            notification.dragDetails != null)) {
      _pauseFollowingForUserScroll();
    }
    return false;
  }

  List<_TranscriptDisplayGroup> _displayGroups() {
    final groups = <_TranscriptDisplayGroup>[];
    for (var cueIndex = 0; cueIndex < widget.cues.length; cueIndex++) {
      final materialIndex = widget.exercises.materialIndexForCue(cueIndex);
      if (groups.isEmpty || groups.last.materialIndex != materialIndex) {
        groups.add(_TranscriptDisplayGroup(materialIndex: materialIndex));
      }
      groups.last.cueIndexes.add(cueIndex);
    }
    return groups;
  }

  Widget _cueRows(List<int> cueIndexes) {
    final theme = FluentTheme.of(context);
    return Column(
      children: [
        for (var rowIndex = 0; rowIndex < cueIndexes.length; rowIndex++) ...[
          if (rowIndex > 0)
            const Divider(
              style: DividerThemeData(horizontalMargin: EdgeInsets.zero),
            ),
          Builder(
            builder: (context) {
              final index = cueIndexes[rowIndex];
              final cue = widget.cues[index];
              return Container(
                key: _cueKeys.putIfAbsent(index, GlobalKey.new),
                constraints: const BoxConstraints(minHeight: 76),
                child: ListTile.selectable(
                  selected: index == widget.activeIndex,
                  leading: Text(formatDuration(cue.start), style: timeStyle),
                  title: DefaultTextStyle.merge(
                    style: theme.typography.bodyLarge?.copyWith(
                      fontSize: widget.fontSize,
                    ),
                    child: _ClozeSentence(
                      text: cue.text,
                      cueIndex: index,
                      clozeWordIndexes:
                          widget.exercises.clozeWordIndexes[index] ?? const {},
                      revealed: widget.revealedCloze,
                      showAll: widget.showAllCloze,
                      onToggle: widget.onToggleCloze,
                    ),
                  ),
                  onPressed: () => widget.onSelected(index),
                ),
              );
            },
          ),
        ],
      ],
    );
  }

  Widget _groupCard(BuildContext context, _TranscriptDisplayGroup group) {
    final theme = FluentTheme.of(context);
    final material = group.materialIndex == null
        ? null
        : widget.exercises.effectiveMaterials[group.materialIndex!];
    final questions = material == null
        ? const <LessonQuestion>[]
        : widget.exercises.questionsForMaterial(material);
    final repeated = material == null
        ? const <int>[]
        : group.cueIndexes
              .where(material.containsRepeatedCue)
              .toList(growable: false);
    final primary = repeated.isEmpty
        ? group.cueIndexes
        : group.cueIndexes
              .where((index) => !material!.containsRepeatedCue(index))
              .toList(growable: false);
    final active = group.cueIndexes.contains(widget.activeIndex);
    final firstCue = widget.cues[group.cueIndexes.first];
    final lastCue = widget.cues[group.cueIndexes.last];
    final questionNumbers = questions
        .map((question) => question.number)
        .where((number) => number > 0)
        .join('、');
    final title = material == null
        ? '题前提示'
        : questionNumbers.isEmpty
        ? '听力材料'
        : '第 $questionNumbers 题';

    if (material == null) {
      return Expander(
        key: _unassignedKeys.putIfAbsent(
          group.cueIndexes.first,
          GlobalKey<ExpanderState>.new,
        ),
        initiallyExpanded: active,
        header: Row(
          children: [
            const Icon(FluentIcons.info, size: 17),
            const SizedBox(width: 10),
            Expanded(child: Text(title, style: theme.typography.bodyStrong)),
            Text(
              '${group.cueIndexes.length} 句',
              style: theme.typography.caption,
            ),
          ],
        ),
        content: _cueRows(group.cueIndexes),
      );
    }

    return Card(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Container(
            color: active
                ? theme.accentColor.withValues(alpha: 0.08)
                : Colors.transparent,
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
            child: Row(
              children: [
                Icon(
                  FluentIcons.checkbox_composite,
                  size: 17,
                  color: active ? theme.accentColor : null,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: theme.typography.bodyStrong),
                      if (questions.isNotEmpty) ...[
                        const SizedBox(height: 3),
                        Text(
                          questions
                              .map(
                                (question) =>
                                    '第 ${question.number} 题：${question.title}',
                              )
                              .join('\n'),
                          maxLines: questions.length * 2,
                          overflow: TextOverflow.ellipsis,
                          style: theme.typography.caption,
                        ),
                      ],
                      const SizedBox(height: 2),
                      Text(
                        '${formatDuration(firstCue.start)} – '
                        '${formatDuration(lastCue.end)} · '
                        '${group.cueIndexes.length} 句',
                        style: theme.typography.caption,
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          const Divider(
            style: DividerThemeData(horizontalMargin: EdgeInsets.zero),
          ),
          if (primary.isNotEmpty) _cueRows(primary),
          if (repeated.isNotEmpty) ...[
            const Divider(
              style: DividerThemeData(horizontalMargin: EdgeInsets.zero),
            ),
            Expander(
              key: _repeatedKeys.putIfAbsent(
                '${material.id}:${group.cueIndexes.first}',
                GlobalKey<ExpanderState>.new,
              ),
              initiallyExpanded: repeated.contains(widget.activeIndex),
              header: Row(
                children: [
                  const Icon(FluentIcons.sync, size: 16),
                  const SizedBox(width: 8),
                  const Text('重复朗读'),
                  const Spacer(),
                  Text('${repeated.length} 句'),
                ],
              ),
              content: _cueRows(repeated),
            ),
          ],
        ],
      ),
    );
  }

  @override
  void dispose() {
    _centerTimer?.cancel();
    _followTimer?.cancel();
    _resumeFollowTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final groups = _displayGroups();
    final activeGroupIndex = groups.indexWhere(
      (group) => group.cueIndexes.contains(widget.activeIndex),
    );
    return ScaffoldPage(
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 0),
      content: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: _GaussianSubtitleMask(
              hidden: !widget.showSubtitles,
              child: Listener(
                onPointerSignal: (signal) {
                  if (signal is PointerScrollEvent) {
                    _pauseFollowingForUserScroll();
                  }
                },
                child: NotificationListener<ScrollNotification>(
                  onNotification: _handleScroll,
                  child: ScrollablePositionedList.builder(
                    itemScrollController: _itemScrollController,
                    itemCount: groups.length,
                    initialScrollIndex: activeGroupIndex < 0
                        ? 0
                        : activeGroupIndex,
                    padding: const EdgeInsets.only(bottom: 16),
                    itemBuilder: (context, index) => Padding(
                      padding: EdgeInsets.only(top: index == 0 ? 0 : 10),
                      child: _groupCard(context, groups[index]),
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _GaussianSubtitleMask extends StatelessWidget {
  const _GaussianSubtitleMask({required this.hidden, required this.child});

  final bool hidden;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return SpringAnimatedDouble(
      value: hidden ? 8 : 0,
      builder: (context, sigma, child) => ImageFiltered(
        imageFilter: ui.ImageFilter.blur(
          sigmaX: sigma.clamp(0, 8),
          sigmaY: sigma.clamp(0, 8),
        ),
        child: child,
      ),
      child: child,
    );
  }
}

class _ClozeSentence extends StatelessWidget {
  const _ClozeSentence({
    required this.text,
    required this.cueIndex,
    required this.clozeWordIndexes,
    required this.revealed,
    required this.showAll,
    required this.onToggle,
  });

  final String text;
  final int cueIndex;
  final Set<int> clozeWordIndexes;
  final Set<String> revealed;
  final bool showAll;
  final void Function(int cueIndex, int wordIndex) onToggle;

  @override
  Widget build(BuildContext context) {
    final style = DefaultTextStyle.of(context).style;
    return Wrap(
      alignment: WrapAlignment.start,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        for (final part in tokenizeLessonText(text))
          if (!part.isWord)
            Text(part.text, style: style)
          else
            _ClozeWord(
              text: part.text,
              hidden:
                  clozeWordIndexes.contains(part.wordIndex) &&
                  !showAll &&
                  !revealed.contains('$cueIndex:${part.wordIndex}'),
              onPressed: clozeWordIndexes.contains(part.wordIndex)
                  ? () => onToggle(cueIndex, part.wordIndex!)
                  : null,
            ),
      ],
    );
  }
}

class _ClozeWord extends StatelessWidget {
  const _ClozeWord({required this.text, required this.hidden, this.onPressed});

  final String text;
  final bool hidden;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final isInteractive = onPressed != null;

    final textWidget = Text(text);
    final wordContent = hidden
        ? ImageFiltered(
            imageFilter: ui.ImageFilter.blur(sigmaX: 4.5, sigmaY: 4.5),
            child: textWidget,
          )
        : textWidget;

    Widget result;
    if (hidden) {
      result = Container(
        margin: const EdgeInsets.symmetric(horizontal: 2, vertical: 1),
        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
        decoration: BoxDecoration(
          color: theme.accentColor.withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(4),
          border: Border(
            bottom: BorderSide(
              color: theme.accentColor.defaultBrushFor(theme.brightness),
              width: 2,
            ),
          ),
        ),
        child: wordContent,
      );
    } else if (isInteractive) {
      result = Container(
        margin: const EdgeInsets.symmetric(horizontal: 2, vertical: 1),
        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
        decoration: BoxDecoration(
          color: theme.accentColor.withValues(alpha: 0.06),
          borderRadius: BorderRadius.circular(4),
        ),
        child: wordContent,
      );
    } else {
      result = Padding(
        padding: const EdgeInsets.symmetric(horizontal: 1),
        child: wordContent,
      );
    }

    if (!isInteractive) return result;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: Tooltip(
        message: hidden ? '显示单词' : '隐藏单词',
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: onPressed,
          child: result,
        ),
      ),
    );
  }
}

class FileRow extends StatelessWidget {
  const FileRow({
    super.key,
    required this.label,
    required this.icon,
    required this.value,
    required this.onPressed,
    this.primary = false,
  });

  final String label;
  final IconData icon;
  final String value;
  final VoidCallback? onPressed;
  final bool primary;

  @override
  Widget build(BuildContext context) {
    final buttonChild = Row(
      children: [Icon(icon), const SizedBox(width: 8), Text(label)],
    );
    return Row(
      children: [
        primary
            ? FilledButton(onPressed: onPressed, child: buttonChild)
            : Button(onPressed: onPressed, child: buttonChild),
        const SizedBox(width: 12),
        Expanded(
          child: Text(value, maxLines: 1, overflow: TextOverflow.ellipsis),
        ),
      ],
    );
  }
}

const timeStyle = TextStyle(
  fontSize: 12,
  fontFeatures: [FontFeature.tabularFigures()],
);

Duration clampDuration(Duration value, Duration duration) {
  if (value < Duration.zero) return Duration.zero;
  if (duration > Duration.zero && value > duration) return duration;
  return value;
}

String formatDuration(Duration value) {
  final duration = value < Duration.zero ? Duration.zero : value;
  final hours = duration.inHours;
  final minutes = duration.inMinutes.remainder(60).toString().padLeft(2, '0');
  final seconds = duration.inSeconds.remainder(60).toString().padLeft(2, '0');
  if (hours > 0) return '$hours:$minutes:$seconds';
  return '$minutes:$seconds';
}

String serializeSrt(List<SrtCue> cues) {
  final buffer = StringBuffer();
  for (var index = 0; index < cues.length; index++) {
    final cue = cues[index];
    buffer
      ..writeln(index + 1)
      ..writeln(
        '${formatSrtTimestamp(cue.start)} --> ${formatSrtTimestamp(cue.end)}',
      )
      ..writeln(cue.text.trim())
      ..writeln();
  }
  return buffer.toString();
}

String formatSrtTimestamp(Duration value) {
  final safe = value < Duration.zero ? Duration.zero : value;
  final hours = safe.inHours.toString().padLeft(2, '0');
  final minutes = safe.inMinutes.remainder(60).toString().padLeft(2, '0');
  final seconds = safe.inSeconds.remainder(60).toString().padLeft(2, '0');
  final milliseconds = safe.inMilliseconds
      .remainder(1000)
      .toString()
      .padLeft(3, '0');
  return '$hours:$minutes:$seconds,$milliseconds';
}

String safeFileName(String value) {
  final name = value.trim().replaceAll(RegExp(r'[\\/:*?"<>|]+'), ' ').trim();
  return name.isEmpty ? 'lesson' : name;
}

Future<void> showLessonPicker({
  required BuildContext context,
  required List<ImportedLesson> lessons,
  required ImportedLesson? selected,
  required Future<void> Function(ImportedLesson lesson) onSelected,
}) {
  return showSpringDialog<void>(
    context: context,
    builder: (context) => ContentDialog(
      title: const Text('打开课程'),
      content: SizedBox(
        width: 420,
        height: 360,
        child: ListView.builder(
          itemCount: lessons.length,
          itemBuilder: (context, index) {
            final lesson = lessons[index];
            return Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: ListTile.selectable(
                selected: lesson.id == selected?.id,
                title: Text(lesson.manifest.title),
                subtitle: Text(
                  '${formatDuration(lesson.manifest.duration)} · ${lesson.cues.length} 句',
                ),
                onPressed: () {
                  Navigator.pop(context);
                  unawaited(onSelected(lesson));
                },
              ),
            );
          },
        ),
      ),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context),
          child: const Text('关闭'),
        ),
      ],
    ),
  );
}

Future<void> showResult(BuildContext context, String title, String message) {
  return showSpringDialog<void>(
    context: context,
    builder: (context) => ContentDialog(
      title: Text(title),
      content: Text(message),
      actions: [
        FilledButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('完成'),
        ),
      ],
    ),
  );
}
