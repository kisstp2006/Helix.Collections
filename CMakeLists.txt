cmake_minimum_required(VERSION 3.28)
project(Helix.Collections LANGUAGES CXX)

if(NOT COMMAND helix_add_library)
    include("${CMAKE_CURRENT_SOURCE_DIR}/External/Helix.CMake/Helix.cmake")
endif()

helix_require(ScriptCore)

# C++: the layouts of the C# containers, readable as Helix.Core spans.
add_library(helix_collections INTERFACE)
add_library(Helix::Collections ALIAS helix_collections)
target_include_directories(helix_collections INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/Source")
target_link_libraries(helix_collections INTERFACE Helix::Core)

# C#: Helix.Collections.dll over HelixEngine.CoreModule (Helix.ScriptCore builds it).
find_program(HELIX_DOTNET NAMES dotnet HINTS "$ENV{HELIX_DOTNET_ROOT}" "$ENV{DOTNET_ROOT}" REQUIRED)
set(collections_artifacts "${CMAKE_BINARY_DIR}/HelixCollections")
set(collections_dir "${CMAKE_CURRENT_SOURCE_DIR}/Source/Helix.Collections")
set(collections_dll "${collections_artifacts}/bin/Helix.Collections/release/Helix.Collections.dll")
file(GLOB_RECURSE collections_sources CONFIGURE_DEPENDS "${collections_dir}/*.cs" "${collections_dir}/*.csproj")
list(FILTER collections_sources EXCLUDE REGEX "/(bin|obj)/")
add_custom_command(
    OUTPUT "${collections_dll}"
    COMMAND "${HELIX_DOTNET}" build "${collections_dir}/Helix.Collections.csproj" -c Release --artifacts-path "${collections_artifacts}" --nologo -v quiet
            "-p:HelixCoreModule=${HELIX_CORE_MODULE}"
    DEPENDS ${collections_sources} "${HELIX_CORE_MODULE}"
    COMMENT "Building Helix.Collections")
add_custom_target(helix_collections_assembly ALL DEPENDS "${collections_dll}")
add_dependencies(helix_collections_assembly helix_core_module)
set(HELIX_COLLECTIONS_ASSEMBLY "${collections_dll}" CACHE INTERNAL "")

# Tests: the containers under the .NET host, with the engine (its jobs and memory): the shared engine on the desktop,
# a web app in node on the Web.
if(HELIX_PLATFORM STREQUAL "windows" OR HELIX_PLATFORM STREQUAL "linux")
    helix_add_test_suite(helix_collections_tests EXCLUDE "/Managed/" DEPS Helix::EngineShared Helix::Collections)
elseif(HELIX_PLATFORM STREQUAL "web")
    helix_add_test_suite(helix_collections_tests DOTNET EXCLUDE "/Managed/" DEPS Helix::Engine Helix::Collections)
endif()
if(TARGET helix_collections_tests)
    set(tests_dir "${CMAKE_CURRENT_SOURCE_DIR}/Tests")
    set(tests_dll "${collections_artifacts}/bin/Helix.Collections.Tests/release/Helix.Collections.Tests.dll")
    file(GLOB_RECURSE test_sources CONFIGURE_DEPENDS "${tests_dir}/Managed/*.cs" "${tests_dir}/Managed/*.csproj")
    list(FILTER test_sources EXCLUDE REGEX "/(bin|obj)/")
    add_custom_command(
        OUTPUT "${tests_dll}"
        COMMAND "${HELIX_DOTNET}" build "${tests_dir}/Managed/Helix.Collections.Tests.csproj" -c Release --artifacts-path "${collections_artifacts}" --nologo -v quiet
                "-p:HelixCoreModule=${HELIX_CORE_MODULE}" "-p:HelixCollections=${collections_dll}" "-p:HelixScriptingRuntime=${HELIX_SCRIPTING_RUNTIME}"
        DEPENDS ${test_sources} "${collections_dll}"
        COMMENT "Building the C# tests of Helix.Collections")
    add_custom_target(helix_collections_test_assembly DEPENDS "${tests_dll}")
    add_dependencies(helix_collections_test_assembly helix_collections_assembly)
    add_dependencies(helix_collections_tests helix_collections_test_assembly)
    target_compile_definitions(helix_collections_tests PRIVATE "HX_TEST_RUNTIME_ASSEMBLY=\"${HELIX_SCRIPTING_RUNTIME}\"" "HX_TEST_CORE_MODULE=\"${HELIX_CORE_MODULE}\""
                                                               "HX_TEST_ASSEMBLY=\"${tests_dll}\"")
    # The web app's assemblies.
    set_property(TARGET helix_collections_tests APPEND PROPERTY HELIX_DOTNET_ASSEMBLIES "${HELIX_CORE_MODULE}" ${HELIX_CORE_MODULE_REFERENCES} "${collections_dll}" "${tests_dll}")
    if(TARGET helix_collections_tests_web)
        add_dependencies(helix_collections_tests_web helix_collections_test_assembly)
    endif()
    # Once more on the coreclr starter (as Android hosts the runtime).
    if(HELIX_PLATFORM STREQUAL "windows" OR HELIX_PLATFORM STREQUAL "linux")
        add_test(NAME helix_collections_tests_coreclr COMMAND helix_collections_tests)
        set_tests_properties(helix_collections_tests_coreclr PROPERTIES ENVIRONMENT "HELIX_DOTNET_RUNTIME=coreclr")
    endif()
endif()

# The public surface of Helix.Collections against its inventory (Tests/Api/Helix.Collections.csv), with Helix.ScriptCore's tool.
if(HELIX_BUILD_TESTS AND PROJECT_IS_TOP_LEVEL AND HELIX_API_INVENTORY)
    add_test(NAME helix_collections_api COMMAND "${HELIX_DOTNET}" "${HELIX_API_INVENTORY}" --assembly "${collections_dll}" --reference "${HELIX_CORE_MODULE}"
             --reference "${HELIX_SCRIPTING_RUNTIME}" --inventory "${CMAKE_CURRENT_SOURCE_DIR}/Tests/Api/Helix.Collections.csv")
    # The tool's target is in a dependency's folder, outside this build's default targets.
    add_dependencies(helix_collections_assembly helix_api_inventory)
endif()

if(TEST helix_collections_tests AND HELIX_ENABLE_ASAN AND HELIX_PLATFORM STREQUAL "linux")
    helix_dependency_dir(scripting_dir Scripting)
    set_tests_properties(helix_collections_tests PROPERTIES ENVIRONMENT "LSAN_OPTIONS=suppressions=${scripting_dir}/Tests/Support/LeakSuppressions.txt")
    # The run on the coreclr starter keeps its starter and takes the suppressions too.
    if(TEST helix_collections_tests_coreclr)
        set_tests_properties(helix_collections_tests_coreclr PROPERTIES ENVIRONMENT "HELIX_DOTNET_RUNTIME=coreclr;LSAN_OPTIONS=suppressions=${scripting_dir}/Tests/Support/LeakSuppressions.txt")
    endif()
endif()
