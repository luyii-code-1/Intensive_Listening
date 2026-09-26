#include "flutter_window.h"

#include <shellapi.h>

#include <optional>
#include <variant>

#include "flutter/generated_plugin_registrant.h"
#include "flutter/standard_method_codec.h"
#include "resource.h"

namespace {
constexpr UINT kTrayMessage = WM_APP + 42;
constexpr UINT kTrayRestore = 4101;
constexpr UINT kTrayExit = 4102;
}

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  taskbar_created_message_ = RegisterWindowMessageW(L"TaskbarCreated");
  window_channel_ =
      std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
          flutter_controller_->engine()->messenger(),
          "intensive_listening/window",
          &flutter::StandardMethodCodec::GetInstance());
  window_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        if (call.method_name() == "enableTray") {
          const auto* enabled = std::get_if<bool>(call.arguments());
          if (!enabled) {
            result->Error("invalid_argument", "Expected a boolean.");
          } else if (!UpdateTrayIcon(*enabled)) {
            result->Error("tray_unavailable", "Windows could not create the tray icon.");
          } else {
            result->Success();
          }
        } else if (call.method_name() == "showWindow") {
          RestoreFromTray();
          result->Success();
        } else {
          result->NotImplemented();
        }
      });
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  UpdateTrayIcon(false);
  window_channel_ = nullptr;
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

bool FlutterWindow::UpdateTrayIcon(bool enabled) {
  if (!GetHandle()) return false;
  if (enabled == tray_enabled_) return true;
  NOTIFYICONDATAW icon{};
  icon.cbSize = sizeof(icon);
  icon.hWnd = GetHandle();
  icon.uID = 1;
  icon.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
  icon.uCallbackMessage = kTrayMessage;
  icon.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_APP_ICON));
  wcscpy_s(icon.szTip, L"Intensive Listening");
  if (enabled) {
    if (!Shell_NotifyIconW(NIM_ADD, &icon)) return false;
    icon.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &icon);
  } else {
    Shell_NotifyIconW(NIM_DELETE, &icon);
  }
  tray_enabled_ = enabled;
  return true;
}

void FlutterWindow::RestoreFromTray() {
  if (!GetHandle()) return;
  ShowWindow(GetHandle(), SW_RESTORE);
  SetForegroundWindow(GetHandle());
  if (flutter_controller_) flutter_controller_->ForceRedraw();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_SHOWWINDOW:
      if (wparam && flutter_controller_) flutter_controller_->ForceRedraw();
      break;
    case WM_ACTIVATE:
      if (LOWORD(wparam) != WA_INACTIVE && flutter_controller_) {
        flutter_controller_->ForceRedraw();
      }
      break;
    case WM_CLOSE:
      if (tray_enabled_) {
        ShowWindow(hwnd, SW_HIDE);
        return 0;
      }
      break;
    case kTrayMessage:
      if (LOWORD(lparam) == WM_LBUTTONUP || LOWORD(lparam) == WM_LBUTTONDBLCLK) {
        RestoreFromTray();
        return 0;
      }
      if (LOWORD(lparam) == WM_RBUTTONUP) {
        HMENU menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, kTrayRestore, L"打开 Intensive Listening");
        AppendMenuW(menu, MF_STRING, kTrayExit, L"退出");
        POINT cursor{};
        GetCursorPos(&cursor);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, hwnd, nullptr);
        DestroyMenu(menu);
        return 0;
      }
      break;
    case WM_COMMAND:
      if (LOWORD(wparam) == kTrayRestore) {
        RestoreFromTray();
        return 0;
      }
      if (LOWORD(wparam) == kTrayExit) {
        UpdateTrayIcon(false);
        DestroyWindow(hwnd);
        return 0;
      }
      break;
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  if (message == taskbar_created_message_ && tray_enabled_) {
    tray_enabled_ = false;
    UpdateTrayIcon(true);
    return 0;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
