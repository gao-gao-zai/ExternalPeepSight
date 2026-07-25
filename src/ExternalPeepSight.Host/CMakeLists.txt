add_executable(ExternalPeepSight.Host WIN32
    current_user_security.cpp
    current_user_security.h
    diagnostics.cpp
    diagnostics.h
    ExternalPeepSight.Host.rc
    host_snapshot.cpp
    host_snapshot.h
    host_threads.cpp
    host_threads.h
    input_system.cpp
    input_system.h
    ipc_endpoint.cpp
    ipc_endpoint.h
    ipc_protocol.cpp
    ipc_protocol.h
    main.cpp
    monitor_descriptor.cpp
    monitor_descriptor.h
    named_pipe_server.cpp
    named_pipe_server.h
    process_lifecycle.cpp
    process_lifecycle.h
    prototype_app.cpp
    prototype_app.h
    prototype_contracts.cpp
    prototype_contracts.h
    render_configuration.cpp
    render_configuration.h
    render_state.cpp
    render_state.h
    render_recovery.cpp
    render_recovery.h
    script_runtime.cpp
    script_runtime.h
    script_coordinator.cpp
    script_coordinator.h
    script_command_configuration.cpp
    script_command_configuration.h
    single_instance.cpp
    single_instance.h
    tray_icon.cpp
    tray_icon.h
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
    advapi32
    bcrypt
    d2d1
    d3d11
    dcomp
    dwrite
    dwmapi
    dxgi
    ole32
    psapi
    shell32
    user32
    windowsapp
    windowscodecs
    wtsapi32
    xmllite
    ExternalPeepSight.Lua
)
