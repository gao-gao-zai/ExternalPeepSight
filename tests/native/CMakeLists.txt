include(FetchContent)

set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG 52eb8108c5bdec04579160ae17225d66034bd723
)
FetchContent_MakeAvailable(googletest)

add_executable(ExternalPeepSight.Native.Tests
    prototype_contracts_tests.cpp
    smoke_test.cpp
    ../../src/ExternalPeepSight.Host/prototype_contracts.cpp
)

target_include_directories(ExternalPeepSight.Native.Tests PRIVATE
    ../../src/ExternalPeepSight.Host
)

if(MSVC)
    target_compile_options(ExternalPeepSight.Native.Tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
        /utf-8
    )

    if(EXTERNAL_PEEPSIGHT_ENABLE_ANALYZE)
        target_compile_options(ExternalPeepSight.Native.Tests PRIVATE
            /analyze
            /analyze:external-
            /wd6326
        )
    endif()

endif()

target_link_libraries(ExternalPeepSight.Native.Tests PRIVATE
    GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(
    ExternalPeepSight.Native.Tests
    DISCOVERY_MODE PRE_TEST
)

add_test(
    NAME ExternalPeepSight.Host.Starts
    COMMAND $<TARGET_FILE:ExternalPeepSight.Host>
        --focus-monitor
        --smoke-test
        --metrics-output=${CMAKE_CURRENT_BINARY_DIR}/host-smoke-metrics.csv
)

set_tests_properties(ExternalPeepSight.Host.Starts PROPERTIES
    TIMEOUT 10
)

add_test(
    NAME ExternalPeepSight.Host.AllMonitors
    COMMAND $<TARGET_FILE:ExternalPeepSight.Host>
        --all-monitors
        --smoke-test
        --metrics-output=${CMAKE_CURRENT_BINARY_DIR}/host-all-monitors-metrics.csv
)

set_tests_properties(ExternalPeepSight.Host.AllMonitors PROPERTIES
    TIMEOUT 10
)
