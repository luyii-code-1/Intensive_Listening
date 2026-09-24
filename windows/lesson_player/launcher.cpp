#include <windows.h>
#include <commctrl.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iterator>
#include <string>
#include <system_error>
#include <vector>

namespace {
namespace fs = std::filesystem;

constexpr char kFooterMagic[] = "ILPPLAYERPACKV1!";
constexpr std::uint64_t kFooterLength = 80;

#pragma pack(push, 1)
struct PackageFooter {
  char magic[16];
  std::uint64_t payload_offset;
  std::uint64_t payload_length;
  char package_id[48];
};
#pragma pack(pop)

static_assert(sizeof(PackageFooter) == kFooterLength);

constexpr wchar_t kStatusWindowClass[] =
    L"IntensiveListeningLessonStatusWindow";
constexpr int kCloseButtonId = 1001;

class StatusWindow {
 public:
  static void ShowErrorBox(const std::wstring& message) {
    MessageBoxW(nullptr, message.c_str(), L"Intensive Listening",
                MB_ICONERROR | MB_OK);
  }

  bool Create(HINSTANCE instance) {
    INITCOMMONCONTROLSEX controls{};
    controls.dwSize = sizeof(controls);
    controls.dwICC = ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES;
    InitCommonControlsEx(&controls);

    WNDCLASSEXW window_class{};
    window_class.cbSize = sizeof(window_class);
    window_class.hInstance = instance;
    window_class.lpfnWndProc = WindowProcedure;
    window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    window_class.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1));
    window_class.hIconSm = window_class.hIcon;
    window_class.hbrBackground =
        reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    window_class.lpszClassName = kStatusWindowClass;
    if (!RegisterClassExW(&window_class)) {
      const DWORD err = GetLastError();
      if (err != ERROR_CLASS_ALREADY_EXISTS) {
        ShowErrorBox(L"无法注册启动窗口类（错误代码: " + std::to_wstring(err) +
                     L"）。");
        return false;
      }
    }

    constexpr int width = 520;
    constexpr int height = 430;
    RECT work_area{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work_area, 0);
    const int x = work_area.left + (work_area.right - work_area.left - width) / 2;
    const int y = work_area.top + (work_area.bottom - work_area.top - height) / 2;
    window_ = CreateWindowExW(
        WS_EX_APPWINDOW, kStatusWindowClass, L"Intensive Listening",
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, x, y,
        width, height, nullptr, nullptr, instance, this);
    if (!window_) {
      ShowErrorBox(L"无法创建启动窗口（错误代码: " +
                   std::to_wstring(GetLastError()) + L"）。");
      return false;
    }

    title_ = CreateWindowExW(0, L"STATIC", L"正在准备独立精听包",
                             WS_CHILD | WS_VISIBLE, 34, 34, 440, 36, window_,
                             nullptr, instance, nullptr);
    status_ = CreateWindowExW(0, L"STATIC", L"正在检查课程文件…",
                              WS_CHILD | WS_VISIBLE, 34, 94, 440, 28, window_,
                              nullptr, instance, nullptr);
    progress_ = CreateWindowExW(0, PROGRESS_CLASSW, nullptr,
                                WS_CHILD | WS_VISIBLE | PBS_SMOOTH, 34, 134,
                                440, 18, window_, nullptr, instance, nullptr);
    details_ = CreateWindowExW(
        0, L"STATIC",
        L"首次运行时会自动解压播放器文件，完成后将打开课程。",
        WS_CHILD | WS_VISIBLE | SS_LEFT, 34, 180, 440, 112, window_, nullptr,
        instance, nullptr);
    close_button_ = CreateWindowExW(
        0, L"BUTTON", L"关闭", WS_CHILD | BS_PUSHBUTTON, 374, 320, 100, 34,
        window_, reinterpret_cast<HMENU>(kCloseButtonId), instance, nullptr);

    font_ = CreateFontW(-19, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                        CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                        DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    title_font_ = CreateFontW(-25, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                              DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                              CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                              DEFAULT_PITCH | FF_DONTCARE, L"Segoe UI");
    for (const HWND control : {status_, details_, close_button_}) {
      SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font_), TRUE);
    }
    SendMessageW(title_, WM_SETFONT, reinterpret_cast<WPARAM>(title_font_),
                 TRUE);
    SendMessageW(progress_, PBM_SETRANGE32, 0, 100);
    SendMessageW(progress_, PBM_SETPOS, 2, 0);
    ShowWindow(window_, SW_SHOW);
    UpdateWindow(window_);
    PumpMessages();
    return true;
  }

  ~StatusWindow() {
    if (font_) DeleteObject(font_);
    if (title_font_) DeleteObject(title_font_);
  }

  void Update(int progress, const std::wstring& message,
              const std::wstring& details = L"") {
    if (!window_) return;
    SendMessageW(progress_, PBM_SETPOS,
                 static_cast<WPARAM>(std::clamp(progress, 0, 100)), 0);
    SetWindowTextW(status_, message.c_str());
    if (!details.empty()) SetWindowTextW(details_, details.c_str());
    PumpMessages();
  }

  void ShowError(const std::wstring& message) {
    if (!window_) {
      ShowErrorBox(message);
      return;
    }
    allow_close_ = true;
    SetWindowTextW(title_, L"无法打开独立精听包");
    SetWindowTextW(status_, L"初始化未完成");
    SetWindowTextW(details_, message.c_str());
    ShowWindow(close_button_, SW_SHOW);
    SetFocus(close_button_);
    while (IsWindow(window_)) {
      MSG message_record{};
      const BOOL result = GetMessageW(&message_record, nullptr, 0, 0);
      if (result <= 0) break;
      TranslateMessage(&message_record);
      DispatchMessageW(&message_record);
    }
    window_ = nullptr;
  }

  void Finish() {
    Update(100, L"课程已准备完成", L"正在打开播放器…");
    for (int index = 0; index < 12; ++index) {
      PumpMessages();
      Sleep(25);
    }
    if (window_) {
      DestroyWindow(window_);
      window_ = nullptr;
    }
  }

  void PumpMessages() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
      TranslateMessage(&message);
      DispatchMessageW(&message);
    }
  }

 private:
  static LRESULT CALLBACK WindowProcedure(HWND window, UINT message,
                                          WPARAM w_param, LPARAM l_param) {
    auto* self = reinterpret_cast<StatusWindow*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
      const auto* create = reinterpret_cast<CREATESTRUCTW*>(l_param);
      self = static_cast<StatusWindow*>(create->lpCreateParams);
      SetWindowLongPtrW(window, GWLP_USERDATA,
                        reinterpret_cast<LONG_PTR>(self));
    }
    if (message == WM_COMMAND && LOWORD(w_param) == kCloseButtonId) {
      DestroyWindow(window);
      return 0;
    }
    if (message == WM_CLOSE) {
      if (self && !self->allow_close_) return 0;
      DestroyWindow(window);
      return 0;
    }
    if (message == WM_DESTROY) {
      PostQuitMessage(0);
      return 0;
    }
    return DefWindowProcW(window, message, w_param, l_param);
  }

  HWND window_ = nullptr;
  HWND title_ = nullptr;
  HWND status_ = nullptr;
  HWND progress_ = nullptr;
  HWND details_ = nullptr;
  HWND close_button_ = nullptr;
  HFONT font_ = nullptr;
  HFONT title_font_ = nullptr;
  bool allow_close_ = false;
};

using ProgressCallback =
    std::function<void(int, const std::wstring&, const std::wstring&)>;

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
  return quoted + L"\"";
}

fs::path ModulePath() {
  std::vector<wchar_t> buffer(MAX_PATH);
  while (true) {
    const DWORD size = GetModuleFileNameW(nullptr, buffer.data(),
                                          static_cast<DWORD>(buffer.size()));
    if (size == 0) return {};
    if (size < buffer.size()) return fs::path(std::wstring(buffer.data(), size));
    buffer.resize(buffer.size() * 2);
  }
}

fs::path TemporaryRoot() {
  std::vector<wchar_t> buffer(MAX_PATH);
  const DWORD size = GetTempPathW(static_cast<DWORD>(buffer.size()),
                                  buffer.data());
  if (size == 0) return {};
  if (size >= buffer.size()) {
    buffer.resize(size + 1);
    if (GetTempPathW(static_cast<DWORD>(buffer.size()), buffer.data()) == 0) {
      return {};
    }
  }
  return fs::path(buffer.data()) / L"Intensive Listening";
}

bool ValidIdentifierCharacter(const char value) {
  return (value >= 'a' && value <= 'z') ||
         (value >= 'A' && value <= 'Z') ||
         (value >= '0' && value <= '9') || value == '-' || value == '_' ||
         value == '.';
}

bool ReadFooter(const fs::path& executable, PackageFooter* footer,
                std::wstring* package_id) {
  std::ifstream input(executable, std::ios::binary | std::ios::ate);
  if (!input.good()) return false;
  const std::streamoff file_size = input.tellg();
  if (file_size < static_cast<std::streamoff>(kFooterLength)) return false;
  input.seekg(file_size - static_cast<std::streamoff>(kFooterLength));
  input.read(reinterpret_cast<char*>(footer), sizeof(*footer));
  if (!input.good() ||
      !std::equal(std::begin(footer->magic), std::end(footer->magic),
                  std::begin(kFooterMagic))) {
    return false;
  }
  if (footer->payload_length == 0 ||
      footer->payload_offset + footer->payload_length + kFooterLength !=
          static_cast<std::uint64_t>(file_size)) {
    return false;
  }
  size_t identifier_length = 0;
  while (identifier_length < std::size(footer->package_id) &&
         footer->package_id[identifier_length] != '\0') {
    if (!ValidIdentifierCharacter(footer->package_id[identifier_length])) {
      return false;
    }
    ++identifier_length;
  }
  if (identifier_length == 0 ||
      identifier_length == std::size(footer->package_id)) {
    return false;
  }
  package_id->clear();
  package_id->reserve(identifier_length);
  for (size_t index = 0; index < identifier_length; ++index) {
    package_id->push_back(static_cast<wchar_t>(
        static_cast<unsigned char>(footer->package_id[index])));
  }
  return true;
}

bool RuntimeFilesPresent(const fs::path& directory) {
  return fs::is_regular_file(directory / L"Intensive Listening.exe") &&
         fs::is_regular_file(directory / L"flutter_windows.dll") &&
         fs::is_regular_file(directory / L"data" / L"app.so") &&
         fs::is_regular_file(directory / L"data" / L"icudtl.dat") &&
         fs::is_regular_file(directory / L"lesson.ilp");
}

bool BundleReady(const fs::path& directory) {
  return fs::is_regular_file(directory / L".ready") &&
         RuntimeFilesPresent(directory);
}

bool CopyPayload(const fs::path& executable, const PackageFooter& footer,
                 const fs::path& destination,
                 const ProgressCallback& progress) {
  std::ifstream input(executable, std::ios::binary);
  std::ofstream output(destination, std::ios::binary | std::ios::trunc);
  if (!input.good() || !output.good()) return false;
  input.seekg(static_cast<std::streamoff>(footer.payload_offset));
  std::array<char, 1024 * 1024> buffer{};
  std::uint64_t remaining = footer.payload_length;
  while (remaining > 0) {
    const auto chunk = static_cast<std::streamsize>(
        std::min<std::uint64_t>(remaining, buffer.size()));
    input.read(buffer.data(), chunk);
    if (input.gcount() != chunk) return false;
    output.write(buffer.data(), chunk);
    if (!output.good()) return false;
    remaining -= static_cast<std::uint64_t>(chunk);
    const std::uint64_t copied = footer.payload_length - remaining;
    const int copy_progress =
        15 + static_cast<int>(33.0L * copied / footer.payload_length);
    progress(copy_progress, L"正在读取课程播放器文件…",
             L"正在准备运行所需文件，请稍候。");
  }
  output.flush();
  return output.good();
}

bool ExtractPayload(const fs::path& archive, const fs::path& destination,
                    const ProgressCallback& progress) {
  std::vector<wchar_t> system_directory(MAX_PATH);
  const UINT size = GetSystemDirectoryW(
      system_directory.data(), static_cast<UINT>(system_directory.size()));
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
  int extraction_progress = 52;
  while (WaitForSingleObject(process.hProcess, 80) == WAIT_TIMEOUT) {
    progress(std::min(extraction_progress, 84),
             L"正在解压课程播放器…",
             L"课程文件将在准备完成后自动打开。");
    ++extraction_progress;
  }
  DWORD exit_code = 1;
  const bool success = GetExitCodeProcess(process.hProcess, &exit_code) &&
                       exit_code == 0;
  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
  return success;
}

bool EnsureBundle(const fs::path& executable, const PackageFooter& footer,
                  const std::wstring& package_id, const fs::path& directory,
                  std::wstring* error, const ProgressCallback& progress) {
  progress(6, L"正在检查课程文件…",
           L"已缓存的播放器文件可以直接复用。");
  const std::wstring mutex_name =
      L"Local\\IntensiveListeningLesson_" + package_id;
  const HANDLE mutex = CreateMutexW(nullptr, FALSE, mutex_name.c_str());
  if (!mutex) {
    *error = L"无法准备课程播放器。";
    return false;
  }
  const DWORD wait = WaitForSingleObject(mutex, INFINITE);
  if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
    CloseHandle(mutex);
    *error = L"无法取得课程播放器运行目录。";
    return false;
  }

  bool success = false;
  fs::path staging;
  try {
    if (BundleReady(directory)) {
      progress(92, L"课程播放器已准备就绪",
               L"正在使用已缓存的运行文件。");
      success = true;
    } else {
      progress(12, L"正在创建课程运行目录…",
               L"首次打开会自动准备播放器文件。");
      staging = directory.parent_path() /
                (L".extract-" + package_id + L"-" +
                 std::to_wstring(GetCurrentProcessId()) + L"-" +
                 std::to_wstring(GetTickCount64()));
      fs::create_directories(staging);
      const fs::path archive = staging / L"bundle.zip";
      if (!CopyPayload(executable, footer, archive, progress) ||
          !ExtractPayload(archive, staging, progress)) {
        *error = L"无法解压课程播放器。";
      } else {
        progress(88, L"正在验证课程文件…",
                 L"正在确认播放器与课程数据完整。");
        fs::remove(archive);
        if (!RuntimeFilesPresent(staging)) {
          *error = L"课程播放器文件不完整。";
        } else {
          std::ofstream marker(staging / L".ready");
          marker.put('1');
          marker.close();
          if (!marker.good()) {
            *error = L"无法写入课程播放器缓存。";
          } else {
            if (fs::exists(directory)) {
              std::error_code remove_ec;
              fs::remove_all(directory, remove_ec);
            }
            bool renamed = false;
            for (int attempt = 0; attempt < 20; ++attempt) {
              std::error_code rename_ec;
              fs::rename(staging, directory, rename_ec);
              if (!rename_ec) {
                renamed = true;
                break;
              }
              Sleep(50);
            }
            if (!renamed) {
              *error = L"无法部署课程运行文件，文件可能被系统占用，请稍后重试。";
            } else {
              progress(94, L"课程文件已准备完成",
                       L"即将启动精听播放器。");
              success = true;
            }
          }
        }
      }
    }
  } catch (const std::exception&) {
    *error = L"无法写入 Windows 临时目录。";
  }
  if (!success && !staging.empty()) {
    std::error_code ignored;
    fs::remove_all(staging, ignored);
  }
  ReleaseMutex(mutex);
  CloseHandle(mutex);
  return success;
}

bool LaunchAndMonitorPlayer(const fs::path& directory, StatusWindow& status_window,
                            std::wstring* error) {
  const fs::path executable = directory / L"Intensive Listening.exe";
  const fs::path lesson = directory / L"lesson.ilp";
  const fs::path data = directory / L"lesson-data";
  const fs::path library = data / L"library";
  std::error_code directory_error;
  fs::create_directories(library, directory_error);
  if (directory_error) {
    *error = L"无法创建课程数据目录。";
    return false;
  }
  if (!SetEnvironmentVariableW(L"ILP_STANDALONE_LIBRARY",
                               library.c_str()) ||
      !SetEnvironmentVariableW(L"ILP_STANDALONE_DATA", data.c_str()) ||
      !SetEnvironmentVariableW(L"ILP_STANDALONE_LESSON", L"1")) {
    *error = L"无法准备课程运行环境。";
    return false;
  }
  std::wstring command = Quote(executable.wstring()) + L" " +
                         Quote(lesson.wstring());
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr,
                      FALSE, 0, nullptr, directory.c_str(), &startup,
                      &process)) {
    const DWORD err = GetLastError();
    *error = L"无法启动课程播放器（错误代码: " + std::to_wstring(err) + L"）。";
    return false;
  }
  CloseHandle(process.hThread);

  status_window.Update(98, L"正在启动精听播放器…",
                       L"播放器窗口正在加载，请稍候…");

  // Monitor child process: detect early exit/crash or wait until GUI is initialized.
  constexpr DWORD kMaxWaitMs = 12000;
  constexpr DWORD kPollIntervalMs = 80;
  DWORD elapsed_ms = 0;

  while (elapsed_ms < kMaxWaitMs) {
    const DWORD wait = WaitForSingleObject(process.hProcess, kPollIntervalMs);
    elapsed_ms += kPollIntervalMs;

    if (wait == WAIT_OBJECT_0) {
      // Child process terminated prematurely!
      DWORD exit_code = 0;
      GetExitCodeProcess(process.hProcess, &exit_code);
      CloseHandle(process.hProcess);
      wchar_t code_buf[32]{};
      swprintf_s(code_buf, L"0x%08X", exit_code);
      *error = std::wstring(L"课程播放器启动异常并已退出（错误代码: ") + code_buf +
               L"）。\n请确认系统环境包含完整的 Windows 运行库组件。";
      return false;
    }

    const DWORD idle_result = WaitForInputIdle(process.hProcess, 0);
    if (idle_result == 0) {
      break;
    }

    status_window.PumpMessages();
  }

  CloseHandle(process.hProcess);
  status_window.Finish();
  return true;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
  StatusWindow status_window;
  if (!status_window.Create(instance)) return 1;
  status_window.Update(3, L"正在验证独立精听包…",
                       L"正在读取课程信息。");
  const fs::path executable = ModulePath();
  PackageFooter footer{};
  std::wstring package_id;
  if (executable.empty() || !ReadFooter(executable, &footer, &package_id)) {
    status_window.ShowError(L"课程播放器包无效或不完整。");
    return 1;
  }
  const fs::path root = TemporaryRoot();
  if (root.empty()) {
    status_window.ShowError(L"无法确定 Windows 临时目录。");
    return 1;
  }
  const fs::path directory = root / package_id;
  const ProgressCallback progress =
      [&status_window](int value, const std::wstring& message,
                       const std::wstring& details) {
        status_window.Update(value, message, details);
      };
  std::wstring error;
  if (!EnsureBundle(executable, footer, package_id, directory, &error,
                    progress)) {
    status_window.ShowError(error.empty() ? L"无法准备课程。" : error);
    return 1;
  }
  if (!LaunchAndMonitorPlayer(directory, status_window, &error)) {
    status_window.ShowError(error.empty() ? L"无法打开课程。" : error);
    return 1;
  }
  return 0;
}
