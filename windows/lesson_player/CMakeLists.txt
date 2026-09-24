cmake_minimum_required(VERSION 3.14)
project(intensive_listening_lesson_launcher LANGUAGES CXX RC)

file(TO_CMAKE_PATH
  "${CMAKE_CURRENT_SOURCE_DIR}/../runner/resources/app_icon.ico"
  lesson_launcher_icon)
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/lesson_launcher.rc"
  "1 ICON \"${lesson_launcher_icon}\"\n")

add_executable(intensive_listening_lesson_launcher WIN32
  launcher.cpp
  "${CMAKE_CURRENT_BINARY_DIR}/lesson_launcher.rc"
  "lesson_player.exe.manifest"
)
target_compile_features(intensive_listening_lesson_launcher PRIVATE cxx_std_17)
target_compile_definitions(intensive_listening_lesson_launcher PRIVATE
  UNICODE _UNICODE NOMINMAX)
target_compile_options(intensive_listening_lesson_launcher PRIVATE
  /W4 /EHsc /utf-8)
target_link_libraries(intensive_listening_lesson_launcher PRIVATE
  shell32 comctl32)
set_property(TARGET intensive_listening_lesson_launcher PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set_target_properties(intensive_listening_lesson_launcher PROPERTIES
  OUTPUT_NAME "lesson_player_launcher")
