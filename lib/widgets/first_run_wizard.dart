import 'dart:io';

import 'package:fluent_ui/fluent_ui.dart';

import '../app_directories.dart';
import '../settings/app_settings.dart';
import '../telemetry/telemetry_consent_ui.dart';
import 'spring_motion.dart';

class FirstRunWizard extends StatefulWidget {
  const FirstRunWizard({
    super.key,
    required this.initial,
    required this.agreement,
    required this.privacy,
  });

  final AppSettings initial;
  final String agreement;
  final String privacy;

  @override
  State<FirstRunWizard> createState() => _FirstRunWizardState();
}

class _FirstRunWizardState extends State<FirstRunWizard> {
  static const _accent = Color(0xFFB80018);

  late final TextEditingController _apiKey = TextEditingController(
    text: widget.initial.cloudApiKey,
  );
  late bool _association = widget.initial.eulaAcceptedVersion.isEmpty
      ? true
      : widget.initial.fileAssociationEnabled;
  late bool _mcp = widget.initial.eulaAcceptedVersion.isEmpty
      ? true
      : widget.initial.mcpEnabled;
  late bool _skipOpening = widget.initial.skipOpeningPrompts;
  late bool _telemetry = widget.initial.telemetryPrompted
      ? widget.initial.telemetryEnabled
      : true;
  late int _fontSize = widget.initial.transcriptFontSize;
  late String _themeMode = widget.initial.themeMode;
  bool _acceptedAgreement = false;
  bool _acceptedPrivacy = false;
  int _step = 0;

  @override
  void dispose() {
    _apiKey.dispose();
    super.dispose();
  }

  int get _totalSteps => widget.initial.cloudReady ? 5 : 6;

  int get _visibleStep =>
      widget.initial.cloudReady && _step == 5 ? 5 : _step + 1;

  void _back() {
    if (_step == 0) {
      Navigator.pop(context);
      return;
    }
    setState(() {
      _step = widget.initial.cloudReady && _step == 5 ? 3 : _step - 1;
    });
  }

  void _next() {
    if (_step == 1 && (!_acceptedAgreement || !_acceptedPrivacy)) return;
    if (_step == 5) {
      Navigator.pop(
        context,
        widget.initial.copyWith(
          eulaAcceptedVersion: '2026-09-22',
          fileAssociationEnabled: _association,
          fileAssociationPrompted: true,
          mcpEnabled: _mcp,
          skipOpeningPrompts: _skipOpening,
          transcriptFontSize: _fontSize,
          themeMode: _themeMode,
          cloudApiKey: _apiKey.text.trim(),
          telemetryEnabled: _telemetry,
          telemetryPrompted: true,
        ),
      );
      return;
    }
    setState(() {
      _step = widget.initial.cloudReady && _step == 3 ? 5 : _step + 1;
    });
  }

  Future<void> _setTelemetry(bool enabled) async {
    if (!enabled && _telemetry && !await confirmTelemetryDisable(context)) {
      return;
    }
    if (mounted) setState(() => _telemetry = enabled);
  }

  Future<void> _showDocument(String title, String body) async {
    await showSpringDialog<void>(
      context: context,
      builder: (dialogContext) => ContentDialog(
        title: Text(title),
        constraints: const BoxConstraints(maxWidth: 700, maxHeight: 660),
        content: SizedBox(
          width: 620,
          height: 460,
          child: SingleChildScrollView(child: SelectableText(body)),
        ),
        actions: [
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: const Text('关闭'),
          ),
        ],
      ),
    );
  }

  Widget _brand({double size = 76}) => Container(
    width: size,
    height: size,
    decoration: BoxDecoration(
      color: _accent,
      borderRadius: BorderRadius.circular(size * 0.24),
    ),
    child: Icon(FluentIcons.play, color: Colors.white, size: size * 0.48),
  );

  Widget _navigation({bool first = false}) => Padding(
    padding: const EdgeInsets.only(top: 26),
    child: Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        if (!first) ...[
          _roundButton(
            icon: FluentIcons.chevron_left,
            label: '上一步',
            onPressed: _back,
            primary: false,
          ),
          const SizedBox(width: 12),
        ],
        _roundButton(
          icon: _step == 5 ? FluentIcons.accept : FluentIcons.chevron_right,
          label: _step == 5 ? '完成设置' : '下一步',
          onPressed: _step == 1 && (!_acceptedAgreement || !_acceptedPrivacy)
              ? null
              : _next,
          primary: true,
        ),
      ],
    ),
  );

  Widget _roundButton({
    required IconData icon,
    required String label,
    required VoidCallback? onPressed,
    required bool primary,
  }) {
    final style = ButtonStyle(
      shape: WidgetStateProperty.all(const CircleBorder()),
      padding: WidgetStateProperty.all(EdgeInsets.zero),
      backgroundColor: primary
          ? WidgetStateProperty.resolveWith((states) {
              if (states.contains(WidgetState.disabled)) return Colors.grey[60];
              return _accent;
            })
          : null,
    );
    final child = SizedBox(
      width: 54,
      height: 54,
      child: Center(child: Icon(icon, size: 22)),
    );
    return Tooltip(
      message: label,
      child: primary
          ? FilledButton(onPressed: onPressed, style: style, child: child)
          : Button(onPressed: onPressed, style: style, child: child),
    );
  }

  Widget _pageHeading(String title, String subtitle) {
    final typography = FluentTheme.of(context).typography;
    return Column(
      children: [
        Text(title, style: typography.title),
        const SizedBox(height: 8),
        Text(subtitle, textAlign: TextAlign.center, style: typography.body),
        const SizedBox(height: 28),
      ],
    );
  }

  Widget _row({
    required IconData icon,
    required String title,
    required String description,
    required Widget trailing,
  }) {
    final theme = FluentTheme.of(context);
    return Card(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 16),
      child: Row(
        children: [
          Icon(icon, size: 22, color: _accent),
          const SizedBox(width: 18),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: theme.typography.bodyStrong),
                const SizedBox(height: 3),
                Text(description, style: theme.typography.caption),
              ],
            ),
          ),
          const SizedBox(width: 18),
          trailing,
        ],
      ),
    );
  }

  Widget _welcome() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _brand(),
      const SizedBox(height: 26),
      Text(
        'Intensive Listening',
        style: FluentTheme.of(context).typography.title,
      ),
      const SizedBox(height: 8),
      const Text('欢迎使用精听课程制作与播放'),
      _navigation(first: true),
    ],
  );

  Widget _agreementPage() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _pageHeading('同意许可条款', '请先阅读并确认用户协议与隐私说明。'),
      _row(
        icon: FluentIcons.document,
        title: '用户协议',
        description: '了解软件许可、用户内容和云端服务的使用说明。',
        trailing: Button(
          onPressed: () => _showDocument('用户协议', widget.agreement),
          child: const Text('阅读'),
        ),
      ),
      const SizedBox(height: 8),
      _row(
        icon: FluentIcons.info,
        title: '隐私说明',
        description: '了解本机保存的数据和匿名数据分析的范围。',
        trailing: Button(
          onPressed: () => _showDocument('隐私说明', widget.privacy),
          child: const Text('阅读'),
        ),
      ),
      const SizedBox(height: 18),
      Align(
        alignment: Alignment.centerLeft,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Checkbox(
              checked: _acceptedAgreement,
              onChanged: (value) =>
                  setState(() => _acceptedAgreement = value == true),
              content: const Text('我已阅读并同意用户协议'),
            ),
            const SizedBox(height: 10),
            Checkbox(
              checked: _acceptedPrivacy,
              onChanged: (value) =>
                  setState(() => _acceptedPrivacy = value == true),
              content: const Text('我已阅读并同意隐私说明'),
            ),
          ],
        ),
      ),
      _navigation(),
    ],
  );

  Widget _basicsPage() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _pageHeading('基本设置', '选择文件打开方式、制作接口和播放习惯。'),
      _row(
        icon: FluentIcons.open_file,
        title: '关联 .ilp 文件',
        description: '双击精听包即可在应用中打开。',
        trailing: ToggleSwitch(
          checked: _association,
          onChanged: (value) => setState(() => _association = value),
        ),
      ),
      const SizedBox(height: 8),
      _row(
        icon: FluentIcons.robot,
        title: 'MCP 制作接口',
        description: '允许本机智能体连接；接管仍需在应用内批准。',
        trailing: ToggleSwitch(
          checked: _mcp,
          onChanged: (value) => setState(() => _mcp = value),
        ),
      ),
      const SizedBox(height: 8),
      _row(
        icon: FluentIcons.forward,
        title: '跳过题前提示',
        description: '首次打开课程时直接定位到第一题前并暂停。',
        trailing: ToggleSwitch(
          checked: _skipOpening,
          onChanged: (value) => setState(() => _skipOpening = value),
        ),
      ),
      const SizedBox(height: 8),
      _row(
        icon: FluentIcons.chart,
        title: '匿名数据分析',
        description: '帮助了解应用使用情况与错误，可在设置中更改。',
        trailing: ToggleSwitch(checked: _telemetry, onChanged: _setTelemetry),
      ),
      const SizedBox(height: 14),
      Align(
        alignment: Alignment.centerLeft,
        child: Text(
          '$telemetryDisclosure\n$telemetryRetention',
          style: FluentTheme.of(context).typography.caption,
        ),
      ),
      _navigation(),
    ],
  );

  Widget _appearancePage() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _pageHeading('外观', '调整播放时的阅读体验。'),
      _row(
        icon: FluentIcons.color,
        title: '外观主题',
        description: '按系统设置或选择浅色、深色模式。',
        trailing: ComboBox<String>(
          value: _themeMode,
          items: const [
            ComboBoxItem(value: 'system', child: Text('跟随系统')),
            ComboBoxItem(value: 'light', child: Text('浅色')),
            ComboBoxItem(value: 'dark', child: Text('深色')),
          ],
          onChanged: (value) {
            if (value != null) setState(() => _themeMode = value);
          },
        ),
      ),
      const SizedBox(height: 8),
      _row(
        icon: FluentIcons.font,
        title: '字幕字体大小',
        description: '调整播放页逐句字幕的字号。',
        trailing: SizedBox(
          width: 240,
          child: Row(
            children: [
              Expanded(
                child: Slider(
                  value: _fontSize.toDouble(),
                  min: 14,
                  max: 28,
                  divisions: 14,
                  onChanged: (value) =>
                      setState(() => _fontSize = value.round()),
                ),
              ),
              const SizedBox(width: 10),
              Text('$_fontSize'),
            ],
          ),
        ),
      ),
      _navigation(),
    ],
  );

  Widget _apiPage() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _pageHeading('云端转写', '填写 API Key 后即可制作课程，也可以稍后再配置。'),
      _row(
        icon: FluentIcons.cloud,
        title: 'API Key',
        description: '密钥只保存在本机设置中。',
        trailing: SizedBox(
          width: 270,
          child: PasswordBox(controller: _apiKey, placeholder: '可留空'),
        ),
      ),
      const SizedBox(height: 14),
      Button(
        onPressed: () => Process.start('explorer.exe', const [
          'https://il.luyii.cn/guide.html#api-setup',
        ]),
        child: const Text('如何配置？'),
      ),
      _navigation(),
    ],
  );

  Widget _donePage() => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      _brand(size: 66),
      const SizedBox(height: 24),
      Text('设置完成', style: FluentTheme.of(context).typography.title),
      const SizedBox(height: 10),
      const Text('现在可以开始播放精听包，或创建一份课程。'),
      _navigation(),
    ],
  );

  @override
  Widget build(BuildContext context) {
    final size = MediaQuery.sizeOf(context);
    final dark = FluentTheme.of(context).brightness == Brightness.dark;
    return Center(
      child: Container(
        width: (size.width - 32).clamp(0.0, 1040.0),
        height: (size.height - 32).clamp(0.0, 740.0),
        decoration: BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topLeft,
            end: Alignment.bottomRight,
            colors: dark
                ? const [Color(0xFF202936), Color(0xFF292637)]
                : const [Color(0xFFEDF7FF), Color(0xFFF1F1FF)],
          ),
          borderRadius: BorderRadius.circular(12),
        ),
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(22, 14, 14, 0),
              child: Row(
                children: [
                  _brand(size: 24),
                  const SizedBox(width: 10),
                  const Expanded(child: Text('Intensive Listening')),
                  Button(
                    onPressed: () => Navigator.pop(context),
                    child: const Icon(FluentIcons.cancel, size: 14),
                  ),
                ],
              ),
            ),
            Expanded(
              child: Center(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 28,
                    vertical: 24,
                  ),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 730),
                    child: AnimatedSwitcher(
                      duration: const Duration(milliseconds: 260),
                      switchInCurve: Curves.easeOutCubic,
                      switchOutCurve: Curves.easeInCubic,
                      child: KeyedSubtree(
                        key: ValueKey(_step),
                        child: switch (_step) {
                          0 => _welcome(),
                          1 => _agreementPage(),
                          2 => _basicsPage(),
                          3 => _appearancePage(),
                          4 => _apiPage(),
                          _ => _donePage(),
                        },
                      ),
                    ),
                  ),
                ),
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(22, 0, 22, 16),
              child: Row(
                children: [
                  Text('$appVersion Prelude'),
                  const Spacer(),
                  Text('$_visibleStep / $_totalSteps'),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
