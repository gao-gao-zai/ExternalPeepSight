add_executable(ExternalPeepSight.Host WIN32
    main.cpp
    prototype_app.cpp
    prototype_app.h
    prototype_contracts.cpp
    prototype_contracts.h
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
    ole32
    psapi
    shell32
    user32
    windowscodecs
)
