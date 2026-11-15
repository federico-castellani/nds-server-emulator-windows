cmake_minimum_required(VERSION 3.16)
project(ndscore LANGUAGES C CXX)

if(NOT WIN32)
    message(FATAL_ERROR "ndscore targets Windows only")
endif()

set(MELONDS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../external/melonDS" CACHE PATH "Path to the melonDS source tree")

find_package(PkgConfig REQUIRED)
pkg_check_modules(SDL2 REQUIRED IMPORTED_TARGET sdl2)

# melonDS core only: no Qt frontend, no OpenGL, no GDB stub.
set(BUILD_QT_SDL OFF CACHE BOOL "" FORCE)
set(ENABLE_OGLRENDERER OFF CACHE BOOL "" FORCE)
set(ENABLE_GDBSTUB OFF CACHE BOOL "" FORCE)
set(ENABLE_LTO OFF CACHE BOOL "" FORCE)
set(ENABLE_LTO_RELEASE OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

list(APPEND CMAKE_MODULE_PATH "${MELONDS_DIR}/cmake")
add_subdirectory(${MELONDS_DIR} melonDS EXCLUDE_FROM_ALL)
add_subdirectory(${MELONDS_DIR}/src/net melonDS-net EXCLUDE_FROM_ALL)

add_library(ndscore SHARED
    src/ndscore.cpp
    src/Platform.cpp
    src/SlirpDriver.cpp
)

target_include_directories(ndscore PRIVATE include ${MELONDS_DIR}/src ${MELONDS_DIR}/src/net)
target_compile_definitions(ndscore PRIVATE NDSCORE_BUILD SDL_MAIN_HANDLED WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(ndscore PRIVATE core net-utils slirp PkgConfig::SDL2 ws2_32 iphlpapi)

# Produce ndscore.dll without the "lib" prefix and keep the MinGW runtime inside it.
set_target_properties(ndscore PROPERTIES PREFIX "" OUTPUT_NAME "ndscore")
target_link_options(ndscore PRIVATE -static-libgcc -static-libstdc++ -Wl,-Bstatic -lwinpthread -Wl,-Bdynamic)

# Network regression test (not part of the normal build): cmake --build build --target nettest
add_executable(nettest EXCLUDE_FROM_ALL tests/nettest.cpp src/SlirpDriver.cpp src/Platform.cpp)
target_include_directories(nettest PRIVATE include ${MELONDS_DIR}/src ${MELONDS_DIR}/src/net)
target_compile_definitions(nettest PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(nettest PRIVATE net-utils slirp ws2_32 iphlpapi)
target_link_options(nettest PRIVATE -static-libgcc -static-libstdc++)
