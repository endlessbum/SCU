cmake_minimum_required(VERSION 3.20)
project(SCU_Install LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED APP_SEMVER)
    set(APP_SEMVER "0.0.0")
endif()

add_executable(SCU_Install src/main.cpp)

target_compile_definitions(SCU_Install PRIVATE APP_SEMVER="${APP_SEMVER}")

# Windows subsystem: GUI (no console window)
if(WIN32)
    set_target_properties(SCU_Install PROPERTIES
        WIN32_EXECUTABLE TRUE
    )
    target_link_options(SCU_Install PRIVATE /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup)
endif()

# MSVC flags
if(MSVC)
    target_compile_options(SCU_Install PRIVATE /W4 /WX-)
endif()
