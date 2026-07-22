cmake_minimum_required(VERSION 3.28)

project(ExternalPeepSightNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(EXTERNAL_PEEPSIGHT_ENABLE_ANALYZE "Enable MSVC static analysis" OFF)
option(EXTERNAL_PEEPSIGHT_ENABLE_ASAN "Enable AddressSanitizer" OFF)

if(MSVC AND EXTERNAL_PEEPSIGHT_ENABLE_ASAN)
    add_compile_options(/fsanitize=address)
    add_link_options(/INCREMENTAL:NO)
endif()

include(CTest)

add_subdirectory(src/ExternalPeepSight.Host)

if(BUILD_TESTING)
    add_subdirectory(tests/native)
endif()
