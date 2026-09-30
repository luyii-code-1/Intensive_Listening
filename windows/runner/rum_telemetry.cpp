#include "rum_telemetry.h"

#include <filesystem>
#include <system_error>
#include <vector>

namespace {
constexpr char kConfigAddress[] =
    "https://hm3xyft6jd-default-cn.rum.aliyuncs.com";
constexpr char kAppId[] = "hm3xyft6jd@76922d8db672517";

std::wstring ReadRegistryString(HKEY root, const wchar_t* key,
                                const wchar_t* value) {
  DWORD size = 0;
  if (RegGetValueW(root, key, value, RRF_RT_REG_SZ, nullptr, nullptr,
                   &size) != ERROR_SUCCESS ||
      size < sizeof(wchar_t)) {
    return {};
  }
  std::vector<wchar_t> buffer(size / sizeof(wchar_t));
  if (RegGetValueW(root, key, value, RRF_RT_REG_SZ, nullptr, buffer.data(),
                   &size) != ERROR_SUCCESS) {
    return {};
  }
  return buffer.data();
}

std::string Utf8(const std::wstring& text) {
  if (text.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, text.data(),
                                       static_cast<int>(text.size()), nullptr,
                                       0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string output(size, '\0');
  WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                      output.data(), size, nullptr, nullptr);
  return output;
}

std::wstring ExecutableDirectory() {
  std::vector<wchar_t> buffer(MAX_PATH);
  while (true) {
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(),
                                            static_cast<DWORD>(buffer.size()));
    if (length == 0) return {};
    if (length < buffer.size() - 1) {
      return std::filesystem::path(std::wstring(buffer.data(), length))
          .parent_path()
          .wstring();
    }
    buffer.resize(buffer.size() * 2);
  }
}

std::string WindowsVersion() {
  struct OsVersion {
    DWORD size;
    DWORD major;
    DWORD minor;
    DWORD build;
    DWORD platform;
    wchar_t service_pack[128];
  } version{};
  version.size = sizeof(version);
  const auto ntdll = GetModuleHandleW(L"ntdll.dll");
  const auto get_version = ntdll
      ? reinterpret_cast<LONG(WINAPI*)(OsVersion*)>(
            GetProcAddress(ntdll, "RtlGetVersion"))
      : nullptr;
  if (!get_version || get_version(&version) != 0) return "unknown";
  return std::to_string(version.major) + "." +
         std::to_string(version.minor) + "." +
         std::to_string(version.build);
}
}  // namespace

RumTelemetry::~RumTelemetry() {
  if (running_ && close_) close_();
  if (options_ && options_free_) options_free_(options_);
  if (library_) FreeLibrary(library_);
}

bool RumTelemetry::Start(const std::string& version,
                         const std::string& cache_path) {
  if (running_) return true;
  if (library_ && !Stop(cache_path)) return false;
  const auto directory = ExecutableDirectory();
  if (directory.empty()) return false;
  const auto dll = (std::filesystem::path(directory) / L"alibabacloud_rum.dll")
                       .wstring();
  library_ = LoadLibraryExW(dll.c_str(), nullptr,
                            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
                                LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (!library_) return false;
  const bool resolved =
      Resolve(options_new_, "alibabacloud_rum_options_new") &&
      Resolve(options_free_, "alibabacloud_rum_options_free") &&
      Resolve(set_config_address_, "alibabacloud_rum_options_set_config_address") &&
      Resolve(set_app_id_, "alibabacloud_rum_options_set_app_id") &&
      Resolve(set_app_name_, "alibabacloud_rum_options_set_app_name") &&
      Resolve(set_app_version_, "alibabacloud_rum_options_set_app_version") &&
      Resolve(set_cache_path_, "alibabacloud_rum_options_set_cache_path") &&
      Resolve(set_auto_curl_, "alibabacloud_rum_options_set_auto_curl_tracking") &&
      Resolve(set_auto_cef_, "alibabacloud_rum_options_set_auto_cef_tracking") &&
      Resolve(set_auto_crash_, "alibabacloud_rum_options_set_auto_crash_tracking") &&
      Resolve(init_, "alibabacloud_rum_init") &&
      Resolve(close_, "alibabacloud_rum_close") &&
      Resolve(event_new_, "alibabacloud_rum_custom_event_new") &&
      Resolve(event_add_extra_, "alibabacloud_rum_custom_event_add_extra") &&
      Resolve(event_report_, "alibabacloud_rum_custom_event_report") &&
      Resolve(log_new_, "alibabacloud_rum_custom_log_new") &&
      Resolve(log_set_log_, "alibabacloud_rum_custom_log_set_log") &&
      Resolve(log_report_, "alibabacloud_rum_custom_log_report");
  if (!resolved) {
    FreeLibrary(library_);
    library_ = nullptr;
    return false;
  }
  options_ = options_new_();
  if (!options_) {
    FreeLibrary(library_);
    library_ = nullptr;
    return false;
  }
  set_config_address_(options_, kConfigAddress);
  set_app_id_(options_, kAppId);
  set_app_name_(options_, "Intensive Listening");
  set_app_version_(options_, version.c_str());
  set_cache_path_(options_, cache_path.c_str());
  set_auto_curl_(options_, 1);
  set_auto_cef_(options_, 0);
  set_auto_crash_(options_, 1);
  running_ = init_(options_) == 0;
  if (!running_) {
    options_free_(options_);
    options_ = nullptr;
    FreeLibrary(library_);
    library_ = nullptr;
  }
  return running_;
}

bool RumTelemetry::Stop(const std::string& cache_path) {
  running_ = false;
  if (library_) {
    if (close_ && close_() != 0) return false;
    if (options_ && options_free_) options_free_(options_);
    options_ = nullptr;
    FreeLibrary(library_);
    library_ = nullptr;
  }
  std::error_code error;
  std::filesystem::remove_all(std::filesystem::u8path(cache_path), error);
  return !error;
}

bool RumTelemetry::Event(
    const std::string& name,
    const std::map<std::string, std::string>& fields) {
  if (!running_) return false;
  auto* event = event_new_("intensive_listening", name.c_str());
  if (!event) return false;
  for (const auto& [key, value] : fields) {
    event_add_extra_(event, key.c_str(), value.c_str());
  }
  event_report_(event);
  return true;
}

bool RumTelemetry::ErrorLog(const std::string& text) {
  if (!running_) return false;
  auto* log = log_new_("app_error", "error_notice");
  if (!log) return false;
  log_set_log_(log, 4, text.c_str());  // ALIBABACLOUD_RUM_LOG_ERROR
  log_report_(log);
  return true;
}

std::string RumTelemetry::InstallCycle() const {
  return Utf8(ReadRegistryString(HKEY_LOCAL_MACHINE,
                                 L"Software\\Intensive Listening",
                                 L"InstallCycle"));
}

std::string RumTelemetry::InstallUuid() const {
  return Utf8(ReadRegistryString(HKEY_LOCAL_MACHINE,
                                 L"Software\\Intensive Listening",
                                 L"InstallUuid"));
}

std::map<std::string, std::string> RumTelemetry::SystemProfile() const {
  std::map<std::string, std::string> profile;
  profile["windows_version"] = WindowsVersion();
  SYSTEM_INFO system{};
  GetNativeSystemInfo(&system);
  profile["cpu_arch"] =
      system.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64
          ? "x64"
          : system.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_ARM64
                ? "arm64"
                : "other";
  profile["logical_cores"] = std::to_string(system.dwNumberOfProcessors);
  profile["cpu_model"] = Utf8(ReadRegistryString(
      HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
      L"ProcessorNameString"));
  if (profile["cpu_model"].empty()) profile["cpu_model"] = "unknown";
  profile["gpu_model"] = "unknown";
  DISPLAY_DEVICEW display{};
  display.cb = sizeof(display);
  for (DWORD index = 0; EnumDisplayDevicesW(nullptr, index, &display, 0);
       ++index) {
    if (display.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) {
      profile["gpu_model"] = Utf8(display.DeviceString);
      break;
    }
    display = {};
    display.cb = sizeof(display);
  }
  MEMORYSTATUSEX memory{};
  memory.dwLength = sizeof(memory);
  if (GlobalMemoryStatusEx(&memory)) {
    profile["memory_mib"] =
        std::to_string(memory.ullTotalPhys / (1024ULL * 1024ULL));
  } else {
    profile["memory_mib"] = "unknown";
  }
  return profile;
}
