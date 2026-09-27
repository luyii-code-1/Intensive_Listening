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
constexpr ULONG_PTR kProbePackage = 0x494c5001;
constexpr ULONG_PTR kOpenPackage = 0x494c5002;
constexpr wchar_t kRunnerWindowClass[] = L"FLUTTER_RUNNER_WIN32_WINDOW";
constexpr wchar_t kRunnerWindowTitle[] = L"Intensive Listening";

std::optional<DWORD_PTR> SendPackageMessage(HWND window, ULONG_PTR kind,
                                            const std::string& payload) {
  COPYDATASTRUCT data{};
  data.dwData = kind;
  data.cbData = static_cast<DWORD>(payload.size() + 1);
  data.lpData = const_cast<char*>(payload.c_str());
  DWORD_PTR response = 0;
  if (!SendMessageTimeoutW(window, WM_COPYDATA, 0,
                           reinterpret_cast<LPARAM>(&data),
                           SMTO_ABORTIFHUNG | SMTO_BLOCK, 1500, &response)) {
    return std::nullopt;
  }
  return response;
}

const std::string* StringArgument(const flutter::EncodableMap* arguments,
                                  const char* key) {
  if (!arguments) return nullptr;
  const auto entry = arguments->find(flutter::EncodableValue(key));
  if (entry == arguments->end()) return nullptr;
  return std::get_if<std::string>(&entry->second);
}
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
        } else if (call.method_name() == "preparePackageInstance") {
          main_instance_ = false;
          result->Success();
        } else if (call.method_name() == "registerMainInstance") {
          main_instance_ = true;
          result->Success();
        } else if (call.method_name() == "routePackageLaunch") {
          const auto* arguments =
              std::get_if<flutter::EncodableMap>(call.arguments());
          if (!arguments) {
            result->Error("invalid_argument", "Expected package details.");
            return;
          }
          const auto uuid_entry = arguments->find(flutter::EncodableValue("uuid"));
          const auto path_entry = arguments->find(flutter::EncodableValue("path"));
          if (uuid_entry == arguments->end() || path_entry == arguments->end()) {
            result->Error("invalid_argument", "Missing package details.");
            return;
          }
          const auto* uuid = std::get_if<std::string>(&uuid_entry->second);
          const auto* path = std::get_if<std::string>(&path_entry->second);
          if (!uuid || uuid->empty() || !path || path->empty()) {
            result->Error("invalid_argument", "Invalid package details.");
            return;
          }
          package_uuid_ = *uuid;
          main_instance_ = false;
          result->Success(flutter::EncodableValue(
              RoutePackageLaunch(*uuid, *path)));
        } else if (call.method_name() == "readyForPackageOpens") {
          dart_ready_for_packages_ = true;
          result->Success();
          auto pending = std::move(pending_package_paths_);
          for (const auto& path : pending) {
            window_channel_->InvokeMethod(
                "externalOpenPackage",
                std::make_unique<flutter::EncodableValue>(path));
          }
        } else {
          result->NotImplemented();
        }
      });
  telemetry_channel_ =
      std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
          flutter_controller_->engine()->messenger(),
          "intensive_listening/telemetry",
          &flutter::StandardMethodCodec::GetInstance());
  telemetry_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        const auto* arguments =
            std::get_if<flutter::EncodableMap>(call.arguments());
        const auto& method = call.method_name();
        if (method == "start") {
          const auto* version = StringArgument(arguments, "version");
          const auto* cache = StringArgument(arguments, "cachePath");
          if (!version || !cache || cache->empty()) {
            result->Error("invalid_argument", "Missing telemetry options.");
            return;
          }
          result->Success(flutter::EncodableValue(
              rum_telemetry_.Start(*version, *cache)));
        } else if (method == "stop") {
          const auto* cache = StringArgument(arguments, "cachePath");
          if (!cache || cache->empty()) {
            result->Error("invalid_argument", "Missing telemetry cache path.");
          } else if (!rum_telemetry_.Stop(*cache)) {
            result->Error("cache_cleanup_failed", "Telemetry cache could not be cleared.");
          } else {
            result->Success();
          }
        } else if (method == "event") {
          const auto* name = StringArgument(arguments, "name");
          if (!name || !arguments) {
            result->Error("invalid_argument", "Missing telemetry event.");
            return;
          }
          const auto fields_entry =
              arguments->find(flutter::EncodableValue("fields"));
          if (fields_entry == arguments->end()) {
            result->Error("invalid_argument", "Missing telemetry fields.");
            return;
          }
          const auto* fields =
              std::get_if<flutter::EncodableMap>(&fields_entry->second);
          if (!fields) {
            result->Error("invalid_argument", "Invalid telemetry fields.");
            return;
          }
          std::map<std::string, std::string> values;
          for (const auto& [key, value] : *fields) {
            const auto* field_name = std::get_if<std::string>(&key);
            const auto* field_value = std::get_if<std::string>(&value);
            if (!field_name || !field_value) {
              result->Error("invalid_argument", "Telemetry fields must be strings.");
              return;
            }
            values[*field_name] = *field_value;
          }
          result->Success(flutter::EncodableValue(
              rum_telemetry_.Event(*name, values)));
        } else if (method == "errorLog") {
          const auto* message = StringArgument(arguments, "text");
          if (!message) {
            result->Error("invalid_argument", "Missing telemetry log.");
          } else {
            result->Success(flutter::EncodableValue(
                rum_telemetry_.ErrorLog(*message)));
          }
        } else if (method == "installCycle") {
          const auto cycle = rum_telemetry_.InstallCycle();
          if (cycle.empty()) {
            result->Success();
          } else {
            result->Success(flutter::EncodableValue(cycle));
          }
        } else if (method == "systemProfile") {
          flutter::EncodableMap profile;
          for (const auto& [key, value] : rum_telemetry_.SystemProfile()) {
            profile[flutter::EncodableValue(key)] = flutter::EncodableValue(value);
          }
          result->Success(flutter::EncodableValue(profile));
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
  telemetry_channel_ = nullptr;
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

bool FlutterWindow::RoutePackageLaunch(const std::string& uuid,
                                      const std::string& path) {
  std::vector<HWND> matching_packages;
  std::vector<HWND> main_windows;
  HWND candidate = nullptr;
  while ((candidate = FindWindowExW(nullptr, candidate, kRunnerWindowClass,
                                    kRunnerWindowTitle)) != nullptr) {
    if (candidate == GetHandle()) continue;
    const auto response = SendPackageMessage(candidate, kProbePackage, uuid);
    if (response == 1) {
      matching_packages.push_back(candidate);
    } else if (response == 2) {
      main_windows.push_back(candidate);
    }
  }
  for (const auto& window : matching_packages) {
    if (SendPackageMessage(window, kOpenPackage, path) == 1) return true;
  }
  for (const auto& window : main_windows) {
    if (SendPackageMessage(window, kOpenPackage, path) == 1) return true;
  }
  return false;
}

void FlutterWindow::ReceivePackagePath(const std::string& path) {
  pending_package_paths_.push_back(path);
  RestoreFromTray();
  if (!dart_ready_for_packages_) return;
  window_channel_->InvokeMethod(
      "externalOpenPackage",
      std::make_unique<flutter::EncodableValue>(path));
  pending_package_paths_.pop_back();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  if (message == WM_COPYDATA) {
    const auto* data = reinterpret_cast<const COPYDATASTRUCT*>(lparam);
    if (!data || !data->lpData || data->cbData < 2 || data->cbData > 131072) {
      return 0;
    }
    const auto* bytes = static_cast<const char*>(data->lpData);
    if (bytes[data->cbData - 1] != '\0') return 0;
    const std::string payload(bytes, data->cbData - 1);
    if (data->dwData == kProbePackage) {
      if (!package_uuid_.empty() && package_uuid_ == payload) return 1;
      return main_instance_ ? 2 : 0;
    }
    if (data->dwData == kOpenPackage) {
      ReceivePackagePath(payload);
      return 1;
    }
    return 0;
  }
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
