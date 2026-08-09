# Exclr8CEF — top-level CMake config.
#
# Locates the CEF binary distribution placed at third_party/cef/<platform>/
# by scripts/download-cef.sh and includes subprojects.

cmake_minimum_required(VERSION 3.21)
project(exclr8cef CXX C)

# CEF 147 requires C++20.
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ---- Detect platform suffix used by download-cef.sh -----------------------
# EXCLR8CEF_PLATFORM env override wins so cross-compile (e.g. osx-x64 from an
# arm64 host with -DCMAKE_OSX_ARCHITECTURES=x86_64) picks up the right CEF
# binary distribution. Falls through to host detection otherwise.
if(DEFINED ENV{EXCLR8CEF_PLATFORM})
    set(_EX_PLATFORM "$ENV{EXCLR8CEF_PLATFORM}")
elseif(APPLE)
    if(CMAKE_OSX_ARCHITECTURES STREQUAL "x86_64")
        set(_EX_PLATFORM "macosx64")
    elseif(CMAKE_OSX_ARCHITECTURES STREQUAL "arm64")
        set(_EX_PLATFORM "macosarm64")
    elseif(CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "arm64")
        set(_EX_PLATFORM "macosarm64")
    else()
        set(_EX_PLATFORM "macosx64")
    endif()
elseif(WIN32)
    set(_EX_PLATFORM "windows64")
elseif(UNIX)
    if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(_EX_PLATFORM "linuxarm64")
    else()
        set(_EX_PLATFORM "linux64")
    endif()
endif()

set(EXCLR8CEF_CEF_ROOT
    "${CMAKE_SOURCE_DIR}/../third_party/cef/${_EX_PLATFORM}"
    CACHE PATH "Path to extracted CEF binary distribution")

if(NOT EXISTS "${EXCLR8CEF_CEF_ROOT}/include/cef_version.h")
    message(FATAL_ERROR
        "CEF binary distribution not found at:\n  ${EXCLR8CEF_CEF_ROOT}\n"
        "Run scripts/download-cef.sh first.")
endif()

message(STATUS "Exclr8CEF: using CEF at ${EXCLR8CEF_CEF_ROOT}")

list(APPEND CMAKE_MODULE_PATH "${EXCLR8CEF_CEF_ROOT}/cmake")
set(CEF_ROOT "${EXCLR8CEF_CEF_ROOT}")
find_package(CEF REQUIRED)

# CEF's Linux ARM64 binary distribution leaks x86-only compile/link flags
# (-m64, -march=x86-64) into the shared CEF_*_COMPILER_FLAGS variables, which
# the libcef_dll_wrapper subdir picks up via CEF's macros. GCC on aarch64
# rejects them with "unrecognized command-line option '-m64'". Strip them.
if(UNIX AND NOT APPLE AND CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
    foreach(_flag_var
            CEF_COMPILER_FLAGS
            CEF_C_COMPILER_FLAGS
            CEF_CXX_COMPILER_FLAGS
            CEF_LINKER_FLAGS
            CEF_C_LINKER_FLAGS
            CEF_CXX_LINKER_FLAGS)
        list(REMOVE_ITEM ${_flag_var} -m64 -march=x86-64)
    endforeach()
endif()

add_subdirectory("${CEF_LIBCEF_DLL_WRAPPER_PATH}" libcef_dll_wrapper)

# Define the imported `libcef_lib` target on platforms that link libcef
# directly (Windows / Linux). On macOS the shim doesn't reference this
# target — libcef.framework is loaded at runtime via CefScopedLibraryLoader.
if(NOT APPLE)
    ADD_LOGICAL_TARGET(libcef_lib "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")
endif()

# ---- Subprojects ----------------------------------------------------------
add_subdirectory(shim)
add_subdirectory(demo)
