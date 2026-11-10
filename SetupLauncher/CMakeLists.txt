cmake_minimum_required(VERSION 3.20)
project(SCU_Install LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED APP_SEMVER)
    set(APP_SEMVER "0.0.0")
endif()

# Проверка подписи payload переиспользует источник движка (pe/signature.cpp):
# self-pin + проверка дайджеста без доверенного корня (OBS-08 / F-07,
# аудит 2026-11-04). Файл самодостаточен (только wincrypt/wintrust/bcrypt,
# библиотеки линкуются через #pragma comment).
add_executable(SCU_Install
    src/main.cpp
    ../SecurityScanner/src/pe/signature.cpp)

target_include_directories(SCU_Install PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../SecurityScanner/src)

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
