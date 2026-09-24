#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <algorithm>
#include <string>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  RECT work_area{};
  ::SystemParametersInfo(SPI_GETWORKAREA, 0, &work_area, 0);
  const UINT dpi = ::GetDpiForSystem();
  const int work_width = work_area.right - work_area.left;
  const int work_height = work_area.bottom - work_area.top;
  Win32Window::Size size(
      static_cast<unsigned int>(std::min(1440, MulDiv(work_width - 48, 96, dpi))),
      static_cast<unsigned int>(std::min(900, MulDiv(work_height - 48, 96, dpi))));
  const int physical_width = MulDiv(size.width, dpi, 96);
  const int physical_height = MulDiv(size.height, dpi, 96);
  Win32Window::Point origin(
      static_cast<unsigned int>(std::max(
          0, MulDiv(work_area.left + (work_width - physical_width) / 2,
                    96, dpi))),
      static_cast<unsigned int>(std::max(
          0, MulDiv(work_area.top + (work_height - physical_height) / 2,
                    96, dpi))));
  if (!window.Create(L"Intensive Listening", origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  return EXIT_SUCCESS;
}
