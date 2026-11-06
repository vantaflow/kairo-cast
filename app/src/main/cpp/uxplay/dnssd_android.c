/*
 * KAIRO: backend DNS-SD pour Android (remplace dns_sd/ et mdnsd/ d'UxPlay).
 * Aucune annonce reseau ici : on construit seulement les enregistrements TXT
 * (memes cles/valeurs que dns_sd/dns_sd.c d'UxPlay) pour que la couche Java
 * les publie via NsdManager. dnssd_register_* renvoie 0.
 */
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <assert.h>

#include "dnssd.h"
#include "dnssdint.h"
#include "utils.h"

typedef struct {
    unsigned char buf[1300];
    int len;
} txt_t;

typedef struct dnssd_private_s {
    txt_t raop;
    txt_t airplay;
} dnssd_private_t;

static void txt_set(txt_t *t, const char *key, const char *value)
{
    size_t kl = strlen(key), vl = strlen(value);
    size_t n = kl + 1 + vl;
    if (n > 255 || (size_t) t->len + 1 + n > sizeof(t->buf)) return;
    t->buf[t->len++] = (unsigned char) n;
    memcpy(t->buf + t->len, key, kl);
    t->len += (int) kl;
    t->buf[t->len++] = '=';
    memcpy(t->buf + t->len, value, vl);
    t->len += (int) vl;
}

void *dnssd_private_init(dnssd_t *dnssd_public, int *error)
{
    (void) dnssd_public;
    if (error) *error = DNSSD_ERROR_NOERROR;
    dnssd_private_t *p = (dnssd_private_t *) calloc(1, sizeof(dnssd_private_t));
    if (!p && error) *error = DNSSD_ERROR_OUTOFMEM;
    return p;
}

void dnssd_private_destroy(void *private)
{
    free(private);
}

int dnssd_register_raop(dnssd_t *pub, unsigned short port)
{
    char features[22] = {0};
    (void) port;
    assert(pub && pub->dnssd_private);
    dnssd_private_t *d = (dnssd_private_t *) pub->dnssd_private;
    txt_t *t = &d->raop;
    t->len = 0;
    snprintf(features, sizeof(features), "0x%X,0x%X", pub->features1, pub->features2);

    txt_set(t, "ch", RAOP_CH);
    txt_set(t, "cn", RAOP_CN);
    txt_set(t, "da", RAOP_DA);
    txt_set(t, "et", RAOP_ET);
    txt_set(t, "vv", RAOP_VV);
    txt_set(t, "ft", features);
    txt_set(t, "am", GLOBAL_MODEL);
    txt_set(t, "md", RAOP_MD);
    txt_set(t, "rhd", RAOP_RHD);
    switch (pub->pin_pw) {
    case 1:
        txt_set(t, "pw", "true");
        txt_set(t, "sf", "0x8c");
        break;
    case 2:
    case 3:
        txt_set(t, "pw", "true");
        txt_set(t, "sf", "0x84");
        break;
    default:
        txt_set(t, "pw", "false");
        txt_set(t, "sf", RAOP_SF);
        break;
    }
    txt_set(t, "sr", RAOP_SR);
    txt_set(t, "ss", RAOP_SS);
    txt_set(t, "sv", RAOP_SV);
    txt_set(t, "tp", RAOP_TP);
    txt_set(t, "txtvers", RAOP_TXTVERS);
    txt_set(t, "vs", RAOP_VS);
    txt_set(t, "vn", RAOP_VN);
    txt_set(t, "pk", pub->pk ? pub->pk : "");
    return 0;
}

int dnssd_register_airplay(dnssd_t *pub, unsigned short port)
{
    char device_id[3 * MAX_HWADDR_LEN];
    char features[22] = {0};
    (void) port;
    assert(pub && pub->dnssd_private);
    dnssd_private_t *d = (dnssd_private_t *) pub->dnssd_private;
    txt_t *t = &d->airplay;
    t->len = 0;
    snprintf(features, sizeof(features), "0x%X,0x%X", pub->features1, pub->features2);
    if (utils_hwaddr_airplay(device_id, sizeof(device_id), pub->hw_addr, pub->hw_addr_len) < 0) {
        return -1;
    }
    txt_set(t, "deviceid", device_id);
    txt_set(t, "features", features);
    txt_set(t, "pw", pub->pin_pw ? "true" : "false");
    txt_set(t, "flags", "0x4");
    txt_set(t, "model", GLOBAL_MODEL);
    txt_set(t, "pk", pub->pk ? pub->pk : "");
    txt_set(t, "pi", AIRPLAY_PI);
    txt_set(t, "srcvers", AIRPLAY_SRCVERS);
    txt_set(t, "vv", AIRPLAY_VV);
    return 0;
}

const char *dnssd_get_raop_txt(dnssd_t *pub, int *length)
{
    dnssd_private_t *d = (dnssd_private_t *) pub->dnssd_private;
    *length = d->raop.len;
    return (const char *) d->raop.buf;
}

const char *dnssd_get_airplay_txt(dnssd_t *pub, int *length)
{
    dnssd_private_t *d = (dnssd_private_t *) pub->dnssd_private;
    *length = d->airplay.len;
    return (const char *) d->airplay.buf;
}

void dnssd_unregister_raop(dnssd_t *pub) { (void) pub; }
void dnssd_unregister_airplay(dnssd_t *pub) { (void) pub; }

int dnssd_get_service_fd(dnssd_t *pub, int service) { (void) pub; (void) service; return -1; }
int dnssd_process_service(dnssd_t *pub, int service) { (void) pub; (void) service; return -1; }

void dnssd_error_text(int *dnssd_error, const char *appname)
{
    (void) appname;
    printf("dnssd (android) error code %d\n", *dnssd_error);
}
