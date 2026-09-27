#ifndef RUNNER_RUM_TELEMETRY_H_
#define RUNNER_RUM_TELEMETRY_H_

#include <windows.h>

#include <map>
#include <string>

// The vendor DLL is loaded only after the user enables telemetry. Keep the
// adapter's ABI limited to the documented C functions used by this app.
class RumTelemetry {
 public:
  RumTelemetry() = default;
  ~RumTelemetry();
  RumTelemetry(const RumTelemetry&) = delete;
  RumTelemetry& operator=(const RumTelemetry&) = delete;

  bool Start(const std::string& version, const std::string& cache_path);
  bool Stop(const std::string& cache_path);
  bool Event(const std::string& name,
             const std::map<std::string, std::string>& fields);
  bool ErrorLog(const std::string& text);
  std::string InstallCycle() const;
  std::map<std::string, std::string> SystemProfile() const;

 private:
  template <typename T>
  bool Resolve(T& target, const char* name) {
    target = reinterpret_cast<T>(GetProcAddress(library_, name));
    return target != nullptr;
  }

  HMODULE library_ = nullptr;
  void* options_ = nullptr;
  bool running_ = false;

  void* (*options_new_)() = nullptr;
  void (*options_free_)(void*) = nullptr;
  void (*set_config_address_)(void*, const char*) = nullptr;
  void (*set_app_id_)(void*, const char*) = nullptr;
  void (*set_app_name_)(void*, const char*) = nullptr;
  void (*set_app_version_)(void*, const char*) = nullptr;
  void (*set_cache_path_)(void*, const char*) = nullptr;
  void (*set_auto_curl_)(void*, int) = nullptr;
  void (*set_auto_cef_)(void*, int) = nullptr;
  void (*set_auto_crash_)(void*, int) = nullptr;
  int (*init_)(void*) = nullptr;
  int (*close_)() = nullptr;
  void* (*event_new_)(const char*, const char*) = nullptr;
  void (*event_add_extra_)(void*, const char*, const char*) = nullptr;
  void (*event_report_)(void*) = nullptr;
  void* (*log_new_)(const char*, const char*) = nullptr;
  void (*log_set_log_)(void*, int, const char*) = nullptr;
  void (*log_report_)(void*) = nullptr;
};

#endif  // RUNNER_RUM_TELEMETRY_H_
