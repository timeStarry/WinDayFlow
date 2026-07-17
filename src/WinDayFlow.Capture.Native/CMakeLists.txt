cmake_minimum_required(VERSION 3.25)

project(WinDayFlowCaptureNative VERSION 0.1.0 LANGUAGES C CXX)

if(NOT WIN32)
  message(FATAL_ERROR "WinDayFlow.Capture.Native supports Windows only.")
endif()

include(CTest)

add_library(windayflow_capture_core STATIC
  internal/capture_event_queue.cpp
  internal/capture_policy.cpp
  internal/capture_runtime_owner.cpp
  internal/capture_safety_core.cpp
  internal/pixel_buffer.cpp
  internal/privacy_guard.cpp)

target_compile_features(windayflow_capture_core PUBLIC cxx_std_20)
target_include_directories(windayflow_capture_core
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/internal)
target_link_libraries(windayflow_capture_core PUBLIC bcrypt)

add_library(windayflow_capture SHARED
  src/capture_c_api.cpp
  src/windayflow_capture.def)

target_compile_features(windayflow_capture PRIVATE cxx_std_20)
target_compile_definitions(windayflow_capture PRIVATE WDF_CAPTURE_BUILD)
target_include_directories(windayflow_capture
  PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
  PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/internal)
target_link_libraries(windayflow_capture
  PRIVATE
    windayflow_capture_core
    d3d11
    dxgi
    mf
    mfplat
    mfreadwrite
    mfuuid
    ole32
    oleaut32
    psapi
    version
    windowscodecs)

foreach(target windayflow_capture_core windayflow_capture)
  target_compile_definitions(${target} PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
  if(MSVC)
    target_compile_options(${target} PRIVATE
      /W4 /WX /permissive- /EHsc /guard:cf /utf-8)
  endif()
endforeach()

if(MSVC)
  target_link_options(windayflow_capture PRIVATE /guard:cf)
endif()

set_target_properties(windayflow_capture PROPERTIES
  OUTPUT_NAME "WinDayFlow.Capture.Native")

if(BUILD_TESTING)
  function(add_windayflow_native_test target source)
    add_executable(${target} ${source})
    target_compile_features(${target} PRIVATE cxx_std_20)
    target_include_directories(${target} PRIVATE
      ${CMAKE_CURRENT_SOURCE_DIR}/include
      ${CMAKE_CURRENT_SOURCE_DIR}/internal)
    target_compile_definitions(${target} PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    if(MSVC)
      target_compile_options(${target} PRIVATE
        /W4 /WX /permissive- /EHsc /guard:cf /utf-8)
      target_link_options(${target} PRIVATE /guard:cf)
    endif()
    add_test(NAME ${target} COMMAND ${target})
    set_tests_properties(${target} PROPERTIES TIMEOUT 15)
  endfunction()

  add_windayflow_native_test(pixel_buffer_tests tests/pixel_buffer_tests.cpp)
  target_link_libraries(pixel_buffer_tests PRIVATE windayflow_capture_core)

  add_windayflow_native_test(capture_policy_tests tests/capture_policy_tests.cpp)
  target_link_libraries(capture_policy_tests PRIVATE windayflow_capture_core)

  add_windayflow_native_test(capture_event_queue_tests tests/capture_event_queue_tests.cpp)
  target_link_libraries(capture_event_queue_tests PRIVATE windayflow_capture_core)

  add_windayflow_native_test(capture_safety_core_tests tests/capture_safety_core_tests.cpp)
  target_link_libraries(capture_safety_core_tests PRIVATE windayflow_capture_core)

  add_windayflow_native_test(capture_c_api_tests tests/capture_c_api_tests.cpp)
  target_link_libraries(capture_c_api_tests PRIVATE windayflow_capture)

  add_executable(c_header_compatibility_test
    tests/c_header_compatibility_test.c)
  set_target_properties(c_header_compatibility_test PROPERTIES
    C_STANDARD 17
    C_STANDARD_REQUIRED YES
    C_EXTENSIONS NO)
  target_include_directories(c_header_compatibility_test PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include)
  target_compile_definitions(c_header_compatibility_test PRIVATE
    NOMINMAX WIN32_LEAN_AND_MEAN)
  target_link_libraries(c_header_compatibility_test PRIVATE
    windayflow_capture)
  if(MSVC)
    target_compile_options(c_header_compatibility_test PRIVATE
      /W4 /WX /guard:cf /utf-8)
    target_link_options(c_header_compatibility_test PRIVATE /guard:cf)
  endif()
  add_test(NAME c_header_compatibility_test
    COMMAND c_header_compatibility_test)
  set_tests_properties(c_header_compatibility_test PROPERTIES TIMEOUT 15)
endif()
