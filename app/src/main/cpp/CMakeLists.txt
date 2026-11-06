cmake_minimum_required(VERSION 3.22.1)
project(kairocast C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

find_package(openssl REQUIRED CONFIG)

# ---- libplist 2.6.0 (statique) ----
file(GLOB PLIST_SRCS ${CMAKE_CURRENT_SOURCE_DIR}/libplist/src/*.c)
add_library(plist STATIC ${PLIST_SRCS}
    libplist/libcnary/cnary.c libplist/libcnary/node.c libplist/libcnary/node_list.c)
target_include_directories(plist
    PUBLIC libplist/include
    PRIVATE libplist libplist/src libplist/libcnary/include)
target_compile_definitions(plist PRIVATE HAVE_CONFIG_H)
target_compile_options(plist PRIVATE -w)

# ---- UxPlay lib/ ----
file(GLOB UXPLAY_SRCS ${CMAKE_CURRENT_SOURCE_DIR}/uxplay/*.c)
set(PLAYFAIR_SRCS
    uxplay/playfair/playfair.c uxplay/playfair/hand_garble.c
    uxplay/playfair/modified_md5.c uxplay/playfair/omg_hax.c uxplay/playfair/sap_hash.c)
set(LLHTTP_SRCS uxplay/llhttp/api.c uxplay/llhttp/http.c uxplay/llhttp/llhttp.c)

add_library(kairocast SHARED jni_bridge.c kairo_server.c ${UXPLAY_SRCS} ${PLAYFAIR_SRCS} ${LLHTTP_SRCS})
target_include_directories(kairocast PRIVATE uxplay uxplay/playfair uxplay/llhttp)
target_compile_definitions(kairocast PRIVATE
    NOHOLD PLIST_210 PLIST_230 STANDALONE TARGET_POSIX _LINUX _REENTRANT
    _FILE_OFFSET_BITS=64 OPENSSL_API_COMPAT=0x10101000L)
target_compile_options(kairocast PRIVATE -O2 -fvisibility=hidden -Wno-unused-parameter)
target_link_libraries(kairocast PRIVATE plist openssl::crypto log)
target_link_options(kairocast PRIVATE -Wl,-z,max-page-size=16384)
