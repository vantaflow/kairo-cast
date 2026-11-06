# Provenance

- Source : UxPlay (https://github.com/FDH2/UxPlay), commit d4b7373, dossier `lib/` copié tel quel,
  sauf `dns_sd/`, `mdnsd/` et les `CMakeLists.txt` (remplacés par `app/src/main/cpp/CMakeLists.txt`).
- Licence : voir `playfair/LICENSE.md` (GPL v3) et `llhttp/LICENSE-MIT`; le reste est LGPL 2.1+ / GPL (voir en-têtes).

## Fichiers UxPlay modifiés

Aucun. `airplay_video.c` (HLS) compile sans modification ; OpenSSL 1.1.1 suffit (aucune API OSSL_*/EVP_MAC).

## Fichiers ajoutés (non UxPlay)

- `dnssd_android.c` : backend DNS-SD sans réseau. `dnssd.c` d'UxPlay ne construit PAS les TXT
  (c'est `dns_sd/dns_sd.c` qui le fait via l'API Bonjour) ; ce fichier reprend donc la construction des
  TXT `_raop._tcp` et `_airplay._tcp` (mêmes clés/valeurs que `dns_sd/dns_sd.c`) avec un encodeur TXT
  minimal. `dnssd_register_*` renvoie 0, `dnssd_get_*_txt` expose les octets TXT, la publication
  réseau sera faite côté Java (NsdManager). `dnssd_get_service_fd` / `dnssd_process_service` renvoient -1.
  Prérequis : appeler `dnssd_set_pk` avant `dnssd_register_*`.

## Compilation

- OpenSSL : prefab Google `com.android.ndk.thirdparty:openssl:1.1.1q-beta-1` (`openssl::crypto`, lié dynamiquement,
  impose `ANDROID_STL=c++_shared` ; `libcrypto.so` et `libc++_shared.so` sont donc embarqués dans l'APK).
- libplist 2.6.0 : voir `../libplist/` (statique ; `config.h` écrit à la main).
- Définitions : `NOHOLD PLIST_210 PLIST_230 OPENSSL_API_COMPAT=0x10101000L`.
