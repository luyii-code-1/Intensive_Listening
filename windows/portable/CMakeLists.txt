cmake_minimum_required(VERSION 3.21)
cmake_policy(SET CMP0091 NEW)
project(intensive_listening_portable LANGUAGES CXX RC)

if(NOT WIN32 OR NOT MSVC)
  message(FATAL_ERROR "The portable launcher requires the Windows MSVC toolchain.")
endif()
if(NOT DEFINED ILP_PAYLOAD_ZIP OR NOT EXISTS "${ILP_PAYLOAD_ZIP}")
  message(FATAL_ERROR "ILP_PAYLOAD_ZIP must point to a Windows Release archive.")
endif()
string(LENGTH "${ILP_BUNDLE_ID}" bundle_id_length)
if(NOT bundle_id_length EQUAL 24 OR NOT ILP_BUNDLE_ID MATCHES "^[0-9a-f]+$")
  message(FATAL_ERROR "ILP_BUNDLE_ID must be a 24-character SHA-256 prefix.")
endif()
if(NOT ILP_APP_VERSION MATCHES "^[0-9]+[.][0-9]+[.][0-9]+$")
  message(FATAL_ERROR "ILP_APP_VERSION must be a three-part version number.")
endif()

configure_file(bundle_info.h.in "${CMAKE_CURRENT_BINARY_DIR}/bundle_info.h" @ONLY)
file(TO_CMAKE_PATH "${ILP_PAYLOAD_ZIP}" payload_zip)
file(TO_CMAKE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../runner/resources/app_icon.ico" app_icon)
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/payload.rc"
  "101 RCDATA \"${payload_zip}\"\n1 ICON \"${app_icon}\"\n")

add_executable(intensive_listening_portable WIN32
  launcher.cpp
  "${CMAKE_CURRENT_BINARY_DIR}/payload.rc"
)
target_compile_features(intensive_listening_portable PRIVATE cxx_std_17)
target_compile_definitions(intensive_listening_portable PRIVATE UNICODE _UNICODE NOMINMAX)
target_compile_options(intensive_listening_portable PRIVATE /W4 /EHsc /utf-8)
target_include_directories(intensive_listening_portable PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_link_libraries(intensive_listening_portable PRIVATE shell32 ole32 uuid)
set_property(TARGET intensive_listening_portable PROPERTY
  MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set_target_properties(intensive_listening_portable PROPERTIES
  OUTPUT_NAME "Intensive Listening"
)
