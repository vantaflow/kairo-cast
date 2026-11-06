/* Serveur AirPlay (UxPlay lib/) sans GStreamer : glue pour Kairo Cast. */
#ifndef KAIRO_SERVER_H
#define KAIRO_SERVER_H

#include <stdint.h>
#include <stddef.h>

#define KAIRO_AIRPLAY_PORT 7000

/* Appelees depuis les threads natifs de raop (pas de thread dedie). */
typedef struct kairo_callbacks_s {
    void (*on_connection_opened)(void);
    void (*on_connection_closed)(void);
    void (*on_video_format)(int width, int height);
    void (*on_video_data)(const unsigned char *data, int size, int64_t pts_us);
    void (*on_audio_data)(const unsigned char *data, int size, int64_t pts_us);
    void (*on_audio_volume)(float volume_db);
    void (*on_video_reset)(void);
} kairo_callbacks_t;

/* 0 si OK, valeur negative sinon. device_id = "aa:bb:cc:dd:ee:ff". */
int kairo_server_start(const char *name, const char *device_id, const char *key_file,
                       const kairo_callbacks_t *callbacks);
void kairo_server_stop(void);

/* Copie les octets TXT (format DNS) dans buf ; renvoie la longueur (0 si arrete). */
int kairo_server_get_airplay_txt(unsigned char *buf, int cap);
int kairo_server_get_raop_txt(unsigned char *buf, int cap);
/* "<HEX12>@<nom>" ; chaine vide si arrete. */
void kairo_server_get_raop_service_name(char *buf, int cap);

#endif
