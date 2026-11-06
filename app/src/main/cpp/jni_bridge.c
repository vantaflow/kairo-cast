/* Pont JNI provisoire (la glue serveur arrive a la tache 3). */
#include <jni.h>

JNIEXPORT jstring JNICALL
Java_com_vantaflow_kairo_cast_NativeReceiver_nativeVersion(JNIEnv *env, jobject thiz)
{
    (void) thiz;
    return (*env)->NewStringUTF(env, "uxplay-d4b7373");
}
