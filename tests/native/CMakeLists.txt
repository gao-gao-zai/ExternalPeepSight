include(FetchContent)

set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_Declare(
    googletest
    GIT_REPOSITORY https://github.com/google/googletest.git
    GIT_TAG 52eb8108c5bdec04579160ae17225d66034bd723
)
FetchContent_MakeAvailable(googletest)

add_executable(ExternalPeepSight.Native.Tests
    smoke_test.cpp
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
)

set_tests_properties(ExternalPeepSight.Host.Starts PROPERTIES
    TIMEOUT 5
)
