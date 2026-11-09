# Kairo Cast

[![Dernière version](https://img.shields.io/github/v/release/vantaflow/kairo-cast?label=version&color=2563eb)](https://github.com/vantaflow/kairo-cast/releases/latest) ![Android TV 11+](https://img.shields.io/badge/Android%20TV-11%2B-3ddc84) ![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078d4) ![Code Downloader 2534592](https://img.shields.io/badge/Downloader-2534592-f97316)

**L'écran du Mac, de l'iPhone ou du PC Windows sur la TV**, avec le son, sans limite de durée et sans abonnement. Kairo Cast est un récepteur de recopie d'écran pour Android TV, compagnon du launcher [Kairo](https://github.com/vantaflow/kairo). Il peut afficher jusqu'à **quatre écrans à la fois** en mosaïque.

| Appareil qui envoie | Comment | Ce qu'il faut |
|---|---|---|
| Mac, iPhone, iPad | AirPlay, intégré au système | rien à installer |
| PC Windows 10/11 | appli **Kairo Cast pour Windows** | [KairoCast.exe](https://github.com/vantaflow/kairo-cast/releases/latest/download/KairoCast.exe) |

Sommaire : [Installation](#installation) · [Depuis un Mac ou un iPhone](#depuis-un-mac-ou-un-iphone) · [Depuis un PC Windows](#depuis-un-pc-windows) · [Plusieurs sources](#plusieurs-sources-mosaïque) · [Télécommande](#télécommande-pendant-la-recopie) · [Réglages](#réglages-de-la-box) · [Dépannage](#dépannage) · [Compilation](#compilation) · [Licence](#licence-et-crédits)

## Installation

### Sur la TV ou la box (Android TV 11 ou plus récent)

- **Downloader** : code **2534592**.
- **Lien direct** (toujours la dernière version) : <https://github.com/vantaflow/kairo-cast/releases/latest/download/kairo-cast.apk>
- **Depuis Kairo** : proposée dans l'assistant de premier lancement et dans « Nos applications », installée en un clic.

Ouvrir l'application une fois : elle démarre ensuite toute seule avec la box et reste prête à recevoir. L'écran d'accueil affiche le nom du récepteur, son adresse IP et l'état de chaque source.

Une autorisation est à accorder une fois pour que la recopie s'ouvre par-dessus une autre application : **Réglages › Afficher par-dessus les autres applis** (ou, par ADB : `adb shell appops set com.vantaflow.kairo.cast SYSTEM_ALERT_WINDOW allow`).

> Depuis la 1.1.0, l'application est signée avec la clé Kairo. Si l'installation par-dessus la 1.0.0 est refusée, désinstaller d'abord Kairo Cast (les réglages repartent à zéro une seule fois).

### Sur le PC Windows

Télécharger **[KairoCast.exe](https://github.com/vantaflow/kairo-cast/releases/latest/download/KairoCast.exe)** et le lancer : rien à installer pour dupliquer l'écran. Seul le mode **Étendre** a besoin d'un pilote d'écran virtuel, installé une seule fois à la première utilisation (voir [Dupliquer ou Étendre](#dupliquer-ou-étendre)).

- **Prérequis** : Windows 10 version 1903 ou plus récent, ou Windows 11, 64 bits.
- L'exécutable n'est pas signé : SmartScreen affiche un avertissement la première fois, cliquer sur **Informations complémentaires › Exécuter quand même**.
- Au premier partage, Windows demande d'autoriser l'appli sur le réseau : accepter **Réseau privé** (sinon la box n'est pas trouvée).

## Depuis un Mac ou un iPhone

1. **Centre de contrôle › Recopie de l'écran › Kairo** (ou le nom choisi dans les réglages de la box).
2. La recopie s'ouvre en plein écran sur la TV, le son suit.
3. Pour arrêter : même menu, **Arrêter la recopie**.

La définition annoncée au Mac dépend du réglage **Netteté** et du nombre de sources ; le Mac adapte sa propre résolution pendant la recopie.

## Depuis un PC Windows

Windows ne sait pas envoyer son écran à un récepteur AirPlay, et une appli Android ne peut pas recevoir le Miracast natif de Windows : d'où **Kairo Cast pour Windows**, qui capture l'écran et le son du PC et les envoie à la box par le réseau local (protocole Kairo Link).

1. Lancer **KairoCast.exe** : l'appli se place près de l'horloge (zone de notification).
2. Appuyer sur **Win+K** (ou cliquer sur l'icône) : le panneau « Partager l'écran vers… » liste les box Kairo du réseau.
3. Choisir **Dupliquer** ou **Étendre** en haut du panneau, puis cliquer sur **Partager** à côté de la box. Le même bouton arrête le partage.

Fenêtre principale (clic sur l'icône) :

- **Box Kairo** : les box trouvées automatiquement sur le réseau. Si une box n'apparaît pas (VPN, routeur qui bloque la découverte, autre sous-réseau), le lien **Box introuvable ? Ajouter par adresse IP** permet de la saisir (l'adresse est affichée sur l'accueil de Kairo Cast).
- **Mode** : **Dupliquer** (par défaut) ou **Étendre**, voir ci-dessous. En mode Dupliquer, si le PC a plusieurs écrans, **Écran à recopier** choisit lequel (l'écran principal par défaut).
- **Qualité** : 1080p 30 i/s (par défaut, le plus sûr), 1080p 60 i/s (plus fluide) ou 1440p 30 i/s (plus net). L'encodage utilise la carte graphique ; l'encodeur utilisé s'affiche sous l'état.
- **Envoyer le son** : le son du PC passe sur la TV.
- **Remplacer Win+K** : Win+K ouvre le panneau Kairo, avec un lien **Autres écrans (Windows)…** vers le panneau natif ; décoché, le raccourci devient **Win+Alt+K**.
- **Démarrer avec Windows**.
- Fermer la fenêtre la réduit près de l'horloge ; **Quitter** depuis le menu de l'icône.

Journal en cas de problème : `%LOCALAPPDATA%\KairoCast\log.txt`.

### Dupliquer ou Étendre

Comme le panneau Win+K natif de Windows :

- **Dupliquer** : la TV affiche ce que montre un écran du PC (l'écran principal, ou celui choisi dans la fenêtre principale).
- **Étendre** : la TV devient un **deuxième écran**, placé à droite de l'écran principal. On y fait glisser des fenêtres (une vidéo, une présentation) tout en gardant son propre écran. La définition de cet écran est celle envoyée à la box (1920×1080, ou 2560×1440 avec la qualité 1440p). Changer de mode pendant un partage le relance aussitôt dans le nouveau mode.

Windows ne sait pas créer seul un écran supplémentaire : le mode Étendre s'appuie sur le pilote open source **[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)** (signé, licence MIT).

- **Installation, une seule fois** : au premier partage en mode Étendre, Kairo Cast propose de l'installer. Il le télécharge avec **winget** (paquet `VirtualDrivers.Virtual-Display-Driver`, sans droits d'administrateur), puis Windows affiche **une seule demande d'autorisation** (UAC) pour installer le pilote. Cette étape ajoute aussi le certificat de l'éditeur du pilote (SignPath Foundation) aux éditeurs approuvés, pour éviter une seconde fenêtre de Windows, et écrit sa configuration dans `C:\VirtualDisplayDriver\vdd_settings.xml` (un seul écran virtuel). En ligne de commande : `KairoCast.exe --install-virtual-display`.
- **Ensuite, aucune demande d'autorisation** : l'écran virtuel reste installé mais **éteint** hors partage. Kairo Cast l'allume au début d'un partage en mode Étendre et l'éteint à l'arrêt (par les réglages d'affichage de Windows, comme **Paramètres › Système › Écran**, ce qui ne demande pas de droits d'administrateur) ; les fenêtres qui s'y trouvaient reviennent sur l'écran principal. Si l'appli a été arrêtée brutalement pendant un partage, l'écran virtuel est éteint à son prochain démarrage.
- **Désinstallation** : dans le **Gestionnaire de périphériques › Cartes graphiques**, clic droit sur **Virtual Display Driver › Désinstaller l'appareil**, en cochant **Tenter de supprimer le pilote de cet appareil** ; puis `winget uninstall --id VirtualDrivers.Virtual-Display-Driver -e` pour supprimer le paquet téléchargé. On peut aussi supprimer le dossier `C:\VirtualDisplayDriver` et, dans `certlm.msc › Éditeurs approuvés`, le certificat « SignPath Foundation ».

## Plusieurs sources (mosaïque)

Dans les réglages de la box, **Nombre de sources** (1 à 4) crée autant de récepteurs, chacun avec son propre nom, visible sur le Mac, l'iPhone et le PC. Plusieurs appareils peuvent alors recopier en même temps :

- **Mosaïque** : *Remplir l'écran* (par défaut, sans bandes) ou *Formats exacts* (chaque source garde son format, sans déformation ni recadrage).
- **Netteté automatique** : avec 2 sources, 1800p ; avec 3 ou 4, 800p. Si la box ne peut pas décoder une source de plus, la tuile indique « Trop de sources ».
- **Son des sources** : une seule source est audible à la fois. Par défaut le son **suit la tuile sélectionnée** ; on peut aussi l'attacher à une source précise (réglage, ou touche Menu sur la tuile).

Avec une seule source, un émetteur à la fois : le dernier arrivé l'emporte. Si un PC prend la main pendant que le Mac est connecté, puis s'arrête, l'image du Mac ne revient pas d'elle-même (AirPlay ne permet pas de redemander une image) : l'accueil affiche « Mac en attente : relancez la recopie depuis le Mac ».

## Télécommande pendant la recopie

| Touche | Une source | Plusieurs sources |
|---|---|---|
| Flèches | — | déplacer la sélection entre les tuiles |
| OK | changer l'affichage (Étirer / Remplir / Ajuster) | agrandir ou réduire la tuile sélectionnée |
| Menu | changer l'affichage | rendre audible la tuile sélectionnée |

Retour ramène à l'accueil ; la recopie continue et **Reprendre la recopie** la réaffiche.

## Réglages de la box

- **Nom du récepteur** (un nom par source) : nom visible sur le Mac, l'iPhone et le PC. Deux sources ne peuvent pas porter le même nom.
- **Netteté** : Standard (1200p), Haute (1600p, par défaut) ou Maximale (2160p). Plus c'est net, plus la box et le réseau travaillent. Avec plusieurs sources, la netteté est fixée automatiquement.
- **Affichage** (une source seule) : *Étirer* (remplit l'écran pour les formats proches du 16:9), *Remplir* (rogne les bords) ou *Ajuster* (bandes noires, proportions conservées).
- **Nombre de sources**, **Mosaïque**, **Son des sources** : voir [Plusieurs sources](#plusieurs-sources-mosaïque).
- **Son** : activé ou non.
- **Démarrage avec la box** : le récepteur se lance au démarrage.
- **Afficher par-dessus les autres applis** : autorisation nécessaire pour que la recopie s'ouvre au-dessus d'une autre application.

## Dépannage

- **La box n'apparaît pas sur le PC** : ouvrir Kairo Cast au moins une fois sur la box, vérifier que le PC est sur le même réseau et que Windows a autorisé l'appli sur le **réseau privé** ; sinon, ajouter la box par son adresse IP.
- **« Partage impossible : aucune connexion n'a pu être établie »** : la box est en veille ou l'application redémarre ; réveiller la box et réessayer.
- **Image saccadée** : choisir 1080p 30 i/s sur le PC, rapprocher la box du routeur ou la brancher en Ethernet.
- **Pas de son** : vérifier **Son** sur la box et **Envoyer le son** sur le PC ; avec plusieurs sources, sélectionner la tuile qui doit être audible.
- **La recopie ne s'ouvre pas au-dessus d'une autre appli** : accorder **Afficher par-dessus les autres applis**.
- **Mode Étendre : « Installation annulée »** : la demande d'autorisation de Windows a été refusée ; relancer un partage en mode Étendre pour la reproposer. Sur un PC géré par une entreprise, l'installation de pilotes peut être interdite : rester en Dupliquer.
- **Mode Étendre : « winget est introuvable »** : installer **App Installer** depuis le Microsoft Store (winget est inclus d'office dans Windows 11 et Windows 10 récent).
- **Mode Étendre : « présent mais inactif »** : le pilote a été désactivé ; dans le Gestionnaire de périphériques, **Cartes graphiques › Virtual Display Driver › Activer l'appareil**. Si l'écran virtuel n'apparaît pas juste après l'installation, redémarrer le PC.
- **Un écran fantôme reste après un partage Étendre** : relancer Kairo Cast (il éteint l'écran virtuel au démarrage), ou dans **Paramètres › Système › Écran**, sélectionner l'écran virtuel et choisir **Déconnecter cet écran**. `KairoCast.exe --displays` liste les écrans vus par l'appli.

## Compilation

Application Android : JDK 17, SDK Android 34, NDK 27.2.12479018.

```
./gradlew :app:testDebugUnitTest :app:assembleRelease
```

La release est signée avec `keystore.properties` (non versionné : `storeFile`, `storePassword`, `keyAlias`, `keyPassword`) s'il existe, sinon avec la clé de debug. `scripts/sign-apk.sh <entrée.apk> <sortie.apk>` signe avec cette clé un APK déjà compilé (par exemple celui de la CI).

Installation sur une box par ADB (débogage réseau activé) :

```
scripts/deploy.sh <IP de la box>
```

Application Windows (.NET 8, dossier `windows/`) :

```
cd windows
dotnet test
dotnet publish KairoCast.App -c Release
```

La CI GitHub compile les deux à chaque envoi (artefacts `kairo-cast-release` et `KairoCast-windows`).

### Fonctionnement

- **AirPlay** : serveur natif basé sur UxPlay, un par source, annoncé en mDNS (`_airplay._tcp`, `_raop._tcp`). Vidéo H.264 décodée par le matériel de la box, son AAC.
- **Kairo Link** (Windows) : la box annonce `_kairocast._tcp` ; le PC capture l'écran (Windows.Graphics.Capture : l'écran choisi en Dupliquer, l'écran virtuel en Étendre), l'encode en H.264 par la carte graphique et l'envoie en TCP, avec le son en PCM. Comme AirPlay sans code, Kairo Link accepte n'importe quel appareil du réseau local.

## Licence et crédits

GPL-3.0.

- [UxPlay](https://github.com/FDH2/UxPlay) (GPL-3.0) : serveur AirPlay sur lequel repose le récepteur.
- [libplist](https://github.com/libimobiledevice/libplist) (LGPL-2.1).
- [NAudio](https://github.com/naudio/NAudio) (MIT) et [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) : capture audio et vidéo de l'appli Windows.
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT, © 2024 Virtual Display, Mike Rodriguez « MikeTheTech » et contributeurs ; pilote signé par SignPath Foundation) : écran virtuel du mode Étendre. Il n'est pas inclus dans KairoCast.exe : il est téléchargé par winget depuis ses publications officielles, à la demande de l'utilisateur.

- OpenSSL 1.1.1 (licence OpenSSL/SSLeay), fourni par le paquet prefab `com.android.ndk.thirdparty:openssl` et lié dynamiquement.

GPL-3.0 et OpenSSL 1.1.1 : la licence OpenSSL/SSLeay n'est pas considérée comme compatible avec la GPL, sauf à bénéficier d'une exception pour les bibliothèques du système. Ici, OpenSSL est une bibliothèque externe fournie sous forme de paquet et liée dynamiquement, pas une bibliothèque du système Android ; cette situation est signalée telle quelle, sans exception de licence ajoutée.
