#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>

#include <algorithm>
#include <cwchar>
#include <exception>
#include <filesystem>
#include <fstream>
#include <string>
#include <system_error>
#include <vector>

#include "bundle_info.h"

namespace {
namespace fs = std::filesystem;

constexpr int kPayloadResource = 101;
const std::wstring kBundleId = ILP_BUNDLE_ID;
const std::wstring kAppVersion = ILP_APP_VERSION;

std::wstring Quote(const std::wstring& value) {
  std::wstring quoted = L"\"";
  size_t backslashes = 0;
  for (const wchar_t character : value) {
    if (character == L'\\') {
      ++backslashes;
      continue;
    }
    if (character == L'"') {
      quoted.append(backslashes * 2 + 1, L'\\');
      quoted += character;
      backslashes = 0;
      continue;
    }
    quoted.append(backslashes, L'\\');
    backslashes = 0;
    quoted += character;
  }
  quoted.append(backslashes * 2, L'\\');
  return quoted + L'"';
}

std::wstring ModulePath() {
  std::vector<wchar_t> buffer(MAX_PATH);
  while (true) {
    const DWORD size = GetModuleFileNameW(nullptr, buffer.data(),
                                          static_cast<DWORD>(buffer.size()));
    if (size == 0) return {};
    if (size < buffer.size()) return std::wstring(buffer.data(), size);
    buffer.resize(buffer.size() * 2);
  }
}

fs::path RuntimeRoot() {
  PWSTR local_app_data = nullptr;
  if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr,
                                  &local_app_data))) {
    return {};
  }
  const fs::path root = fs::path(local_app_data) / L"Intensive Listening";
  CoTaskMemFree(local_app_data);
  return root;
}

bool BundleFilesPresent(const fs::path& directory) {
  return fs::is_regular_file(directory / L"Intensive Listening.exe") &&
         fs::is_regular_file(directory / L"flutter_windows.dll") &&
         fs::is_regular_file(directory / L"data" / L"app.so");
}

bool BundleReady(const fs::path& directory) {
  return fs::is_regular_file(directory / (L".ready-" + kBundleId)) &&
         BundleFilesPresent(directory);
}

bool WritePayload(const fs::path& destination) {
  const HMODULE module = GetModuleHandleW(nullptr);
  const HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(kPayloadResource),
                                      RT_RCDATA);
  if (!resource) return false;
  const HGLOBAL loaded = LoadResource(module, resource);
  if (!loaded) return false;
  const auto* bytes = static_cast<const BYTE*>(LockResource(loaded));
  const DWORD size = SizeofResource(module, resource);
  if (!bytes || size == 0) return false;

  const HANDLE output = CreateFileW(destination.c_str(), GENERIC_WRITE, 0,
                                    nullptr, CREATE_ALWAYS,
                                    FILE_ATTRIBUTE_NORMAL, nullptr);
  if (output == INVALID_HANDLE_VALUE) return false;
  DWORD written_total = 0;
  while (written_total < size) {
    const DWORD chunk = std::min<DWORD>(size - written_total, 1024 * 1024);
    DWORD written = 0;
    if (!WriteFile(output, bytes + written_total, chunk, &written, nullptr) ||
        written == 0) {
      CloseHandle(output);
      return false;
    }
    written_total += written;
  }
  CloseHandle(output);
  return true;
}

bool ExtractPayload(const fs::path& archive, const fs::path& destination) {
  std::vector<wchar_t> system_directory(MAX_PATH);
  const UINT size = GetSystemDirectoryW(system_directory.data(),
                                       static_cast<UINT>(system_directory.size()));
  if (size == 0 || size >= system_directory.size()) return false;
  const fs::path tar = fs::path(system_directory.data()) / L"tar.exe";
  if (!fs::is_regular_file(tar)) return false;

  std::wstring command = Quote(tar.wstring()) + L" -xf " +
                         Quote(archive.wstring()) + L" -C " +
                         Quote(destination.wstring());
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESHOWWINDOW;
  startup.wShowWindow = SW_HIDE;
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(tar.c_str(), command.data(), nullptr, nullptr, FALSE,
                      CREATE_NO_WINDOW, nullptr, destination.c_str(), &startup,
                      &process)) {
    return false;
  }
  WaitForSingleObject(process.hProcess, INFINITE);
  DWORD exit_code = 1;
  const bool success = GetExitCodeProcess(process.hProcess, &exit_code) &&
                       exit_code == 0;
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return success;
}

bool EnsureBundle(const fs::path& directory, std::wstring* error) {
  const std::wstring mutex_name =
      L"Local\\IntensiveListeningPortable_" + kAppVersion;
  const HANDLE mutex = CreateMutexW(nullptr, FALSE, mutex_name.c_str());
  if (!mutex) {
    *error = L"无法准备运行目录。";
    return false;
  }
  const DWORD wait = WaitForSingleObject(mutex, INFINITE);
  if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
    CloseHandle(mutex);
    *error = L"无法取得运行目录。";
    return false;
  }

  bool success = false;
  fs::path staging;
  try {
    if (BundleReady(directory)) {
      success = true;
    } else {
      staging = directory.parent_path() /
                (L".extract-" + kBundleId + L"-" +
                 std::to_wstring(GetCurrentProcessId()) + L"-" +
                 std::to_wstring(GetTickCount64()));
      fs::create_directories(staging);
      const fs::path archive = staging / L"bundle.zip";
      if (!WritePayload(archive) || !ExtractPayload(archive, staging)) {
        *error = L"无法解压应用文件。";
      } else {
        fs::remove(archive);
        if (!BundleFilesPresent(staging)) {
          *error = L"应用文件不完整。";
        } else {
          std::ofstream marker(staging / (L".ready-" + kBundleId));
          marker.put('1');
          marker.close();
          if (!marker.good()) {
            *error = L"无法写入应用缓存。";
          } else {
            if (fs::exists(directory)) fs::remove_all(directory);
            fs::rename(staging, directory);
            success = true;
          }
        }
      }
    }
  } catch (const std::exception&) {
    *error = L"无法写入应用缓存。";
  }
  if (!success && !staging.empty()) {
    std::error_code ignored;
    fs::remove_all(staging, ignored);
  }
  ReleaseMutex(mutex);
  CloseHandle(mutex);
  return success;
}

int ReplaceLauncher(const std::wstring& running_launcher,
                    const std::wstring& target_launcher,
                    std::wstring* error) {
  const fs::path source(running_launcher);
  const fs::path target(target_launcher);
  if (!fs::is_regular_file(target) ||
      _wcsicmp(source.c_str(), target.c_str()) == 0) {
    *error = L"更新目标无效。";
    return 1;
  }

  const fs::path temporary = target.parent_path() /
      (L".IntensiveListening-update-" +
       std::to_wstring(GetCurrentProcessId()) + L".exe");
  std::error_code file_error;
  fs::copy_file(source, temporary, fs::copy_options::overwrite_existing,
                file_error);
  if (file_error) {
    *error = L"无法写入原应用位置。";
    return 1;
  }

  bool replaced = false;
  for (int attempt = 0; attempt < 120; ++attempt) {
    if (MoveFileExW(temporary.c_str(), target.c_str(),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
      replaced = true;
      break;
    }
    Sleep(250);
  }
  if (!replaced) {
    fs::remove(temporary, file_error);
    *error = L"旧版程序仍在使用或原位置不可写。";
    return 1;
  }

  std::wstring command = Quote(target.wstring());
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(target.c_str(), command.data(), nullptr, nullptr,
                      FALSE, 0, nullptr, target.parent_path().c_str(),
                      &startup, &process)) {
    *error = L"新版本已写入，但无法重新启动。";
    return 1;
  }
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return 0;
}

int Launch(const fs::path& directory, const std::wstring& launcher,
           int argc, wchar_t** argv, std::wstring* error) {
  const fs::path executable = directory / L"Intensive Listening.exe";
  std::wstring command = Quote(executable.wstring());
  for (int index = 1; index < argc; ++index) {
    command += L" ";
    command += Quote(argv[index]);
  }
  if (!SetEnvironmentVariableW(L"ILP_PORTABLE_LAUNCHER",
                               launcher.c_str())) {
    *error = L"无法设置启动环境。";
    return 1;
  }

  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  const std::wstring working_directory = directory.wstring();
  if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr,
                      FALSE, 0, nullptr,
                      working_directory.c_str(), &startup, &process)) {
    *error = L"无法启动应用程序。";
    return 1;
  }
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return 0;
}

void ReportError(const std::wstring& message) {
  MessageBoxW(nullptr, message.c_str(), L"Intensive Listening",
              MB_OK | MB_ICONERROR);
}
}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
  int argc = 0;
  wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  if (!argv) return 1;
  const std::wstring launcher = ModulePath();
  if (argc == 3 && wcscmp(argv[1], L"--replace-launcher") == 0) {
    std::wstring update_error;
    const int result = ReplaceLauncher(launcher, argv[2], &update_error);
    if (!update_error.empty()) ReportError(update_error);
    LocalFree(argv);
    return result;
  }
  const fs::path root = RuntimeRoot();
  if (launcher.empty() || root.empty()) {
    ReportError(L"无法确定应用运行目录。");
    LocalFree(argv);
    return 1;
  }
  const fs::path directory = root / kAppVersion;
  SetEnvironmentVariableW(L"ILP_APP_VERSION", kAppVersion.c_str());
  std::wstring error;
  if (!EnsureBundle(directory, &error)) {
    ReportError(error);
    LocalFree(argv);
    return 1;
  }
  const int result = Launch(directory, launcher, argc, argv, &error);
  if (!error.empty()) ReportError(error);
  LocalFree(argv);
  return result;
}
