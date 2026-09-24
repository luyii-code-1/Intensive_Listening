import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intensive_listening/widgets/stacked_info_bars.dart';

void main() {
  testWidgets(
    'success closes after five seconds while errors stay and copy details',
    (tester) async {
      final controller = StackedInfoBarController();
      addTearDown(controller.dispose);
      Object? copied;
      final messenger =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      messenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.setData') copied = call.arguments;
        return null;
      });
      addTearDown(
        () => messenger.setMockMethodCallHandler(SystemChannels.platform, null),
      );

      await tester.pumpWidget(
        FluentApp(
          home: Align(
            alignment: Alignment.bottomRight,
            child: SizedBox(
              width: 380,
              child: StackedInfoBarHost(controller: controller),
            ),
          ),
        ),
      );

      controller.show(title: '完成', message: '已完成');
      controller.show(
        title: '转写失败',
        message: 'API 返回错误详情',
        severity: InfoBarSeverity.error,
      );
      await tester.pumpAndSettle();
      expect(find.text('完成'), findsOneWidget);
      expect(find.text('转写失败'), findsOneWidget);

      await tester.pump(const Duration(seconds: 6));
      await tester.pumpAndSettle();
      expect(find.text('完成'), findsNothing);
      expect(find.text('转写失败'), findsOneWidget);

      await tester.tap(find.text('复制错误详情'));
      await tester.pump(const Duration(milliseconds: 200));
      expect(copied, {'text': '转写失败\nAPI 返回错误详情'});
      expect(find.text('已复制'), findsOneWidget);
    },
  );
}
