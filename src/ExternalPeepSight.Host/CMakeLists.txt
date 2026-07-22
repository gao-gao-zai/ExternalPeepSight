add_executable(ExternalPeepSight.Host WIN32
    main.cpp
)

target_compile_definitions(ExternalPeepSight.Host PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

if(MSVC)
    target_compile_options(ExternalPeepSight.Host PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
        /utf-8
    )

    if(EXTERNAL_PEEPSIGHT_ENABLE_ANALYZE)
        target_compile_options(ExternalPeepSight.Host PRIVATE
            /analyze
            /analyze:external-
        )
    endif()

endif()

target_link_libraries(ExternalPeepSight.Host PRIVATE
    d2d1
    d3d11
    dcomp
    dwrite
    dxgi
    user32
    windowscodecs
)
