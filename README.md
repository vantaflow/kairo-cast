# Kairo Cast

Récepteur de recopie d'écran pour Android TV, compagnon de Kairo. Il permet d'afficher l'écran d'un **Mac** (AirPlay) ou d'un **PC Windows** (application Kairo Cast pour Windows), image jusqu'en 4K et son, sur une box ou un téléviseur Android TV.

## Utilisation

1. Installer l'application sur la box et l'ouvrir une fois : elle démarre ensuite toute seule avec la box.
2. Sur le Mac : **Centre de contrôle › Recopie de l'écran › Kairo** (ou le nom choisi dans les réglages).
3. La recopie s'affiche en plein écran sur la TV. Pendant la recopie, la touche **OK** de la télécommande ouvre un petit menu.

### Depuis un PC Windows

1. Télécharger **[KairoCast.exe](https://github.com/vantaflow/kairo-cast/releases/latest/download/KairoCast.exe)** (Windows 10 ou 11, 64 bits, rien à installer).
2. L'ouvrir : les box Kairo Cast du réseau s'affichent (ou saisir l'adresse IP de la box).
3. Choisir l'écran, puis **Recopier**. Le son du PC suit ; **Arrêter** rend la main.

L'encodage vidéo utilise la carte graphique (NVIDIA, Intel ou AMD), sinon l'encodeur de Windows. Le PC et la box doivent être sur le même réseau ; le pare-feu Windows peut demander d'autoriser Kairo Cast au premier lancement (réseau privé).

### Réglages

- **Nom du récepteur** : nom visible sur le Mac.
- **Netteté** : 1080p, 1440p (par défaut) ou 4K. Plus c'est net, plus la box et le réseau sont sollicités.
- **Affichage** : *Étirer* (par défaut, remplit l'écran), *Remplir* (rogne les bords) ou *Ajuster* (bandes noires, proportions conservées).
- **Son** : activé ou non.
- **Démarrage avec la box** : lance le récepteur au démarrage.
- **Afficher par-dessus les autres applis** : autorisation nécessaire pour que la recopie s'ouvre au-dessus d'une autre application.

## Installation

Avec `adb` (la box doit accepter le débogage réseau) :

```
adb connect 192.168.0.62:5555
adb install -r app/build/outputs/apk/release/app-release.apk
adb shell appops set com.vantaflow.kairo.cast SYSTEM_ALERT_WINDOW allow
adb shell pm grant com.vantaflow.kairo.cast android.permission.POST_NOTIFICATIONS
```

Ou en une commande (compile, installe, autorise, lance) :

```
scripts/deploy.sh 192.168.0.62
```

La version release est signée avec la clé de debug, ou avec `keystore.properties` (non versionné : `storeFile`, `storePassword`, `keyAlias`, `keyPassword`) s'il existe.

## Compilation

JDK 17, SDK Android 34, NDK 27.2.12479018 :

```
./gradlew :app:testDebugUnitTest :app:assembleRelease
```

Application Windows (.NET 8, dossier `windows/`) :

```
cd windows
dotnet test KairoCast.Core.Tests
./scripts/fetch-ffmpeg.ps1
dotnet publish KairoCast/KairoCast.csproj -c Release -o out
```

### Protocole Kairo Link

Le PC envoie à la box (port TCP 7200, annoncé en mDNS `_kairocast._tcp`) un flux H.264 et du son PCM 16 bits ; description complète dans `docs/superpowers/specs/2026-11-09-emetteur-windows-design.md`.

## Licence et crédits

GPL-3.0.

- [UxPlay](https://github.com/FDH2/UxPlay) (GPL-3.0) : serveur AirPlay sur lequel repose le récepteur.
- [libplist](https://github.com/libimobiledevice/libplist) (LGPL-2.1).
- [FFmpeg](https://ffmpeg.org) (LGPL-2.1, version LGPL de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)) : capture et encodage dans l'application Windows, exécuté comme programme séparé ; licence jointe à la release (`FFMPEG-LICENSE.txt`).
- [NAudio](https://github.com/naudio/NAudio) (MIT) : capture du son dans l'application Windows.
- OpenSSL 1.1.1 (licence OpenSSL/SSLeay), fourni par le paquet prefab `com.android.ndk.thirdparty:openssl` et lié dynamiquement.

GPL-3.0 et OpenSSL 1.1.1 : la licence OpenSSL/SSLeay n'est pas considérée comme compatible avec la GPL, sauf à bénéficier d'une exception pour les bibliothèques du système. Ici, OpenSSL est une bibliothèque externe fournie sous forme de paquet et liée dynamiquement, pas une bibliothèque du système Android ; cette situation est signalée telle quelle, sans exception de licence ajoutée.
