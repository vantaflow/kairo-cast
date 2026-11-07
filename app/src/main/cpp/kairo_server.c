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

/* Un emplacement = une instance UxPlay complete. Chaque emplacement a ses propres
 * verrous : arreter A (raop_destroy attend les threads de A) ne bloque pas B. */
typedef struct kairo_slot_s {
    int index;
    pthread_mutex_t lock;            /* start/stop/TXT de cet emplacement */
    raop_t *raop;
    dnssd_t *dnssd;
    kairo_callbacks_t cb;
    char service_name[12 + 1 + 256];
    volatile double volume;
    pthread_mutex_t conn_lock;
    int open_connections;
    int admit;                       /* acces atomique (__atomic_*) */
} kairo_slot_t;

#define SLOT_INIT(i) { .index = (i), .lock = PTHREAD_MUTEX_INITIALIZER, \
                       .conn_lock = PTHREAD_MUTEX_INITIALIZER, .admit = 1 }
static kairo_slot_t g_slots[KAIRO_MAX_SLOTS] = { SLOT_INIT(0), SLOT_INIT(1), SLOT_INIT(2), SLOT_INIT(3) };
_Static_assert(KAIRO_MAX_SLOTS == 4, "SLOT_INIT a mettre a jour");

static kairo_slot_t *slot_at(int slot)
{
    return (slot >= 0 && slot < KAIRO_MAX_SLOTS) ? &g_slots[slot] : NULL;
}

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

/* ---- callbacks raop (cls = kairo_slot_t de l'emplacement) ---- */
static void cb_conn_init(void *cls)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    pthread_mutex_lock(&s->conn_lock);
    int first = (s->open_connections++ == 0);
    pthread_mutex_unlock(&s->conn_lock);
    if (first && s->cb.on_connection_opened) s->cb.on_connection_opened(s->index);
}

static void cb_conn_destroy(void *cls)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    pthread_mutex_lock(&s->conn_lock);
    int last = (s->open_connections > 0 && --s->open_connections == 0);
    pthread_mutex_unlock(&s->conn_lock);
    if (last && s->cb.on_connection_closed) s->cb.on_connection_closed(s->index);
}

static void cb_conn_feedback(void *cls) { (void) cls; }

static void cb_conn_reset(void *cls, int reason)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    if (reason == 1) LOGE("[%d] connexion perdue avec le client (reseau ?)", s->index);
    else if (reason == 2) LOGE("[%d] source HLS non prise en charge", s->index);
}

static void cb_audio_process(void *cls, raop_ntp_t *ntp, audio_decode_struct *data)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    if (!s->cb.on_audio_data || !data->data || data->data_len <= 0) return;
    uint64_t t = data->ntp_time_local ? data->ntp_time_local : get_local_time();
    s->cb.on_audio_data(s->index, data->data, data->data_len, (int64_t) (t / 1000), (int) data->ct);
}

static void cb_video_process(void *cls, raop_ntp_t *ntp, video_decode_struct *data)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    if (!s->cb.on_video_data || !data->data || data->data_len <= 0) return;
    uint64_t t = data->ntp_time_local ? data->ntp_time_local : get_local_time();
    s->cb.on_video_data(s->index, data->data, data->data_len, (int64_t) (t / 1000));
}

static void cb_nop(void *cls) { (void) cls; }

static void cb_video_reset(void *cls, reset_type_t type)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    if (type == RESET_TYPE_ON_VIDEO_PLAY) return;
    if (s->cb.on_video_reset) s->cb.on_video_reset(s->index);
}

static int cb_video_set_codec_and_size(void *cls, video_codec_t codec, unsigned short width_source,
                                       unsigned short height_source, unsigned short x_offset,
                                       unsigned short y_offset, unsigned short width,
                                       unsigned short height)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    if (codec == VIDEO_CODEC_H265) {
        LOGE("[%d] H.265 refuse (non pris en charge)", s->index);
        return -1;
    }
    if (s->cb.on_video_format) s->cb.on_video_format(s->index, (int) width_source, (int) height_source,
                                                     (int) x_offset, (int) y_offset, (int) width, (int) height);
    return 0;
}

static double cb_audio_set_client_volume(void *cls) { return ((kairo_slot_t *) cls)->volume; }

static void cb_audio_set_volume(void *cls, float volume)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    s->volume = (double) volume;
    if (s->cb.on_audio_volume) s->cb.on_audio_volume(s->index, volume);
}

static void cb_report_client_request(void *cls, char *deviceid, char *model, char *name, bool *admit)
{
    kairo_slot_t *s = (kairo_slot_t *) cls;
    int allow = __atomic_load_n(&s->admit, __ATOMIC_ACQUIRE);
    LOGI("[%d] demande de connexion de %s (%s), deviceID = %s : %s", s->index, name ? name : "?",
         model ? model : "?", deviceid ? deviceid : "?", allow ? "admise" : "refusee");
    *admit = allow != 0;
}

static const char *cb_passwd(void *cls, int *len)
{
    *len = 0;   /* pas de mot de passe */
    return NULL;
}

/* HLS (lecture de video/URL) : coupe dans les features, mais http_handlers.h appelle ces
 * callbacks sans test de NULL ; des bouchons evitent un plantage sur une requete egaree. */
static void cb_hls_play(void *cls, const char *location, const float start_position)
{
    LOGE("[%d] requete HLS ignoree (lecture d'URL non prise en charge)", ((kairo_slot_t *) cls)->index);
}
static void cb_hls_scrub(void *cls, const float position) { (void) cls; }
static void cb_hls_rate(void *cls, const float rate) { (void) cls; }
static void cb_hls_stop(void *cls) { (void) cls; }
static void cb_hls_playback_info(void *cls, playback_info_t *info)
{
    /* position = -1 : « information indisponible », sans reinitialisation */
    info->duration = 0.0;
    info->position = -1.0;
    info->rate = 0.0f;
    info->num_loaded_time_ranges = 0;
    info->num_seekable_time_ranges = 0;
    info->loadedTimeRanges = NULL;
    info->seekableTimeRanges = NULL;
}
static float cb_hls_playlist_remove(void *cls) { (void) cls; return 0.0f; }

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

static void stop_locked(kairo_slot_t *s)
{
    if (s->raop) {
        raop_destroy(s->raop);   /* attend la fin des threads de cet emplacement seulement */
        s->raop = NULL;
    }
    if (s->dnssd) {
        dnssd_unregister_raop(s->dnssd);
        dnssd_unregister_airplay(s->dnssd);
        dnssd_destroy(s->dnssd);
        s->dnssd = NULL;
    }
    s->service_name[0] = 0;
    pthread_mutex_lock(&s->conn_lock);
    s->open_connections = 0;
    pthread_mutex_unlock(&s->conn_lock);
}

int kairo_server_start(int slot, const char *name, const char *device_id, const char *key_file,
                       int width, int height, int max_fps, const kairo_ports_t *ports,
                       const kairo_callbacks_t *cb)
{
    kairo_slot_t *s = slot_at(slot);
    char hw_addr[MAX_HWADDR_LEN];
    int rc = -1;
    int err = 0;

    if (!s) {
        LOGE("emplacement invalide : %d", slot);
        return -1;
    }
    if (!name || !device_id || !key_file || !cb || !ports) return -1;
    if (parse_hw_addr(device_id, hw_addr) != 0) {
        LOGE("[%d] deviceId invalide : %s", slot, device_id);
        return -2;
    }

    pthread_mutex_lock(&s->lock);
    if (s->raop || s->dnssd) stop_locked(s);
    s->cb = *cb;
    s->volume = 0.0;

    /* dnssd : pin_pw = 0 (aucun controle d'acces) */
    s->dnssd = dnssd_init(name, (int) strlen(name), hw_addr, MAX_HWADDR_LEN, 0, &err);
    if (!s->dnssd || err) {
        LOGE("[%d] dnssd_init a echoue : %d", slot, err);
        s->dnssd = NULL;
        rc = -3;
        goto fail;
    }

    /* Fonctionnalites : celles d'UxPlay (defauts de dnssdint.h) sauf ceux-ci.
     * bit 0 = video AirPlay (lecture de fichier/URL) et bit 4 = HLS : coupes, on ne
     * fait que la recopie d'ecran (mirroring, bit 7).
     * bit 42 = "Screen Multi Codec" (autorise H.265) : coupe, H.264 seulement.
     * bit 27 = appairage AirPlay 2 : 0, comme UxPlay par defaut (legacy pairing). */
    dnssd_set_airplay_features(s->dnssd, 0, 0);
    dnssd_set_airplay_features(s->dnssd, 4, 0);
    dnssd_set_airplay_features(s->dnssd, 42, 0);
    dnssd_set_airplay_features(s->dnssd, 27, 0);

    raop_callbacks_t cbs;
    memset(&cbs, 0, sizeof(cbs));
    cbs.cls = s;
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
    cbs.on_video_play = cb_hls_play;
    cbs.on_video_scrub = cb_hls_scrub;
    cbs.on_video_rate = cb_hls_rate;
    cbs.on_video_stop = cb_hls_stop;
    cbs.on_video_acquire_playback_info = cb_hls_playback_info;
    cbs.on_video_playlist_remove = cb_hls_playlist_remove;

    s->raop = raop_init(&cbs);
    if (!s->raop) {
        LOGE("[%d] raop_init a echoue", slot);
        rc = -4;
        goto fail;
    }
    raop_set_log_callback(s->raop, log_callback, NULL);
    raop_set_log_level(s->raop, LOGGER_INFO);
    if (raop_init2(s->raop, 0, device_id, key_file)) {
        LOGE("[%d] raop_init2 a echoue", slot);
        free(s->raop);   /* comme uxplay.cpp : raop_init2 n'a rien alloue a liberer */
        s->raop = NULL;
        rc = -5;
        goto fail;
    }

    /* affichage annonce au client : taille choisie (nettete), refreshRate = defaut d'UxPlay */
    if (width > 0 && height > 0) {
        raop_set_plist(s->raop, "width", width);
        raop_set_plist(s->raop, "height", height);
    }
    if (max_fps > 0) raop_set_plist(s->raop, "maxFPS", max_fps);
    LOGI("[%d] affichage annonce : %dx%d, maxFPS %d", slot, width, height, max_fps);

    /* ports de l'emplacement (option -p d'UxPlay) : tcp[0] = miroir, tcp[1] = HTTP/RTSP */
    unsigned short tcp[3] = { ports->tcp_mirror, ports->tcp_http, 0 };
    unsigned short udp[3] = { ports->udp_timing, ports->udp_control, ports->udp_data };
    raop_set_tcp_ports(s->raop, tcp);
    raop_set_udp_ports(s->raop, udp);

    unsigned short port = raop_get_port(s->raop);
    if (raop_start_httpd(s->raop, &port) < 0) {
        LOGE("[%d] raop_start_httpd a echoue : port TCP %u indisponible", slot, ports->tcp_http);
        rc = -6;
        goto fail;
    }
    if (port != ports->tcp_http) {
        LOGE("[%d] port HTTP %u attribue au lieu de %u", slot, port, ports->tcp_http);
        rc = -6;
        goto fail;
    }
    raop_set_port(s->raop, port);

    /* raop_set_dnssd appelle dnssd_set_pk : doit preceder l'enregistrement des TXT */
    raop_set_dnssd(s->raop, s->dnssd);
    if (dnssd_register_raop(s->dnssd, port) || dnssd_register_airplay(s->dnssd, port)) {
        LOGE("[%d] dnssd_register_* a echoue", slot);
        rc = -7;
        goto fail;
    }

    char hex[2 * MAX_HWADDR_LEN + 1];
    utils_hwaddr_raop(hex, sizeof(hex), hw_addr, MAX_HWADDR_LEN);
    snprintf(s->service_name, sizeof(s->service_name), "%s@%s", hex, name);

    LOGI("[%d] serveur AirPlay demarre : TCP %u/%u, UDP %u/%u/%u (%s)", slot, port, ports->tcp_mirror,
         ports->udp_timing, ports->udp_control, ports->udp_data, s->service_name);
    pthread_mutex_unlock(&s->lock);
    return 0;

fail:
    stop_locked(s);
    pthread_mutex_unlock(&s->lock);
    return rc;
}

void kairo_server_stop(int slot)
{
    kairo_slot_t *s = slot_at(slot);
    if (!s) return;
    pthread_mutex_lock(&s->lock);
    int was_running = s->raop != NULL;
    stop_locked(s);
    pthread_mutex_unlock(&s->lock);
    if (was_running) LOGI("[%d] serveur AirPlay arrete", slot);
}

void kairo_server_set_admit(int slot, int allow)
{
    kairo_slot_t *s = slot_at(slot);
    if (s) __atomic_store_n(&s->admit, allow ? 1 : 0, __ATOMIC_RELEASE);
}

static int copy_txt(int slot, const char *(*getter)(dnssd_t *, int *), unsigned char *buf, int cap)
{
    kairo_slot_t *s = slot_at(slot);
    int n = 0;
    if (!s) return 0;
    pthread_mutex_lock(&s->lock);
    if (s->dnssd) {
        const char *p = getter(s->dnssd, &n);
        if (n < 0 || n > cap) n = 0;
        else if (n > 0) memcpy(buf, p, (size_t) n);
    }
    pthread_mutex_unlock(&s->lock);
    return n;
}

int kairo_server_get_airplay_txt(int slot, unsigned char *buf, int cap)
{
    return copy_txt(slot, dnssd_get_airplay_txt, buf, cap);
}

int kairo_server_get_raop_txt(int slot, unsigned char *buf, int cap)
{
    return copy_txt(slot, dnssd_get_raop_txt, buf, cap);
}

void kairo_server_get_raop_service_name(int slot, char *buf, int cap)
{
    kairo_slot_t *s = slot_at(slot);
    if (cap <= 0) return;
    buf[0] = 0;
    if (!s) return;
    pthread_mutex_lock(&s->lock);
    snprintf(buf, (size_t) cap, "%s", s->service_name);
    pthread_mutex_unlock(&s->lock);
}
