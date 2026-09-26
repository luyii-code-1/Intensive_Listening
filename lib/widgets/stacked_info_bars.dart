import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter/services.dart';

import 'spring_motion.dart';
import '../app_log.dart';

const _exitDuration = Duration(milliseconds: 420);

class StackedInfoBarController extends ChangeNotifier {
  final _items = <_StackedInfoBarItem>[];
  final _timers = <int, Timer>{};
  var _sequence = 0;

  List<_StackedInfoBarItem> get _visibleItems => List.unmodifiable(_items);

  void show({
    required String title,
    required String message,
    InfoBarSeverity severity = InfoBarSeverity.success,
    Duration duration = const Duration(seconds: 5),
  }) {
    AppLog.notice(
      title,
      message,
      isWarning: severity == InfoBarSeverity.warning,
      isError: severity == InfoBarSeverity.error,
    );
    final id = ++_sequence;
    _items.add(
      _StackedInfoBarItem(
        id: id,
        title: title,
        message: message,
        severity: severity,
      ),
    );
    if (severity != InfoBarSeverity.error) {
      _timers[id] = Timer(duration, () => dismiss(id));
    }
    notifyListeners();
  }

  void dismiss(int id) {
    _timers.remove(id)?.cancel();
    final index = _items.indexWhere((item) => item.id == id);
    if (index < 0 || _items[index].isDismissing) return;
    _items[index] = _items[index].dismissing();
    notifyListeners();
    _timers[id] = Timer(_exitDuration, () {
      _timers.remove(id);
      _items.removeWhere((item) => item.id == id);
      notifyListeners();
    });
  }

  @override
  void dispose() {
    for (final timer in _timers.values) {
      timer.cancel();
    }
    _timers.clear();
    super.dispose();
  }
}

class StackedInfoBarScope extends InheritedNotifier<StackedInfoBarController> {
  const StackedInfoBarScope({
    super.key,
    required StackedInfoBarController controller,
    required super.child,
  }) : super(notifier: controller);

  static StackedInfoBarController of(BuildContext context) {
    final scope = context
        .dependOnInheritedWidgetOfExactType<StackedInfoBarScope>();
    assert(scope != null, 'StackedInfoBarScope is missing above this context.');
    return scope!.notifier!;
  }
}

class StackedInfoBarHost extends StatelessWidget {
  const StackedInfoBarHost({super.key, required this.controller});

  final StackedInfoBarController controller;

  @override
  Widget build(BuildContext context) {
    return ConstrainedBox(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.sizeOf(context).height * 0.65,
      ),
      child: ListenableBuilder(
        listenable: controller,
        builder: (context, _) => SingleChildScrollView(
          reverse: true,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              for (final item in controller._visibleItems)
                _AnimatedNoticeTile(
                  key: ValueKey(item.id),
                  item: item,
                  onClose: () => controller.dismiss(item.id),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _StackedInfoBarItem {
  const _StackedInfoBarItem({
    required this.id,
    required this.title,
    required this.message,
    required this.severity,
    this.isDismissing = false,
  });

  final int id;
  final String title;
  final String message;
  final InfoBarSeverity severity;
  final bool isDismissing;

  _StackedInfoBarItem dismissing() => _StackedInfoBarItem(
    id: id,
    title: title,
    message: message,
    severity: severity,
    isDismissing: true,
  );
}

class AnimatedInlineInfoBar extends StatefulWidget {
  const AnimatedInlineInfoBar({
    super.key,
    required this.noticeId,
    required this.title,
    required this.message,
    required this.severity,
    required this.onClose,
  });

  final Object? noticeId;
  final String? title;
  final String? message;
  final InfoBarSeverity? severity;
  final VoidCallback onClose;

  @override
  State<AnimatedInlineInfoBar> createState() => _AnimatedInlineInfoBarState();
}

class _AnimatedInlineInfoBarState extends State<AnimatedInlineInfoBar> {
  Timer? _timer;

  @override
  void initState() {
    super.initState();
    _scheduleDismissal();
  }

  @override
  void didUpdateWidget(covariant AnimatedInlineInfoBar oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.noticeId != widget.noticeId) _scheduleDismissal();
  }

  void _scheduleDismissal() {
    _timer?.cancel();
    if (widget.noticeId != null && widget.severity != InfoBarSeverity.error) {
      _timer = Timer(const Duration(seconds: 5), widget.onClose);
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 280),
      reverseDuration: _exitDuration,
      layoutBuilder: (currentChild, previousChildren) => Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [...previousChildren, ?currentChild],
      ),
      transitionBuilder: (child, animation) =>
          _NoticeTransition(animation: animation, child: child),
      child: widget.noticeId == null
          ? const SizedBox.shrink(key: ValueKey('no-notice'))
          : Padding(
              key: ValueKey(widget.noticeId),
              padding: const EdgeInsets.only(bottom: 12),
              child: _AppNoticeBar(
                title: widget.title!,
                message: widget.message!,
                severity: widget.severity!,
                onClose: widget.onClose,
              ),
            ),
    );
  }
}

class _AnimatedNoticeTile extends StatefulWidget {
  const _AnimatedNoticeTile({
    super.key,
    required this.item,
    required this.onClose,
  });

  final _StackedInfoBarItem item;
  final VoidCallback onClose;

  @override
  State<_AnimatedNoticeTile> createState() => _AnimatedNoticeTileState();
}

class _AnimatedNoticeTileState extends State<_AnimatedNoticeTile>
    with SingleTickerProviderStateMixin {
  late final AnimationController _animation = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 280),
    reverseDuration: _exitDuration,
  )..forward();

  @override
  void didUpdateWidget(covariant _AnimatedNoticeTile oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!oldWidget.item.isDismissing && widget.item.isDismissing) {
      _animation.reverse();
    }
  }

  @override
  void dispose() {
    _animation.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return _NoticeTransition(
      animation: _animation,
      child: Padding(
        padding: const EdgeInsets.only(top: 8),
        child: _AppNoticeBar(
          title: widget.item.title,
          message: widget.item.message,
          severity: widget.item.severity,
          onClose: widget.onClose,
        ),
      ),
    );
  }
}

class _NoticeTransition extends StatelessWidget {
  const _NoticeTransition({required this.animation, required this.child});

  final Animation<double> animation;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return ClipRect(
      child: SizeTransition(
        sizeFactor: CurvedAnimation(
          parent: animation,
          curve: Curves.easeOutCubic,
          reverseCurve: Curves.easeInCubic,
        ),
        child: SpringMotionTransition(
          animation: animation,
          beginOffset: const Offset(0, 0.04),
          beginScale: 1,
          child: child,
        ),
      ),
    );
  }
}

class _AppNoticeBar extends StatefulWidget {
  const _AppNoticeBar({
    required this.title,
    required this.message,
    required this.severity,
    required this.onClose,
  });

  final String title;
  final String message;
  final InfoBarSeverity severity;
  final VoidCallback onClose;

  @override
  State<_AppNoticeBar> createState() => _AppNoticeBarState();
}

class _AppNoticeBarState extends State<_AppNoticeBar> {
  var _copied = false;

  @override
  void didUpdateWidget(covariant _AppNoticeBar oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.message != widget.message ||
        oldWidget.title != widget.title) {
      _copied = false;
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final (icon, color) = switch (widget.severity) {
      InfoBarSeverity.success => (FluentIcons.completed, Colors.green),
      InfoBarSeverity.warning => (FluentIcons.warning, Colors.orange),
      InfoBarSeverity.error => (FluentIcons.error_badge, Colors.red),
      InfoBarSeverity.info => (FluentIcons.info, theme.accentColor),
    };
    return SizedBox(
      width: 380,
      height: 96,
      child: Container(
        padding: const EdgeInsets.fromLTRB(14, 12, 8, 12),
        decoration: BoxDecoration(
          color: theme.resources.solidBackgroundFillColorQuarternary,
          borderRadius: BorderRadius.circular(6),
          border: Border.all(color: theme.resources.cardStrokeColorDefault),
          boxShadow: [
            BoxShadow(
              color: const Color(0xFF000000).withValues(alpha: 0.12),
              blurRadius: 14,
              offset: const Offset(0, 4),
            ),
          ],
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Icon(icon, color: color, size: 19),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(
                    widget.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.typography.bodyStrong,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    widget.message,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: theme.typography.body,
                  ),
                ],
              ),
            ),
            if (widget.severity == InfoBarSeverity.error)
              Tooltip(
                message: _copied ? '已复制' : '复制错误详情',
                child: IconButton(
                  icon: const Icon(FluentIcons.copy, size: 14),
                  onPressed: () async {
                    await Clipboard.setData(
                      ClipboardData(text: '${widget.title}\n${widget.message}'),
                    );
                    if (mounted) setState(() => _copied = true);
                  },
                ),
              ),
            IconButton(
              icon: const Icon(FluentIcons.chrome_close, size: 14),
              onPressed: widget.onClose,
            ),
          ],
        ),
      ),
    );
  }
}
