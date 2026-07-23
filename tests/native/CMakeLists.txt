include(FetchContent)

set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG 52eb8108c5bdec04579160ae17225d66034bd723
)
FetchContent_MakeAvailable(googletest)

add_executable(ExternalPeepSight.Native.Tests
    host_infrastructure_tests.cpp
    input_system_tests.cpp
    ipc_protocol_tests.cpp
    named_pipe_server_tests.cpp
    prototype_contracts_tests.cpp
    render_configuration_tests.cpp
    render_state_tests.cpp
    smoke_test.cpp
    ../../src/ExternalPeepSight.Host/current_user_security.cpp
    ../../src/ExternalPeepSight.Host/diagnostics.cpp
    ../../src/ExternalPeepSight.Host/host_snapshot.cpp
    ../../src/ExternalPeepSight.Host/host_threads.cpp
    ../../src/ExternalPeepSight.Host/input_system.cpp
    ../../src/ExternalPeepSight.Host/ipc_endpoint.cpp
    ../../src/ExternalPeepSight.Host/ipc_protocol.cpp
    ../../src/ExternalPeepSight.Host/monitor_descriptor.cpp
    ../../src/ExternalPeepSight.Host/named_pipe_server.cpp
    ../../src/ExternalPeepSight.Host/prototype_contracts.cpp
    ../../src/ExternalPeepSight.Host/render_configuration.cpp
    ../../src/ExternalPeepSight.Host/render_state.cpp
    ../../src/ExternalPeepSight.Host/render_recovery.cpp
    ../../src/ExternalPeepSight.Host/single_instance.cpp
)

target_include_directories(ExternalPeepSight.Native.Tests PRIVATE
    ../../src/ExternalPeepSight.Host
)

target_compile_definitions(ExternalPeepSight.Native.Tests PRIVATE
    EXTERNAL_PEEPSIGHT_RENDER_GEOMETRY_FIXTURE="${CMAKE_SOURCE_DIR}/contracts/fixtures/render-geometry-v1.json"
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
    advapi32
    bcrypt
    GTest::gtest_main
    shell32
    user32
    windowsapp
    wtsapi32
    xmllite
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
        --instance-id=smoke-focus
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
        --instance-id=smoke-all
        --metrics-output=${CMAKE_CURRENT_BINARY_DIR}/host-all-monitors-metrics.csv
)

set_tests_properties(ExternalPeepSight.Host.AllMonitors PROPERTIES
    TIMEOUT 10
)

add_test(
    NAME ExternalPeepSight.Host.StaticPresentation
    COMMAND ${CMAKE_COMMAND}
        -DMETRICS_FILE=${CMAKE_CURRENT_BINARY_DIR}/host-smoke-metrics.csv
        -P ${CMAKE_CURRENT_SOURCE_DIR}/verify_host_metrics.cmake
)

set_tests_properties(ExternalPeepSight.Host.StaticPresentation PROPERTIES
    DEPENDS ExternalPeepSight.Host.Starts
)

add_test(
    NAME ExternalPeepSight.Host.AllMonitorsStaticPresentation
    COMMAND ${CMAKE_COMMAND}
        -DMETRICS_FILE=${CMAKE_CURRENT_BINARY_DIR}/host-all-monitors-metrics.csv
        -P ${CMAKE_CURRENT_SOURCE_DIR}/verify_host_metrics.cmake
)

set_tests_properties(ExternalPeepSight.Host.AllMonitorsStaticPresentation PROPERTIES
    DEPENDS ExternalPeepSight.Host.AllMonitors
)
