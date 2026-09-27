cmake_minimum_required(VERSION 3.24)
project(SecurityScanner LANGUAGES C CXX)

# ScannerCore.exe — offline on-demand сканер для SCU. Только Win32 API и CNG,
# без внешних зависимостей: бинарник статически линкует CRT и не тянет runtime.

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

# Build-time генерация component_allowlist.h: SHA-256 собственных скриптов SCU.
# Хеши фиксируются на этапе сборки (см. документ п. 19/29), чтобы сканер
# не детектировал собственные скрипты при Full Scan.
set(SCU_ASSETS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../SCU.App/Assets")
set(GENERATED_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
# DEPENDS на сами ассеты: без этого изменение Utilities.bat/SCU.ps1 не
# перегенерирует allowlist, и сканер начинает детектить собственные скрипты.
file(GLOB SCU_ASSET_FILES LIST_DIRECTORIES false
    "${SCU_ASSETS_DIR}/*.ps1"
    "${SCU_ASSETS_DIR}/*.bat")
add_custom_command(
    OUTPUT "${GENERATED_DIR}/component_allowlist.h"
    COMMAND ${CMAKE_COMMAND}
        -DASSETS_DIR=${SCU_ASSETS_DIR}
        -DOUT_FILE=${GENERATED_DIR}/component_allowlist.h
        -P ${CMAKE_CURRENT_SOURCE_DIR}/cmake/component_allowlist.cmake
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/cmake/component_allowlist.cmake ${SCU_ASSET_FILES}
    COMMENT "Generating component_allowlist.h (SHA-256 of SCU scripts)"
)
add_custom_target(allowlist_header DEPENDS "${GENERATED_DIR}/component_allowlist.h")

# Пиннинг сертификата подписи (free self-signed path): отпечаток из
# signing/thumbprint.txt вшивается в ScannerCore; отсутствие файла = dev-режим.
set(SCU_SIGNING_DIR "${CMAKE_CURRENT_SOURCE_DIR}/signing")
add_custom_command(
    OUTPUT "${GENERATED_DIR}/signing_pin.h"
    COMMAND ${CMAKE_COMMAND}
        -DPIN_FILE=${SCU_SIGNING_DIR}/thumbprint.txt
        -DOUT_FILE=${GENERATED_DIR}/signing_pin.h
        -P ${CMAKE_CURRENT_SOURCE_DIR}/cmake/signing_pin.cmake
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/cmake/signing_pin.cmake
            ${SCU_SIGNING_DIR}/thumbprint.txt
    COMMENT "Generating signing_pin.h (certificate thumbprint pin)"
)
add_custom_target(signing_pin_header DEPENDS "${GENERATED_DIR}/signing_pin.h")

# miniz (public domain, third_party/miniz) — только чтение ZIP/inflate.
# Отдельная статическая библиотека с отключёнными предупреждениями.
add_library(miniz STATIC
    third_party/miniz/miniz.c
    third_party/miniz/miniz_tinfl.c
    third_party/miniz/miniz_tdef.c
    third_party/miniz/miniz_zip.c
)
target_include_directories(miniz PUBLIC third_party/miniz)
if(MSVC)
    target_compile_options(miniz PRIVATE /W0)
endif()

add_executable(ScannerCore
    src/main.cpp
    src/core/types.cpp
    src/core/scanner.cpp
    src/core/allowlist.cpp
    src/core/database.cpp
    src/core/database_package.cpp
    src/core/file_type.cpp
    src/archive/archive_scanner.cpp
    src/filesystem/enumerator.cpp
    src/hash/sha256.cpp
    src/pe/pe_analyzer.cpp
    src/pe/signature.cpp
    src/script/script_scanner.cpp
    src/system/persistence_scanner.cpp
    src/system/process_scanner.cpp
    src/ipc/event_writer.cpp
)

target_link_libraries(ScannerCore PRIVATE miniz)

add_dependencies(ScannerCore allowlist_header signing_pin_header)
target_include_directories(ScannerCore PRIVATE src ${GENERATED_DIR})

target_compile_definitions(ScannerCore PRIVATE
    UNICODE _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    _CRT_SECURE_NO_WARNINGS
)

# /utf-8: исходники в UTF-8, широкие литералы с кириллицей/тире должны
# компилироваться без перекодировки в системную кодовую страницу.
target_compile_options(ScannerCore PRIVATE /W4 /permissive- /EHsc /utf-8)

# Hardening (документ п. 22/23): DEP, ASLR, CFG; зависимости — только из
# System32 (LOAD_LIBRARY_SEARCH_SYSTEM32), чтобы DLL hijacking через scan path
# или рабочую директорию был невозможен.
target_link_options(ScannerCore PRIVATE
    /DYNAMICBASE
    /NXCOMPAT
    /GUARD:CF
    /DEPENDENTLOADFLAG:0x800
    /SUBSYSTEM:CONSOLE
)

target_link_libraries(ScannerCore PRIVATE bcrypt wintrust crypt32)

# Готовый exe кладём в SecurityScanner/out — SCU.App.csproj копирует его
# в свой вывод при сборке (Target CopyScannerCore).
set_target_properties(ScannerCore PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/out"
)
