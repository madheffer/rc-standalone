# bc7enc_native - the fast BC7 block encoder behind ResourceBuilder.BuildTexture.
# Vendored bc7enc.c (Richard Geldreich, MIT / public domain) plus bc7enc_image.c,
# a whole-image C-ABI wrapper. P/Invoked from src/Source2.Compiler/Texture/Bc7Native.cs.
#
# Building it is OPTIONAL. Without the library Bc7Native.Available is false and
# BuildTexture falls back to the managed BCnEncoder.Net path, which is correct
# but roughly two orders of magnitude slower.
#
#     cmake -S . -B build
#     cmake --build build --config Release
#
# The result lands in bin/ and the csproj copies it next to the built assembly.
# On Windows this needs a C compiler: install the Visual Studio Build Tools with
# the "Desktop development with C++" workload. On Linux/macOS any cc will do.

cmake_minimum_required(VERSION 3.15)
project(bc7enc_native C)

set(CMAKE_C_STANDARD 11)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

add_library(bc7enc_native SHARED bc7enc.c bc7enc_image.c)

# Only the two BC7ENC_API functions in bc7enc_image.c are exported.
set_target_properties(bc7enc_native PROPERTIES
    C_VISIBILITY_PRESET hidden
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin")

if(MSVC)
    target_compile_options(bc7enc_native PRIVATE /O2)
else()
    target_compile_options(bc7enc_native PRIVATE -O3 -fPIC)
    target_link_libraries(bc7enc_native PRIVATE m)
endif()

# Multi-config generators (Visual Studio) append the config name to the output
# dir; force every config to land in bin/ so the csproj path is stable.
foreach(cfg ${CMAKE_CONFIGURATION_TYPES})
    string(TOUPPER ${cfg} cfg_u)
    set_target_properties(bc7enc_native PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${cfg_u} "${CMAKE_CURRENT_SOURCE_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY_${cfg_u} "${CMAKE_CURRENT_SOURCE_DIR}/bin")
endforeach()
