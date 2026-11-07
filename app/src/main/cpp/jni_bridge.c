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
static jobject g_listener = NULL;            /* reference globale */
static pthread_mutex_t g_listener_lock = PTHREAD_MUTEX_INITIALIZER;
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

static jobject listener_ref(void)
{
    pthread_mutex_lock(&g_listener_lock);
    jobject l = g_listener;
    pthread_mutex_unlock(&g_listener_lock);
    return l;
}

static void call_void(jmethodID m)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref();
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m);
    check_exception(env);
}

static void call_data(jmethodID m, const unsigned char *data, int size, int64_t pts_us)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref();
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
    (*env)->CallVoidMethod(env, l, m, ctx->array, (jint) size, (jlong) pts_us);
    check_exception(env);
}

static void cb_opened(void) { call_void(m_opened); }
static void cb_closed(void) { call_void(m_closed); }
static void cb_reset(void) { call_void(m_reset); }

static void cb_format(int w, int h, int cx, int cy, int cw, int ch)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref();
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m_format, (jint) w, (jint) h,
                           (jint) cx, (jint) cy, (jint) cw, (jint) ch);
    check_exception(env);
}

static void cb_video(const unsigned char *d, int n, int64_t pts) { call_data(m_video, d, n, pts); }
static void cb_audio(const unsigned char *d, int n, int64_t pts) { call_data(m_audio, d, n, pts); }

static void cb_volume(float db)
{
    thread_ctx_t *ctx = NULL;
    JNIEnv *env = get_env(&ctx);
    jobject l = listener_ref();
    if (!env || !l) return;
    (*env)->CallVoidMethod(env, l, m_volume, (jfloat) db);
    check_exception(env);
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved)
{
    (void) reserved;
    g_vm = vm;
    return JNI_VERSION_1_6;
}

JNIEXPORT jstring JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeVersion(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    return (*env)->NewStringUTF(env, "uxplay-d4b7373");
}

static void release_listener(JNIEnv *env)
{
    pthread_mutex_lock(&g_listener_lock);
    jobject old = g_listener;
    g_listener = NULL;
    pthread_mutex_unlock(&g_listener_lock);
    if (old) (*env)->DeleteGlobalRef(env, old);
}

JNIEXPORT jboolean JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeStart(JNIEnv *env, jobject thiz, jstring jname,
                                                         jstring jdevice, jstring jkey, jobject listener)
{
    (void) thiz;
    jclass cls = (*env)->GetObjectClass(env, listener);
    m_opened = (*env)->GetMethodID(env, cls, "onConnectionOpened", "()V");
    m_closed = (*env)->GetMethodID(env, cls, "onConnectionClosed", "()V");
    m_format = (*env)->GetMethodID(env, cls, "onVideoFormat", "(IIIIII)V");
    m_video = (*env)->GetMethodID(env, cls, "onVideoData", "([BIJ)V");
    m_audio = (*env)->GetMethodID(env, cls, "onAudioData", "([BIJ)V");
    m_volume = (*env)->GetMethodID(env, cls, "onAudioVolume", "(F)V");
    m_reset = (*env)->GetMethodID(env, cls, "onVideoReset", "()V");
    (*env)->DeleteLocalRef(env, cls);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        LOGE("methodes ReceiverListener introuvables");
        return JNI_FALSE;
    }

    /* arret d'un serveur precedent avant de remplacer l'ecouteur */
    kairo_server_stop();
    release_listener(env);
    jobject ref = (*env)->NewGlobalRef(env, listener);
    pthread_mutex_lock(&g_listener_lock);
    g_listener = ref;
    pthread_mutex_unlock(&g_listener_lock);

    const char *name = (*env)->GetStringUTFChars(env, jname, NULL);
    const char *device = (*env)->GetStringUTFChars(env, jdevice, NULL);
    const char *key = (*env)->GetStringUTFChars(env, jkey, NULL);
    kairo_callbacks_t cb = {
        cb_opened, cb_closed, cb_format, cb_video, cb_audio, cb_volume, cb_reset
    };
    int rc = (name && device && key) ? kairo_server_start(name, device, key, &cb) : -1;
    if (name) (*env)->ReleaseStringUTFChars(env, jname, name);
    if (device) (*env)->ReleaseStringUTFChars(env, jdevice, device);
    if (key) (*env)->ReleaseStringUTFChars(env, jkey, key);
    if (rc != 0) {
        release_listener(env);
        return JNI_FALSE;
    }
    return JNI_TRUE;
}

JNIEXPORT void JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeStop(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    kairo_server_stop();   /* ne rend la main qu'apres l'arret des threads raop */
    release_listener(env);
}

static jbyteArray txt_to_array(JNIEnv *env, int (*getter)(unsigned char *, int))
{
    unsigned char buf[1300];
    int n = getter(buf, sizeof(buf));
    jbyteArray arr = (*env)->NewByteArray(env, n);
    if (arr && n > 0) (*env)->SetByteArrayRegion(env, arr, 0, n, (const jbyte *) buf);
    return arr;
}

JNIEXPORT jbyteArray JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeAirplayTxt(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    return txt_to_array(env, kairo_server_get_airplay_txt);
}

JNIEXPORT jbyteArray JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeRaopTxt(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    return txt_to_array(env, kairo_server_get_raop_txt);
}

JNIEXPORT jstring JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeRaopServiceName(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    char buf[12 + 1 + 256];
    kairo_server_get_raop_service_name(buf, sizeof(buf));
    return (*env)->NewStringUTF(env, buf);
}
