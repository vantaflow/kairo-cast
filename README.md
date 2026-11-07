# Kairo Cast

Récepteur AirPlay de recopie d'écran pour Android TV, compagnon de Kairo. Il permet d'afficher l'écran d'un Mac (image jusqu'en 4K et son) sur une box ou un téléviseur Android TV.

## Utilisation

1. Installer l'application sur la box et l'ouvrir une fois : elle démarre ensuite toute seule avec la box.
2. Sur le Mac : **Centre de contrôle › Recopie de l'écran › Kairo** (ou le nom choisi dans les réglages).
3. La recopie s'affiche en plein écran sur la TV. Pendant la recopie, la touche **OK** de la télécommande ouvre un petit menu.

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

## Licence et crédits

GPL-3.0.

- [UxPlay](https://github.com/FDH2/UxPlay) (GPL-3.0) : serveur AirPlay sur lequel repose le récepteur.
- [libplist](https://github.com/libimobiledevice/libplist) (LGPL-2.1).
- OpenSSL 1.1.1 (licence OpenSSL/SSLeay), fourni par le paquet prefab `com.android.ndk.thirdparty:openssl` et lié dynamiquement.

GPL-3.0 et OpenSSL 1.1.1 : la licence OpenSSL/SSLeay n'est pas considérée comme compatible avec la GPL, sauf à bénéficier d'une exception pour les bibliothèques du système. Ici, OpenSSL est une bibliothèque externe fournie sous forme de paquet et liée dynamiquement, pas une bibliothèque du système Android ; cette situation est signalée telle quelle, sans exception de licence ajoutée.
