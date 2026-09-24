import 'package:fluent_ui/fluent_ui.dart';

import '../widgets/empty_state.dart';
import '../widgets/confirm_delete_dialog.dart';
import '../widgets/spring_motion.dart';
import 'transcription_queue.dart';

class TranscriptionQueuePanel extends StatelessWidget {
  const TranscriptionQueuePanel({
    super.key,
    required this.queue,
    this.onPickAudio,
    this.onLoadSrt,
    this.onTaskRemoved,
  });

  final TranscriptionQueue queue;
  final VoidCallback? onPickAudio;
  final void Function(TranscriptionJob job)? onLoadSrt;
  final ValueChanged<String>? onTaskRemoved;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: queue,
      builder: (context, _) {
        final jobs = queue.jobs;
        if (jobs.isEmpty) {
          return EmptyState(
            icon: FluentIcons.sync,
            title: '暂无转写任务',
            message: '在教师端项目中提交音频后，可随时从这里查看进度。',
            action: onPickAudio == null
                ? const SizedBox.shrink()
                : Button(onPressed: onPickAudio, child: const Text('选择音频')),
          );
        }

        final hasFinished = jobs.any((job) => job.isTerminal);
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Text(
                  '转写队列',
                  style: FluentTheme.of(context).typography.bodyStrong,
                ),
                const SizedBox(width: 8),
                Text(
                  '${jobs.length} 个任务',
                  style: FluentTheme.of(context).typography.caption,
                ),
                const Spacer(),
                if (hasFinished)
                  Button(
                    onPressed: queue.clearFinished,
                    child: const Text('清除已完成'),
                  ),
              ],
            ),
            if (queue.interruptedCount > 0) ...[
              const SizedBox(height: 10),
              InfoBar(
                title: const Text('有未完成的任务'),
                content: Row(
                  children: [
                    Text('上次退出时还有 ${queue.interruptedCount} 个任务没有结束。'),
                    const SizedBox(width: 12),
                    Button(
                      onPressed: queue.resumeInterrupted,
                      child: const Text('继续'),
                    ),
                  ],
                ),
                severity: InfoBarSeverity.warning,
                isLong: true,
              ),
            ],
            const SizedBox(height: 12),
            for (final job in jobs)
              _QueueTile(
                job: job,
                onCancel: () => queue.cancel(job.id),
                onRemove: () async {
                  final confirmed = await confirmPermanentDelete(
                    context: context,
                    title: '删除转写任务？',
                    message: job.isActive
                        ? '将取消并永久删除「${job.title}」。'
                        : '将永久删除「${job.title}」的任务记录。',
                  );
                  if (confirmed) {
                    queue.delete(job.id);
                    onTaskRemoved?.call(job.title);
                  }
                },
                onLoadSrt: onLoadSrt == null ? null : () => onLoadSrt!(job),
              ),
          ],
        );
      },
    );
  }
}

class _QueueTile extends StatelessWidget {
  const _QueueTile({
    required this.job,
    required this.onCancel,
    required this.onRemove,
    this.onLoadSrt,
  });

  final TranscriptionJob job;
  final VoidCallback onCancel;
  final Future<void> Function() onRemove;
  final VoidCallback? onLoadSrt;

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final segments = _segmentsLabel(job);
    final elapsed = _elapsedLabel(job);
    final loadableSrt =
        job.status == TranscriptionJobStatus.completed &&
        !job.srtConsumed &&
        (job.srt?.isNotEmpty ?? false);

    final color = _statusColor(theme, job);
    final percentage = ((job.fraction ?? 0) * 100).clamp(0, 100).round();
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Card(
        padding: const EdgeInsets.all(14),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(_statusIcon(job), color: color, size: 20),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          job.title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: theme.typography.bodyStrong,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Text(_statusLabel(job), style: TextStyle(color: color)),
                    ],
                  ),
                  const SizedBox(height: 4),
                  Text(
                    elapsed == null ? job.message : '${job.message}（$elapsed）',
                    style: theme.typography.caption,
                  ),
                  if (job.status == TranscriptionJobStatus.running) ...[
                    const SizedBox(height: 10),
                    Row(
                      children: [
                        Expanded(
                          child: SpringProgressBar(
                            value: percentage.toDouble(),
                          ),
                        ),
                        const SizedBox(width: 12),
                        SizedBox(
                          width: 42,
                          child: Text(
                            '$percentage%',
                            textAlign: TextAlign.end,
                            style: theme.typography.caption,
                          ),
                        ),
                      ],
                    ),
                  ],
                  if (segments != null) ...[
                    const SizedBox(height: 6),
                    Text(segments, style: theme.typography.caption),
                  ],
                  const SizedBox(height: 10),
                  Row(
                    children: [
                      if (loadableSrt) ...[
                        FilledButton(
                          onPressed: onLoadSrt,
                          child: const Text('载入字幕'),
                        ),
                        const SizedBox(width: 8),
                      ],
                      if (job.isActive) ...[
                        Button(onPressed: onCancel, child: const Text('取消')),
                        const SizedBox(width: 8),
                      ],
                      Button(
                        onPressed: () => onRemove(),
                        child: const Text('删除'),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  static Color _statusColor(FluentThemeData theme, TranscriptionJob job) {
    return switch (job.status) {
      TranscriptionJobStatus.completed => Colors.green,
      TranscriptionJobStatus.failed => Colors.red,
      TranscriptionJobStatus.canceled => Colors.grey,
      TranscriptionJobStatus.interrupted => Colors.orange,
      TranscriptionJobStatus.awaitingDecision => Colors.orange,
      TranscriptionJobStatus.queued => theme.accentColor,
      TranscriptionJobStatus.running => theme.accentColor,
    };
  }

  static IconData _statusIcon(TranscriptionJob job) => switch (job.status) {
    TranscriptionJobStatus.completed => FluentIcons.completed,
    TranscriptionJobStatus.failed => FluentIcons.error_badge,
    TranscriptionJobStatus.canceled => FluentIcons.cancel,
    TranscriptionJobStatus.interrupted => FluentIcons.warning,
    TranscriptionJobStatus.awaitingDecision => FluentIcons.help,
    TranscriptionJobStatus.queued => FluentIcons.clock,
    TranscriptionJobStatus.running => FluentIcons.sync,
  };

  static String _statusLabel(TranscriptionJob job) {
    return switch (job.status) {
      TranscriptionJobStatus.completed => '已完成',
      TranscriptionJobStatus.failed => '失败',
      TranscriptionJobStatus.canceled => '已取消',
      TranscriptionJobStatus.interrupted => '已中断',
      TranscriptionJobStatus.awaitingDecision => '等待确认',
      TranscriptionJobStatus.queued => '排队中',
      TranscriptionJobStatus.running => switch (job.stage) {
        TranscriptionStage.queued => '准备中',
        TranscriptionStage.fingerprinting => '校验中',
        TranscriptionStage.decoding => '解码中',
        TranscriptionStage.slicing => '切片中',
        TranscriptionStage.uploading => '上传中',
        TranscriptionStage.recognizing => '识别中',
        TranscriptionStage.formatting => '生成字幕',
        TranscriptionStage.merging => '合并中',
      },
    };
  }

  static String? _segmentsLabel(TranscriptionJob job) {
    final total = job.segmentTotal;
    if (total == null || total <= 0) return null;
    final done = job.segmentIndex ?? 0;
    return '分段 $done/$total';
  }

  static String? _elapsedLabel(TranscriptionJob job) {
    final startedAt = job.startedAt;
    if (startedAt == null || !job.fractionIsEstimated) return null;
    final seconds = DateTime.now().difference(startedAt).inSeconds;
    if (seconds < 5) return null;
    final minutes = seconds ~/ 60;
    final remainder = (seconds % 60).toString().padLeft(2, '0');
    return '已用 $minutes:$remainder';
  }
}
