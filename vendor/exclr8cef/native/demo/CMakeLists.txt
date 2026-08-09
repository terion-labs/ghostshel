# Demo app — Stage 2 visual proof: opens a URL in a CEF window.
# On macOS this builds a .app bundle plus the 5 required Helper.app
# subprocess bundles defined by CEF_HELPER_APP_SUFFIXES.

set(DEMO_TARGET exclr8cef_demo)
set(DEMO_HELPER_TARGET exclr8cef_demo_Helper)
set(DEMO_HELPER_OUTPUT_NAME "exclr8cef_demo Helper")

set(DEMO_SRCS main.c)
set(DEMO_HELPER_SRCS ../helper/main.c)

SET_CEF_TARGET_OUT_DIR()

# ---- Linux ---------------------------------------------------------------
if(OS_LINUX)
    add_executable(${DEMO_TARGET} ${DEMO_SRCS})
    SET_EXECUTABLE_TARGET_PROPERTIES(${DEMO_TARGET})
    add_dependencies(${DEMO_TARGET} libcef_dll_wrapper exclr8cef)
    target_link_libraries(${DEMO_TARGET}
        exclr8cef
        libcef_lib
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
    )
    set_target_properties(${DEMO_TARGET} PROPERTIES
        INSTALL_RPATH "$ORIGIN"
        BUILD_WITH_INSTALL_RPATH TRUE
        RUNTIME_OUTPUT_DIRECTORY ${CEF_TARGET_OUT_DIR}
    )
    COPY_FILES("${DEMO_TARGET}" "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR}" "${CEF_TARGET_OUT_DIR}")
    COPY_FILES("${DEMO_TARGET}" "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${CEF_TARGET_OUT_DIR}")
endif()

# ---- macOS ---------------------------------------------------------------
if(OS_MAC)
    set(DEMO_APP "${CEF_TARGET_OUT_DIR}/${DEMO_TARGET}.app")

    set(EXECUTABLE_NAME "${DEMO_TARGET}")
    set(PRODUCT_NAME "${DEMO_TARGET}")

    add_executable(${DEMO_TARGET} MACOSX_BUNDLE ${DEMO_SRCS})
    SET_EXECUTABLE_TARGET_PROPERTIES(${DEMO_TARGET})
    add_dependencies(${DEMO_TARGET} libcef_dll_wrapper exclr8cef)
    target_link_libraries(${DEMO_TARGET} exclr8cef ${CEF_STANDARD_LIBS})
    set_target_properties(${DEMO_TARGET} PROPERTIES
        MACOSX_BUNDLE_INFO_PLIST ${CMAKE_CURRENT_SOURCE_DIR}/Info.plist
        INSTALL_RPATH "@executable_path/../Frameworks"
        BUILD_WITH_INSTALL_RPATH TRUE
    )

    COPY_MAC_FRAMEWORK("${DEMO_TARGET}" "${CEF_BINARY_DIR}" "${DEMO_APP}")

    add_custom_command(TARGET ${DEMO_TARGET} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:exclr8cef>
            "${DEMO_APP}/Contents/Frameworks/$<TARGET_FILE_NAME:exclr8cef>"
        VERBATIM
    )

    foreach(_suffix_list ${CEF_HELPER_APP_SUFFIXES})
        string(REPLACE ":" ";" _suffix_list ${_suffix_list})
        list(GET _suffix_list 0 _name_suffix)
        list(GET _suffix_list 1 _target_suffix)
        list(GET _suffix_list 2 _plist_suffix)

        set(_helper_target "${DEMO_HELPER_TARGET}${_target_suffix}")
        set(_helper_output_name "${DEMO_HELPER_OUTPUT_NAME}${_name_suffix}")

        set(_helper_info_plist "${CMAKE_CURRENT_BINARY_DIR}/helper-Info${_target_suffix}.plist")
        file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../helper/helper-Info.plist" _plist_contents)
        string(REPLACE "\${EXECUTABLE_NAME}" "${_helper_output_name}" _plist_contents ${_plist_contents})
        string(REPLACE "\${PRODUCT_NAME}"    "${_helper_output_name}" _plist_contents ${_plist_contents})
        string(REPLACE "\${BUNDLE_ID_SUFFIX}" "${_plist_suffix}"      _plist_contents ${_plist_contents})
        string(REPLACE "\${VERSION_SHORT}"   "0.2.0"                  _plist_contents ${_plist_contents})
        file(WRITE ${_helper_info_plist} ${_plist_contents})

        add_executable(${_helper_target} MACOSX_BUNDLE ${DEMO_HELPER_SRCS})
        SET_EXECUTABLE_TARGET_PROPERTIES(${_helper_target})
        add_dependencies(${_helper_target} exclr8cef)
        target_link_libraries(${_helper_target} exclr8cef ${CEF_STANDARD_LIBS})
        set_target_properties(${_helper_target} PROPERTIES
            MACOSX_BUNDLE_INFO_PLIST ${_helper_info_plist}
            OUTPUT_NAME ${_helper_output_name}
            INSTALL_RPATH "@executable_path/../../.."
            BUILD_WITH_INSTALL_RPATH TRUE
        )

        add_dependencies(${DEMO_TARGET} ${_helper_target})

        add_custom_command(TARGET ${DEMO_TARGET} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_directory
                "${CEF_TARGET_OUT_DIR}/${_helper_output_name}.app"
                "${DEMO_APP}/Contents/Frameworks/${_helper_output_name}.app"
            VERBATIM
        )
    endforeach()
endif()

# ---- Windows -------------------------------------------------------------
if(OS_WINDOWS)
    add_executable(${DEMO_TARGET} ${DEMO_SRCS})
    SET_EXECUTABLE_TARGET_PROPERTIES(${DEMO_TARGET})
    add_dependencies(${DEMO_TARGET} libcef_dll_wrapper exclr8cef)
    target_link_libraries(${DEMO_TARGET}
        exclr8cef
        libcef_lib
        libcef_dll_wrapper
        ${CEF_STANDARD_LIBS}
    )
    COPY_FILES("${DEMO_TARGET}" "${CEF_BINARY_FILES}" "${CEF_BINARY_DIR}" "${CEF_TARGET_OUT_DIR}")
    COPY_FILES("${DEMO_TARGET}" "${CEF_RESOURCE_FILES}" "${CEF_RESOURCE_DIR}" "${CEF_TARGET_OUT_DIR}")
endif()
