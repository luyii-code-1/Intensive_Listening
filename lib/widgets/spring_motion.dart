import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter/physics.dart';

const _criticalSpring = SpringDescription(
  mass: 1,
  stiffness: 510,
  damping: 45.17,
);

class SpringMotionTransition extends StatefulWidget {
  const SpringMotionTransition({
    super.key,
    required this.animation,
    required this.child,
    this.beginOffset = const Offset(0, 0.015),
    this.beginScale = 0.97,
  });

  final Animation<double> animation;
  final Widget child;
  final Offset beginOffset;
  final double beginScale;

  @override
  State<SpringMotionTransition> createState() => _SpringMotionTransitionState();
}

class _SpringMotionTransitionState extends State<SpringMotionTransition>
    with SingleTickerProviderStateMixin {
  late final AnimationController _spring;
  double? _target;

  @override
  void initState() {
    super.initState();
    _spring = AnimationController.unbounded(
      vsync: this,
      value: widget.animation.value,
    );
    widget.animation.addStatusListener(_handleStatus);
    _retarget();
  }

  @override
  void didUpdateWidget(covariant SpringMotionTransition oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.animation != widget.animation) {
      oldWidget.animation.removeStatusListener(_handleStatus);
      widget.animation.addStatusListener(_handleStatus);
      _target = null;
      _retarget();
    }
  }

  void _handleStatus(AnimationStatus _) => _retarget();

  void _retarget() {
    final nextTarget = switch (widget.animation.status) {
      AnimationStatus.reverse || AnimationStatus.dismissed => 0.0,
      AnimationStatus.forward || AnimationStatus.completed => 1.0,
    };
    if (_target == nextTarget) return;
    _target = nextTarget;
    _spring.animateWith(
      SpringSimulation(
        _criticalSpring,
        _spring.value,
        nextTarget,
        _spring.velocity,
      ),
    );
  }

  @override
  void dispose() {
    widget.animation.removeStatusListener(_handleStatus);
    _spring.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (MediaQuery.disableAnimationsOf(context)) {
      return FadeTransition(opacity: widget.animation, child: widget.child);
    }
    return AnimatedBuilder(
      animation: _spring,
      child: widget.child,
      builder: (context, child) {
        final value = _spring.value.clamp(0.0, 1.0);
        return Opacity(
          opacity: value,
          child: FractionalTranslation(
            translation: Offset(
              widget.beginOffset.dx * (1 - value),
              widget.beginOffset.dy * (1 - value),
            ),
            child: Transform.scale(
              scale: widget.beginScale + (1 - widget.beginScale) * value,
              child: child,
            ),
          ),
        );
      },
    );
  }
}

Future<T?> showSpringDialog<T extends Object?>({
  required BuildContext context,
  required WidgetBuilder builder,
  bool useRootNavigator = true,
  bool barrierDismissible = false,
}) {
  return showDialog<T>(
    context: context,
    builder: builder,
    useRootNavigator: useRootNavigator,
    barrierDismissible: barrierDismissible,
    transitionDuration: const Duration(milliseconds: 280),
    transitionBuilder: (context, animation, secondaryAnimation, child) {
      return SpringMotionTransition(animation: animation, child: child);
    },
  );
}

class SpringProgressBar extends StatefulWidget {
  const SpringProgressBar({super.key, required this.value});

  final double value;

  @override
  State<SpringProgressBar> createState() => _SpringProgressBarState();
}

class _SpringProgressBarState extends State<SpringProgressBar>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController.unbounded(
    vsync: this,
    value: widget.value.clamp(0, 100),
  );

  @override
  void didUpdateWidget(covariant SpringProgressBar oldWidget) {
    super.didUpdateWidget(oldWidget);
    final target = widget.value.clamp(0, 100).toDouble();
    if (target == oldWidget.value.clamp(0, 100)) return;
    _controller.animateWith(
      SpringSimulation(
        _criticalSpring,
        _controller.value,
        target,
        _controller.velocity,
      ),
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (MediaQuery.disableAnimationsOf(context)) {
      return ProgressBar(value: widget.value.clamp(0, 100));
    }
    return AnimatedBuilder(
      animation: _controller,
      builder: (context, _) =>
          ProgressBar(value: _controller.value.clamp(0, 100)),
    );
  }
}

class SpringAnimatedDouble extends StatefulWidget {
  const SpringAnimatedDouble({
    super.key,
    required this.value,
    required this.builder,
    this.child,
  });

  final double value;
  final Widget? child;
  final Widget Function(BuildContext context, double value, Widget? child)
  builder;

  @override
  State<SpringAnimatedDouble> createState() => _SpringAnimatedDoubleState();
}

class _SpringAnimatedDoubleState extends State<SpringAnimatedDouble>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController.unbounded(
    vsync: this,
    value: widget.value,
  );

  @override
  void didUpdateWidget(covariant SpringAnimatedDouble oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.value == oldWidget.value) return;
    _controller.animateWith(
      SpringSimulation(
        _criticalSpring,
        _controller.value,
        widget.value,
        _controller.velocity,
      ),
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (MediaQuery.disableAnimationsOf(context)) {
      return widget.builder(context, widget.value, widget.child);
    }
    return AnimatedBuilder(
      animation: _controller,
      child: widget.child,
      builder: (context, child) =>
          widget.builder(context, _controller.value, child),
    );
  }
}
