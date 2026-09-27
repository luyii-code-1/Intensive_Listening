#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <flutter/method_channel.h>
#include <flutter/encodable_value.h>

#include <memory>
#include <string>
#include <vector>

#include "win32_window.h"
#include "rum_telemetry.h"

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> window_channel_;
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> telemetry_channel_;
  RumTelemetry rum_telemetry_;
  bool tray_enabled_ = false;
  bool main_instance_ = false;
  bool dart_ready_for_packages_ = false;
  std::string package_uuid_;
  std::vector<std::string> pending_package_paths_;
  UINT taskbar_created_message_ = 0;

  bool UpdateTrayIcon(bool enabled);
  void RestoreFromTray();
  bool RoutePackageLaunch(const std::string& uuid, const std::string& path);
  void ReceivePackagePath(const std::string& path);
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
