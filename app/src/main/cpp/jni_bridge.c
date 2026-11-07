/*
 * Pont JNI Kairo Cast. Les callbacks de raop arrivent sur ses propres threads
 * natifs : on attache chaque thread a la JVM a la premiere utilisation et on le
 * detache a sa sortie (destructeur de cle pthread). Un jbyteArray est conserve
 * par thread et agrandi au besoin.
 */
#include <android/log.h>
#include <jni.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#include "kairo_server.h"

#define TAG "KairoCast"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

static JavaVM *g_vm = NULL;
/* Une reference globale par emplacement, protegee par un verrou commun (court). */
static jobject g_listener[KAIRO_MAX_SLOTS];
static pthread_mutex_t g_listener_lock = PTHREAD_MUTEX_INITIALIZER;
/* Serialise start/stop d'un meme emplacement (jamais pris par un callback). */
static pthread_mutex_t g_api_lock[KAIRO_MAX_SLOTS] = {
    PTHREAD_MUTEX_INITIALIZER, PTHREAD_MUTEX_INITIALIZER,
    PTHREAD_MUTEX_INITIALIZER, PTHREAD_MUTEX_INITIALIZER
};
_Static_assert(KAIRO_MAX_SLOTS == 4, "g_api_lock a mettre a jour");
/* Methodes de l'interface ReceiverListener, resolues une fois dans JNI_OnLoad :
 * valables pour toute implementation, jamais reaffectees pendant qu'un serveur tourne. */
static jmethodID m_opened, m_closed, m_format, m_video, m_audio, m_volume, m_reset;

typedef struct {
    jbyteArray array;   /* reference globale */
    jsize cap;
    int attached;       /* attache par nous : a detacher */
} thread_ctx_t;

static pthread_key_t g_key;
static pthread_once_t g_key_once = PTHREAD_ONCE_INIT;

static void thread_exit(void *p)
{
    thread_ctx_t *ctx = (thread_ctx_t *) p;
    if (!ctx) return;
    JNIEnv *env = NULL;
    if ((*g_vm)->GetEnv(g_vm, (void **) &env, JNI_VERSION_1_6) == JNI_OK && env) {
        if (ctx->array) (*env)->DeleteGlobalRef(env, ctx->array);
    }
    if (ctx->attached) (*g_vm)->DetachCurrentThread(g_vm);
    free(ctx);
}

static void make_key(void) { pthread_key_create(&g_key, thread_exit); }

/* Renvoie l'env du thread courant (attache si besoin) et son contexte. */
static JNIEnv *get_env(thread_ctx_t **out_ctx)
{
    pthread_once(&g_key_once, make_key);
    thread_ctx_t *ctx = (thread_ctx_t *) pthread_getspecific(g_key);
    JNIEnv *env = NULL;
    int attached = 0;
    if ((*g_vm)->GetEnv(g_vm, (void **) &env, JNI_VERSION_1_6) != JNI_OK) {
        if ((*g_vm)->AttachCurrentThread(g_vm, &env, NULL) != JNI_OK) return NULL;
        attached = 1;
    }
    if (!ctx) {
        ctx = (thread_ctx_t *) calloc(1, sizeof(*ctx));
        if (!ctx) return env;
        ctx->attached = attached;
        pthread_setspecific(g_key, ctx);
    }
    *out_ctx = ctx;
    return env;
}

static void check_exception(JNIEnv *env)
{
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
    }
}

/* La reference reste valide pendant le callback : elle n'est liberee qu'apres
 * kairo_server_stop(slot), qui attend la fin des threads de l'emplacement. */
static jobject listener_ref(int slot)
{
    if (slot < 0 || slot >= KAIRO_MAX_SLOTS) return NULL;
    pthread_mutex_lock(&g_listener_lock);
    jobject l = g_listener[slot];
    pthread_mutex_unlock(&g_listener_lock);
    return l;
}

static void call_void(int slot, jmethodID m)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref(slot);
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m);
    check_exception(env);
}

static void call_data(int slot, jmethodID m, const unsigned char *data, int size, int64_t pts_us, int extra, int has_extra)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref(slot);
    if (!env || !l || !ctx || size <= 0) return;
    if (!ctx->array || ctx->cap < size) {
        jsize cap = ctx->cap ? ctx->cap : 64 * 1024;
        while (cap < size) cap *= 2;
        jbyteArray local = (*env)->NewByteArray(env, cap);
        if (!local) { check_exception(env); return; }
        jbyteArray global = (jbyteArray) (*env)->NewGlobalRef(env, local);
        (*env)->DeleteLocalRef(env, local);
        if (!global) return;
        if (ctx->array) (*env)->DeleteGlobalRef(env, ctx->array);
        ctx->array = global;
        ctx->cap = cap;
    }
    (*env)->SetByteArrayRegion(env, ctx->array, 0, size, (const jbyte *) data);
    if (has_extra) (*env)->CallVoidMethod(env, l, m, ctx->array, (jint) size, (jlong) pts_us, (jint) extra);
    else (*env)->CallVoidMethod(env, l, m, ctx->array, (jint) size, (jlong) pts_us);
    check_exception(env);
}

static void cb_opened(int slot) { call_void(slot, m_opened); }
static void cb_closed(int slot) { call_void(slot, m_closed); }
static void cb_reset(int slot) { call_void(slot, m_reset); }

static void cb_format(int slot, int w, int h, int cx, int cy, int cw, int ch)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref(slot);
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m_format, (jint) w, (jint) h,
                           (jint) cx, (jint) cy, (jint) cw, (jint) ch);
    check_exception(env);
}

static void cb_video(int slot, const unsigned char *d, int n, int64_t pts) { call_data(slot, m_video, d, n, pts, 0, 0); }
static void cb_audio(int slot, const unsigned char *d, int n, int64_t pts, int ct) { call_data(slot, m_audio, d, n, pts, ct, 1); }

static void cb_volume(int slot, float db)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref(slot);
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m_volume, (jfloat) db);
    check_exception(env);
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void) reserved;
    g_vm = vm;
    JNIEnv *env = NULL;
    if ((*vm)->GetEnv(vm, (void **) &env, JNI_VERSION_1_6) != JNI_OK || !env) return JNI_ERR;
    jclass cls = (*env)->FindClass(env, "com/vantaflow/kairo/cast/ReceiverListener");
    if (!cls) {
        (*env)->ExceptionClear(env);
        LOGE("interface ReceiverListener introuvable");
        return JNI_ERR;
    }
    m_opened = (*env)->GetMethodID(env, cls, "onConnectionOpened", "()V");
    m_closed = (*env)->GetMethodID(env, cls, "onConnectionClosed", "()V");
    m_format = (*env)->GetMethodID(env, cls, "onVideoFormat", "(IIIIII)V");
    m_video = (*env)->GetMethodID(env, cls, "onVideoData", "([BIJ)V");
    m_audio = (*env)->GetMethodID(env, cls, "onAudioData", "([BIJI)V");
    m_volume = (*env)->GetMethodID(env, cls, "onAudioVolume", "(F)V");
    m_reset = (*env)->GetMethodID(env, cls, "onVideoReset", "()V");
    (*env)->DeleteLocalRef(env, cls);
    if ((*env)->ExceptionCheck(env) || !m_opened || !m_closed || !m_format || !m_video || !m_audio
        || !m_volume || !m_reset) {
        (*env)->ExceptionClear(env);
        LOGE("methodes ReceiverListener introuvables");
        return JNI_ERR;
    }
    return JNI_VERSION_1_6;
}

JNIEXPORT jstring JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeVersion(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    return (*env)->NewStringUTF(env, "uxplay-d4b7373");
}

static int valid_slot(jint slot) { return slot >= 0 && slot < KAIRO_MAX_SLOTS; }

static void release_listener(JNIEnv *env, int slot)
{
    pthread_mutex_lock(&g_listener_lock);
    jobject old = g_listener[slot];
    g_listener[slot] = NULL;
    pthread_mutex_unlock(&g_listener_lock);
    if (old) (*env)->DeleteGlobalRef(env, old);
}

JNIEXPORT jboolean JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeStart(JNIEnv *env, jobject thiz, jint slot, jstring jname,
                                                         jstring jdevice, jstring jkey, jint width,
                                                         jint height, jint max_fps, jintArray jports,
                                                         jobject listener)
{
    (void) thiz;
    if (!valid_slot(slot) || !listener || !jports || (*env)->GetArrayLength(env, jports) != 5) {
        LOGE("nativeStart : arguments invalides (emplacement %d)", (int) slot);
        return JNI_FALSE;
    }
    jint p[5];
    (*env)->GetIntArrayRegion(env, jports, 0, 5, p);
    if ((*env)->ExceptionCheck(env)) { check_exception(env); return JNI_FALSE; }
    for (int i = 0; i < 5; i++) {
        if (p[i] <= 0 || p[i] > 65535) {
            LOGE("[%d] port invalide : %d", (int) slot, (int) p[i]);
            return JNI_FALSE;
        }
    }
    kairo_ports_t ports = {
        .tcp_http = (unsigned short) p[0], .tcp_mirror = (unsigned short) p[1],
        .udp_timing = (unsigned short) p[2], .udp_control = (unsigned short) p[3],
        .udp_data = (unsigned short) p[4],
    };

    pthread_mutex_lock(&g_api_lock[slot]);
    /* arret du serveur precedent de CET emplacement (threads termines) avant de
     * remplacer son ecouteur ; les autres emplacements ne sont pas touches */
    kairo_server_stop((int) slot);
    release_listener(env, (int) slot);
    jobject ref = (*env)->NewGlobalRef(env, listener);
    if (!ref) {
        check_exception(env);
        LOGE("[%d] NewGlobalRef a echoue", (int) slot);
        pthread_mutex_unlock(&g_api_lock[slot]);
        return JNI_FALSE;
    }
    pthread_mutex_lock(&g_listener_lock);
    g_listener[slot] = ref;
    pthread_mutex_unlock(&g_listener_lock);

    const char *name = jname ? (*env)->GetStringUTFChars(env, jname, NULL) : NULL;
    const char *device = jdevice ? (*env)->GetStringUTFChars(env, jdevice, NULL) : NULL;
    const char *key = jkey ? (*env)->GetStringUTFChars(env, jkey, NULL) : NULL;
    if (!name || !device || !key) check_exception(env);   /* OutOfMemoryError eventuelle */
    kairo_callbacks_t cb = {
        cb_opened, cb_closed, cb_format, cb_video, cb_audio, cb_volume, cb_reset
    };
    int rc = (name && device && key)
        ? kairo_server_start((int) slot, name, device, key, (int) width, (int) height, (int) max_fps, &ports, &cb)
        : -1;
    if (name) (*env)->ReleaseStringUTFChars(env, jname, name);
    if (device) (*env)->ReleaseStringUTFChars(env, jdevice, device);
    if (key) (*env)->ReleaseStringUTFChars(env, jkey, key);
    if (rc != 0) {
        LOGE("[%d] demarrage du serveur echoue (%d)", (int) slot, rc);
        release_listener(env, (int) slot);
    }
    pthread_mutex_unlock(&g_api_lock[slot]);
    return rc == 0 ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeStop(JNIEnv *env, jobject thiz, jint slot)
{
    (void) thiz;
    if (!valid_slot(slot)) return;
    pthread_mutex_lock(&g_api_lock[slot]);
    kairo_server_stop((int) slot);   /* ne rend la main qu'apres l'arret des threads de l'emplacement */
    release_listener(env, (int) slot);
    pthread_mutex_unlock(&g_api_lock[slot]);
}

JNIEXPORT void JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeSetAdmit(JNIEnv *env, jobject thiz, jint slot, jboolean allow)
{
    (void) env; (void) thiz;
    if (valid_slot(slot)) kairo_server_set_admit((int) slot, allow ? 1 : 0);
}

static jbyteArray txt_to_array(JNIEnv *env, int slot, int (*getter)(int, unsigned char *, int))
{
    unsigned char buf[1300];
    int n = valid_slot(slot) ? getter(slot, buf, sizeof(buf)) : 0;
    jbyteArray arr = (*env)->NewByteArray(env, n);
    if (arr && n > 0) (*env)->SetByteArrayRegion(env, arr, 0, n, (const jbyte *) buf);
    return arr;
}

JNIEXPORT jbyteArray JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeAirplayTxt(JNIEnv *env, jobject thiz, jint slot)
{
    (void) thiz;
    return txt_to_array(env, (int) slot, kairo_server_get_airplay_txt);
}

JNIEXPORT jbyteArray JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeRaopTxt(JNIEnv *env, jobject thiz, jint slot)
{
    (void) thiz;
    return txt_to_array(env, (int) slot, kairo_server_get_raop_txt);
}

JNIEXPORT jstring JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeRaopServiceName(JNIEnv *env, jobject thiz, jint slot)
{
    (void) thiz;
    char buf[12 + 1 + 256];
    buf[0] = 0;
    if (valid_slot(slot)) kairo_server_get_raop_service_name((int) slot, buf, sizeof(buf));
    return (*env)->NewStringUTF(env, buf);
}
