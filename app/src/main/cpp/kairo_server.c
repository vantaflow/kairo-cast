/*
 * Glue serveur Kairo Cast : reprend la partie serveur de uxplay.cpp
 * (start_dnssd, start_raop_server, callbacks) sans GStreamer.
 * Les donnees video/audio sont transmises telles quelles a la couche JNI.
 */
#include <android/log.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "kairo_server.h"
#include "raop.h"
#include "dnssd.h"
#include "logger.h"
#include "utils.h"
#include "global.h"

#define TAG "KairoCast"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;   /* start/stop/TXT */
static raop_t *g_raop = NULL;
static dnssd_t *g_dnssd = NULL;
static kairo_callbacks_t g_cb;
static char g_service_name[12 + 1 + 256];
static volatile double g_volume = 0.0;
static pthread_mutex_t g_conn_lock = PTHREAD_MUTEX_INITIALIZER;
static int g_open_connections = 0;

/* ---- journalisation UxPlay -> logcat ---- */
static void log_callback(void *cls, int level, const char *msg)
{
    int prio;
    switch (level) {
    case LOGGER_EMERG: case LOGGER_ALERT: case LOGGER_CRIT: case LOGGER_ERR:
        prio = ANDROID_LOG_ERROR; break;
    case LOGGER_WARNING: prio = ANDROID_LOG_WARN; break;
    case LOGGER_NOTICE: case LOGGER_INFO: prio = ANDROID_LOG_INFO; break;
    default: prio = ANDROID_LOG_DEBUG; break;
    }
    __android_log_print(prio, TAG, "%s", msg);
}

/* ---- callbacks raop ---- */
static void cb_conn_init(void *cls)
{
    pthread_mutex_lock(&g_conn_lock);
    int first = (g_open_connections++ == 0);
    pthread_mutex_unlock(&g_conn_lock);
    if (first && g_cb.on_connection_opened) g_cb.on_connection_opened();
}

static void cb_conn_destroy(void *cls)
{
    pthread_mutex_lock(&g_conn_lock);
    int last = (g_open_connections > 0 && --g_open_connections == 0);
    pthread_mutex_unlock(&g_conn_lock);
    if (last && g_cb.on_connection_closed) g_cb.on_connection_closed();
}

static void cb_conn_feedback(void *cls) { (void) cls; }

static void cb_conn_reset(void *cls, int reason)
{
    if (reason == 1) LOGE("connexion perdue avec le client (reseau ?)");
    else if (reason == 2) LOGE("source HLS non prise en charge");
}

static void cb_audio_process(void *cls, raop_ntp_t *ntp, audio_decode_struct *data)
{
    if (!g_cb.on_audio_data || !data->data || data->data_len <= 0) return;
    uint64_t t = data->ntp_time_local ? data->ntp_time_local : get_local_time();
    g_cb.on_audio_data(data->data, data->data_len, (int64_t) (t / 1000));
}

static void cb_video_process(void *cls, raop_ntp_t *ntp, video_decode_struct *data)
{
    if (!g_cb.on_video_data || !data->data || data->data_len <= 0) return;
    uint64_t t = data->ntp_time_local ? data->ntp_time_local : get_local_time();
    g_cb.on_video_data(data->data, data->data_len, (int64_t) (t / 1000));
}

static void cb_nop(void *cls) { (void) cls; }

static void cb_video_reset(void *cls, reset_type_t type)
{
    if (type == RESET_TYPE_ON_VIDEO_PLAY) return;
    if (g_cb.on_video_reset) g_cb.on_video_reset();
}

static int cb_video_set_codec_and_size(void *cls, video_codec_t codec, unsigned short width_source,
                                       unsigned short height_source, unsigned short x_offset,
                                       unsigned short y_offset, unsigned short width,
                                       unsigned short height)
{
    if (codec == VIDEO_CODEC_H265) {
        LOGE("H.265 refuse (non pris en charge)");
        return -1;
    }
    if (g_cb.on_video_format) g_cb.on_video_format((int) width_source, (int) height_source);
    return 0;
}

static double cb_audio_set_client_volume(void *cls) { return g_volume; }

static void cb_audio_set_volume(void *cls, float volume)
{
    g_volume = (double) volume;
    if (g_cb.on_audio_volume) g_cb.on_audio_volume(volume);
}

static void cb_report_client_request(void *cls, char *deviceid, char *model, char *name, bool *admit)
{
    LOGI("demande de connexion de %s (%s), deviceID = %s", name ? name : "?", model ? model : "?",
         deviceid ? deviceid : "?");
    *admit = true;
}

static const char *cb_passwd(void *cls, int *len)
{
    *len = 0;   /* pas de mot de passe */
    return NULL;
}

/* ---- demarrage / arret ---- */
static int parse_hw_addr(const char *str, char hw[MAX_HWADDR_LEN])
{
    /* "aa:bb:cc:dd:ee:ff" */
    if (!str || strlen(str) != 3 * MAX_HWADDR_LEN - 1) return -1;
    for (int i = 0; i < MAX_HWADDR_LEN; i++) {
        const char *p = str + 3 * i;
        char *end;
        if (i < MAX_HWADDR_LEN - 1 && p[2] != ':') return -1;
        char tmp[3] = { p[0], p[1], 0 };
        long v = strtol(tmp, &end, 16);
        if (*end != 0) return -1;
        hw[i] = (char) v;
    }
    return 0;
}

static void stop_locked(void)
{
    if (g_raop) {
        raop_destroy(g_raop);
        g_raop = NULL;
    }
    if (g_dnssd) {
        dnssd_unregister_raop(g_dnssd);
        dnssd_unregister_airplay(g_dnssd);
        dnssd_destroy(g_dnssd);
        g_dnssd = NULL;
    }
    g_service_name[0] = 0;
    pthread_mutex_lock(&g_conn_lock);
    g_open_connections = 0;
    pthread_mutex_unlock(&g_conn_lock);
}

int kairo_server_start(const char *name, const char *device_id, const char *key_file,
                       const kairo_callbacks_t *callbacks)
{
    char hw_addr[MAX_HWADDR_LEN];
    int rc = -1;
    int err = 0;

    if (!name || !device_id || !key_file || !callbacks) return -1;
    if (parse_hw_addr(device_id, hw_addr) != 0) {
        LOGE("deviceId invalide : %s", device_id);
        return -2;
    }

    pthread_mutex_lock(&g_lock);
    if (g_raop || g_dnssd) stop_locked();
    g_cb = *callbacks;
    g_volume = 0.0;

    /* dnssd : pin_pw = 0 (aucun controle d'acces) */
    g_dnssd = dnssd_init(name, (int) strlen(name), hw_addr, MAX_HWADDR_LEN, 0, &err);
    if (!g_dnssd || err) {
        LOGE("dnssd_init a echoue : %d", err);
        g_dnssd = NULL;
        rc = -3;
        goto fail;
    }

    /* Fonctionnalites : celles d'UxPlay (defauts de dnssdint.h) sauf ceux-ci.
     * bit 0 = video AirPlay (lecture de fichier/URL) et bit 4 = HLS : coupes, on ne
     * fait que la recopie d'ecran (mirroring, bit 7).
     * bit 42 = "Screen Multi Codec" (autorise H.265) : coupe, H.264 seulement.
     * bit 27 = appairage AirPlay 2 : 0, comme UxPlay par defaut (legacy pairing). */
    dnssd_set_airplay_features(g_dnssd, 0, 0);
    dnssd_set_airplay_features(g_dnssd, 4, 0);
    dnssd_set_airplay_features(g_dnssd, 42, 0);
    dnssd_set_airplay_features(g_dnssd, 27, 0);

    raop_callbacks_t cbs;
    memset(&cbs, 0, sizeof(cbs));
    cbs.conn_init = cb_conn_init;
    cbs.conn_destroy = cb_conn_destroy;
    cbs.conn_reset = cb_conn_reset;
    cbs.conn_feedback = cb_conn_feedback;
    cbs.audio_process = cb_audio_process;
    cbs.video_process = cb_video_process;
    cbs.audio_flush = cb_nop;
    cbs.video_flush = cb_nop;
    cbs.video_pause = cb_nop;
    cbs.video_resume = cb_nop;
    cbs.audio_set_client_volume = cb_audio_set_client_volume;
    cbs.audio_set_volume = cb_audio_set_volume;
    cbs.report_client_request = cb_report_client_request;
    cbs.passwd = cb_passwd;
    cbs.video_reset = cb_video_reset;
    cbs.video_set_codec_and_size = cb_video_set_codec_and_size;

    g_raop = raop_init(&cbs);
    if (!g_raop) {
        LOGE("raop_init a echoue");
        rc = -4;
        goto fail;
    }
    raop_set_log_callback(g_raop, log_callback, NULL);
    raop_set_log_level(g_raop, LOGGER_INFO);
    if (raop_init2(g_raop, 0, device_id, key_file)) {
        LOGE("raop_init2 a echoue");
        free(g_raop);   /* comme uxplay.cpp : raop_init2 n'a rien alloue a liberer */
        g_raop = NULL;
        rc = -5;
        goto fail;
    }

    /* affichage annonce au client : defauts d'UxPlay (1920x1080, 60 Hz, 30 fps) */
    /* ports fixes (option -p d'UxPlay) : UDP 7011 6001 6000, TCP 7100 7000 7001 */
    unsigned short tcp[3] = { 7100, KAIRO_AIRPLAY_PORT, 7001 };
    unsigned short udp[3] = { 7011, 6001, 6000 };
    raop_set_tcp_ports(g_raop, tcp);
    raop_set_udp_ports(g_raop, udp);

    unsigned short port = raop_get_port(g_raop);
    if (raop_start_httpd(g_raop, &port) < 0) {
        LOGE("raop_start_httpd a echoue (port %u)", port);
        rc = -6;
        goto fail_raop;
    }
    raop_set_port(g_raop, port);

    /* raop_set_dnssd appelle dnssd_set_pk : doit preceder l'enregistrement des TXT */
    raop_set_dnssd(g_raop, g_dnssd);
    if (dnssd_register_raop(g_dnssd, port) || dnssd_register_airplay(g_dnssd, port)) {
        LOGE("dnssd_register_* a echoue");
        rc = -7;
        goto fail_raop;
    }

    char hex[2 * MAX_HWADDR_LEN + 1];
    utils_hwaddr_raop(hex, sizeof(hex), hw_addr, MAX_HWADDR_LEN);
    snprintf(g_service_name, sizeof(g_service_name), "%s@%s", hex, name);

    LOGI("serveur AirPlay demarre sur le port %u (%s)", port, g_service_name);
    pthread_mutex_unlock(&g_lock);
    return 0;

fail_raop:
fail:
    stop_locked();
    pthread_mutex_unlock(&g_lock);
    return rc;
}

void kairo_server_stop(void)
{
    pthread_mutex_lock(&g_lock);
    stop_locked();
    pthread_mutex_unlock(&g_lock);
}

static int copy_txt(const char *(*getter)(dnssd_t *, int *), unsigned char *buf, int cap)
{
    int n = 0;
    pthread_mutex_lock(&g_lock);
    if (g_dnssd) {
        const char *p = getter(g_dnssd, &n);
        if (n < 0 || n > cap) n = 0;
        else if (n > 0) memcpy(buf, p, (size_t) n);
    }
    pthread_mutex_unlock(&g_lock);
    return n;
}

int kairo_server_get_airplay_txt(unsigned char *buf, int cap)
{
    return copy_txt(dnssd_get_airplay_txt, buf, cap);
}

int kairo_server_get_raop_txt(unsigned char *buf, int cap)
{
    return copy_txt(dnssd_get_raop_txt, buf, cap);
}

void kairo_server_get_raop_service_name(char *buf, int cap)
{
    pthread_mutex_lock(&g_lock);
    snprintf(buf, (size_t) cap, "%s", g_service_name);
    pthread_mutex_unlock(&g_lock);
}
