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

Télécharger **[KairoCast.exe](https://github.com/vantaflow/kairo-cast/releases/latest/download/KairoCast.exe)** et le lancer : rien à installer pour recopier l'écran. En option, un pilote d'écran virtuel fait de la TV un vrai écran de Windows pendant le partage, pour choisir **Dupliquer** ou **Étendre** avec Win+P (voir [Dupliquer ou Étendre](#dupliquer-ou-étendre)).

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
3. Cliquer sur **Partager** à côté de la box. Le même bouton arrête le partage. Avec l'écran virtuel, **Win+P** (ou les boutons **Dupliquer** / **Étendre** du panneau) choisit ce que montre la TV pendant le partage.

Fenêtre principale (clic sur l'icône) :

- **Box Kairo** : les box trouvées automatiquement sur le réseau. Si une box n'apparaît pas (VPN, routeur qui bloque la découverte, autre sous-réseau), le lien **Box introuvable ? Ajouter par adresse IP** permet de la saisir (l'adresse est affichée sur l'accueil de Kairo Cast).
- **Écran de la TV** : état de l'écran virtuel (bouton **Installer** ou **Activer l'écran virtuel…** s'il manque), case pour s'en passer, et, si le PC a plusieurs écrans, **Écran à recopier** (l'écran principal par défaut).
- **Qualité** : 1080p 30 i/s (par défaut, le plus sûr), 1080p 60 i/s (plus fluide) ou 1440p 30 i/s (plus net). L'encodage utilise la carte graphique ; l'encodeur utilisé s'affiche sous l'état.
- **Envoyer le son** : le son du PC passe sur la TV.
- **Remplacer Win+K** : Win+K ouvre le panneau Kairo, avec un lien **Autres écrans (Windows)…** vers le panneau natif ; décoché, le raccourci devient **Win+Alt+K**.
- **Démarrer avec Windows**.
- Fermer la fenêtre la réduit près de l'horloge ; **Quitter** depuis le menu de l'icône.

Journal en cas de problème : `%LOCALAPPDATA%\KairoCast\log.txt`.

### Dupliquer ou Étendre

Sans pilote, Kairo Cast recopie l'écran principal (ou celui choisi) : l'affichage de Windows n'est jamais modifié.

Avec le pilote open source **[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)** (signé, licence MIT), la TV devient **un écran de Windows pendant le partage**, comme un écran branché :

- **Au début du partage**, Kairo Cast note la disposition actuelle des écrans, ajoute l'écran de la TV **en recopie** de l'écran principal (Dupliquer) et ne touche à rien d'autre : une TV déjà branchée en HDMI en recopie le reste.
- **Pendant le partage**, c'est Windows qui décide : **Win+P › Dupliquer**, **Étendre** (la TV devient un deuxième bureau, à la définition envoyée à la box) ou **Deuxième écran uniquement**, ou **Paramètres › Système › Écran**. L'image envoyée à la box suit ces changements sans couper la session. Les boutons **Dupliquer** / **Étendre** du panneau Win+K font la même chose pour la TV seule (les autres écrans ne bougent pas).
- **À la fin** (arrêt, erreur, box déconnectée, fermeture de l'appli ou de la session Windows), la disposition d'avant est remise **à l'identique** et les fenêtres restées sur la TV reviennent sur l'écran principal. Si l'appli plante, elle le fait à son lancement suivant (la disposition est gardée sur disque jusque-là).
- **Hors partage, l'écran virtuel n'existe pas pour Windows** : le pilote est désactivé, Win+P et les branchements d'écrans ne le voient pas.

Installation, seulement sur clic (bouton **Installer l'écran virtuel…** de la fenêtre principale ou lien du panneau), jamais automatiquement :

- Kairo Cast télécharge le pilote avec **winget** (paquet `VirtualDrivers.Virtual-Display-Driver`, sans droits d'administrateur), puis Windows affiche **une seule demande d'autorisation** (UAC). Cette étape installe le pilote, ajoute le certificat de son éditeur (SignPath Foundation) aux éditeurs approuvés (sinon Windows ouvrirait une seconde fenêtre), écrit sa configuration dans `C:\VirtualDisplayDriver\vdd_settings.xml` (un seul écran virtuel), le **désactive** aussitôt et crée deux tâches planifiées `\KairoCast\VirtualDisplayOn` / `Off` qui ne font que `pnputil /enable-device` / `/disable-device` sur ce périphérique. L'affichage est sauvegardé avant et remis tel quel après.
- Si le pilote est déjà installé (désactivé ou non), le bouton devient **Activer l'écran virtuel…** : même demande d'autorisation, sans téléchargement.
- **Ensuite, aucune demande d'autorisation** : l'appli lance ces tâches au début et à la fin de chaque partage. Il faut un compte administrateur du PC.
- En ligne de commande : `KairoCast.exe --setup-virtual-display`.
- **Désinstallation** : `schtasks /delete /tn \KairoCast\VirtualDisplayOn /f` et `schtasks /delete /tn \KairoCast\VirtualDisplayOff /f` (invite de commandes administrateur) ; **Gestionnaire de périphériques › Cartes graphiques › Virtual Display Driver › Désinstaller l'appareil**, en cochant **Tenter de supprimer le pilote de cet appareil** ; puis `winget uninstall --id VirtualDrivers.Virtual-Display-Driver -e`. On peut aussi supprimer le dossier `C:\VirtualDisplayDriver` et, dans `certlm.msc › Éditeurs approuvés`, le certificat « SignPath Foundation ».

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
- **La TV montre un bureau vide après un plantage** (ou un écran fantôme reste après un partage) : appuyer sur **Win+P › Dupliquer** ; relancer Kairo Cast remet aussi la disposition d'avant le partage et désactive l'écran virtuel. En dernier recours : **Gestionnaire de périphériques › Cartes graphiques › Virtual Display Driver › Désactiver l'appareil**.
- **Écran virtuel : « Annulé : Windows n'a pas reçu l'autorisation »** : la demande UAC a été refusée ; recliquer sur **Installer** (ou **Activer**) **l'écran virtuel…**. Sur un PC géré par une entreprise, l'installation de pilotes peut être interdite : décocher l'écran virtuel, la TV recopie alors l'écran comme avant.
- **Écran virtuel : « winget est introuvable »** : installer **App Installer** depuis le Microsoft Store (winget est inclus d'office dans Windows 11 et Windows 10 récent).
- **Écran virtuel : « le pilote ne s'est pas activé »** : redémarrer le PC si le pilote vient d'être installé ; sinon recliquer sur **Activer l'écran virtuel…**. `KairoCast.exe --displays` liste, sans rien modifier, les écrans et l'état du pilote ; les lignes « Écran virtuel : » du journal détaillent chaque étape.

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
- **Kairo Link** (Windows) : la box annonce `_kairocast._tcp` ; le PC capture l'écran (Windows.Graphics.Capture : l'écran de la TV quand l'écran virtuel est là, sinon l'écran choisi), l'encode en H.264 par la carte graphique et l'envoie en TCP, avec le son en PCM. Comme AirPlay sans code, Kairo Link accepte n'importe quel appareil du réseau local.

## Licence et crédits

GPL-3.0.

- [UxPlay](https://github.com/FDH2/UxPlay) (GPL-3.0) : serveur AirPlay sur lequel repose le récepteur.
- [libplist](https://github.com/libimobiledevice/libplist) (LGPL-2.1).
- [NAudio](https://github.com/naudio/NAudio) (MIT) et [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) : capture audio et vidéo de l'appli Windows.
- [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT, © 2024 Virtual Display, Mike Rodriguez « MikeTheTech » et contributeurs ; pilote signé par SignPath Foundation) : écran virtuel qui fait de la TV un écran de Windows (Dupliquer / Étendre). Il n'est pas inclus dans KairoCast.exe : il est téléchargé par winget depuis ses publications officielles, à la demande de l'utilisateur.
- OpenSSL 1.1.1 (licence OpenSSL/SSLeay), fourni par le paquet prefab `com.android.ndk.thirdparty:openssl` et lié dynamiquement.

GPL-3.0 et OpenSSL 1.1.1 : la licence OpenSSL/SSLeay n'est pas considérée comme compatible avec la GPL, sauf à bénéficier d'une exception pour les bibliothèques du système. Ici, OpenSSL est une bibliothèque externe fournie sous forme de paquet et liée dynamiquement, pas une bibliothèque du système Android ; cette situation est signalée telle quelle, sans exception de licence ajoutée.
