/* Serveur AirPlay (UxPlay lib/) sans GStreamer : glue pour Kairo Cast.
 * Plusieurs emplacements (« slots ») independants dans le meme processus :
 * une instance raop_t + dnssd_t par emplacement, chacune sur ses propres ports. */
#ifndef KAIRO_SERVER_H
#define KAIRO_SERVER_H

#include <stdint.h>
#include <stddef.h>

#define KAIRO_MAX_SLOTS 4

/* Ports d'un emplacement (cf. SlotPorts.kt) : les tables doivent etre disjointes
 * entre emplacements (SO_REUSEADDR : un UDP en double volerait les paquets). */
typedef struct {
    unsigned short tcp_http, tcp_mirror, udp_timing, udp_control, udp_data;
} kairo_ports_t;

/* Appelees depuis les threads natifs de raop de l'emplacement `slot`.
 * Interdit depuis un callback : kairo_server_stop / start / get_* (verrou de
 * l'emplacement tenu pendant raop_destroy, qui attend ces memes threads). */
typedef struct kairo_callbacks_s {
    void (*on_connection_opened)(int slot);
    void (*on_connection_closed)(int slot);
    void (*on_video_format)(int slot, int width, int height, int cx, int cy, int cw, int ch);
    void (*on_video_data)(int slot, const unsigned char *data, int size, int64_t pts_us);
    void (*on_audio_data)(int slot, const unsigned char *data, int size, int64_t pts_us, int codec_type);
    void (*on_audio_volume)(int slot, float volume_db);
    void (*on_video_reset)(int slot);
} kairo_callbacks_t;

/* 0 si OK, valeur negative sinon. device_id = "aa:bb:cc:dd:ee:ff".
 * N'arrete que l'emplacement `slot` s'il tournait deja. max_fps <= 0 : defaut UxPlay. */
int kairo_server_start(int slot, const char *name, const char *device_id, const char *key_file,
                       int width, int height, int max_fps, const kairo_ports_t *ports,
                       const kairo_callbacks_t *cb);
void kairo_server_stop(int slot);

/* Copie les octets TXT (format DNS) dans buf ; renvoie la longueur (0 si arrete). */
int kairo_server_get_airplay_txt(int slot, unsigned char *buf, int cap);
int kairo_server_get_raop_txt(int slot, unsigned char *buf, int cap);
/* "<HEX12>@<nom>" ; chaine vide si arrete. */
void kairo_server_get_raop_service_name(int slot, char *buf, int cap);

/* Admission des nouveaux clients (1 par defaut) ; sans verrou, appelable a tout moment. */
void kairo_server_set_admit(int slot, int allow);

#endif
