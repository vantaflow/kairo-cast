# Kairo Cast

[![Dernière version](https://img.shields.io/github/v/release/vantaflow/kairo-cast?label=version&color=2563eb)](https://github.com/vantaflow/kairo-cast/releases/latest) ![Android TV 11+](https://img.shields.io/badge/Android%20TV-11%2B-3ddc84) ![Code Downloader 2534592](https://img.shields.io/badge/Downloader-2534592-f97316)

Récepteur AirPlay de recopie d'écran pour Android TV, compagnon de Kairo. Il permet d'afficher l'écran d'un Mac (image jusqu'en 4K et son) sur une box ou un téléviseur Android TV.

## Utilisation

1. Installer l'application sur la box et l'ouvrir une fois : elle démarre ensuite toute seule avec la box.
2. Sur le Mac : **Centre de contrôle › Recopie de l'écran › Kairo** (ou le nom choisi dans les réglages).
3. La recopie s'affiche en plein écran sur la TV. Pendant la recopie, la touche **OK** de la télécommande ouvre un petit menu.

**Kairo Link (Windows)** : un seul émetteur à la fois, le dernier arrivé l'emporte. Si une session Windows prend la main pendant que le Mac est connecté, puis se termine, l'image du Mac ne revient pas toute seule (AirPlay ne permet pas de redemander une image) : l'accueil affiche « Mac en attente : relancez la recopie depuis le Mac ».

Kairo Link, comme AirPlay sans code, accepte n'importe quel appareil du réseau local.

### Réglages

- **Nom du récepteur** : nom visible sur le Mac.
- **Netteté** : 1080p, 1440p (par défaut) ou 4K. Plus c'est net, plus la box et le réseau sont sollicités.
- **Affichage** : *Étirer* (par défaut, remplit l'écran), *Remplir* (rogne les bords) ou *Ajuster* (bandes noires, proportions conservées).
- **Son** : activé ou non.
- **Démarrage avec la box** : lance le récepteur au démarrage.
- **Afficher par-dessus les autres applis** : autorisation nécessaire pour que la recopie s'ouvre au-dessus d'une autre application.

## Installation

Sur n'importe quelle TV ou box Android TV 11+ :

- **Downloader** : code **2534592**.
- **Lien direct** (toujours la dernière version) : https://github.com/vantaflow/kairo-cast/releases/latest/download/kairo-cast.apk

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

## Windows

Kairo Cast existe aussi pour Windows : une petite appli qui envoie l'écran et le son du PC vers la box. Windows ne sait pas envoyer l'écran vers un récepteur AirPlay, d'où cette appli. Le Miracast natif de Windows vers la box n'est pas possible : une appli Android ne peut pas être récepteur Miracast sans droits système.

- **Installation** : l'exécutable est pour l'instant produit par la CI sous forme d'artefact `KairoCast-windows` ; il sera joint à la prochaine release. Il n'est pas signé : SmartScreen affiche un avertissement, cliquer sur « Informations complémentaires › Exécuter quand même ».
- **Utilisation** : l'appli KairoCast intercepte Win+K et ouvre son propre panneau, où Kairo apparaît en premier (avec un lien vers le panneau natif de Windows). On peut aussi utiliser l'icône près de l'horloge.
- **Pare-feu** : au premier partage, Windows demande d'autoriser l'appli ; accepter « Réseau privé ».
- **Prérequis** : Windows 10 1903 ou plus récent, ou Windows 11.
- **Journal** : `%LOCALAPPDATA%\KairoCast\log.txt`.

## Licence et crédits

GPL-3.0.

- [UxPlay](https://github.com/FDH2/UxPlay) (GPL-3.0) : serveur AirPlay sur lequel repose le récepteur.
- [libplist](https://github.com/libimobiledevice/libplist) (LGPL-2.1).
- [NAudio](https://github.com/naudio/NAudio) (MIT) et [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) : capture audio et vidéo de l'appli Windows.
- OpenSSL 1.1.1 (licence OpenSSL/SSLeay), fourni par le paquet prefab `com.android.ndk.thirdparty:openssl` et lié dynamiquement.

GPL-3.0 et OpenSSL 1.1.1 : la licence OpenSSL/SSLeay n'est pas considérée comme compatible avec la GPL, sauf à bénéficier d'une exception pour les bibliothèques du système. Ici, OpenSSL est une bibliothèque externe fournie sous forme de paquet et liée dynamiquement, pas une bibliothèque du système Android ; cette situation est signalée telle quelle, sans exception de licence ajoutée.
