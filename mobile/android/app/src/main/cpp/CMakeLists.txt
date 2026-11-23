cmake_minimum_required(VERSION 3.22.1)
project(osmium_mobile_engine LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Paths relative to Osmium/mobile/android/app/src/main/cpp
get_filename_component(OSMIUM_MOBILE_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../.." ABSOLUTE)
get_filename_component(OSMIUM_REPO_ROOT "${OSMIUM_MOBILE_ROOT}/.." ABSOLUTE)

set(OSMIUM_PROTOCOL_INCLUDE "${OSMIUM_REPO_ROOT}/native/osmium-engine/include")
set(OSMIUM_OPUS_DIR "${OSMIUM_REPO_ROOT}/native/deps/opus")
set(OSMIUM_MBEDTLS_DIR "${OSMIUM_MOBILE_ROOT}/third_party/mbedtls")

# Build libopus as static library directly from repo dependency
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_CMAKE_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
add_subdirectory("${OSMIUM_OPUS_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/opus" EXCLUDE_FROM_ALL)

# Build mbedTLS (mbedcrypto) as static library
set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
set(USE_SHARED_MBEDTLS_LIBRARY OFF CACHE BOOL "" FORCE)
set(USE_STATIC_MBEDTLS_LIBRARY ON CACHE BOOL "" FORCE)
set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)
add_subdirectory("${OSMIUM_MBEDTLS_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/mbedtls" EXCLUDE_FROM_ALL)

add_library(osmium_mobile_engine SHARED
    MobileCrypto.cpp
    MobileTransport.cpp
    VideoReassembler.cpp
    AndroidVideoDecoder.cpp
    AndroidAudioEngine.cpp
    OsmiumMobileApi.cpp
)

target_include_directories(osmium_mobile_engine PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${OSMIUM_PROTOCOL_INCLUDE}"
    "${OSMIUM_OPUS_DIR}/include"
    "${OSMIUM_MBEDTLS_DIR}/include"
)

target_compile_definitions(osmium_mobile_engine PRIVATE
    OSMIUM_MOBILE_ANDROID=1
)

target_link_libraries(osmium_mobile_engine PRIVATE
    opus
    mbedcrypto
    mediandk
    aaudio
    android
    log
)
