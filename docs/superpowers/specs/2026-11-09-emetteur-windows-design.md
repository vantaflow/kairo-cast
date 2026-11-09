# Kairo Cast pour Windows — conception

## Intention

Kairo Cast reçoit aujourd'hui la recopie d'écran d'un Mac (AirPlay). Un PC Windows ne sait pas
émettre en AirPlay (chiffrement FairPlay propriétaire). On ajoute donc un **émetteur Windows**
(`KairoCast.exe`) et un **protocole propre**, « Kairo Link », que le récepteur Android écoute en
plus d'AirPlay. Rien ne change pour le Mac.

## Objectifs

- Depuis un PC Windows 10/11 du même réseau : choisir la box, cliquer « Recopier » ; l'écran et
  le son du PC s'affichent sur la TV, comme depuis le Mac.
- Un seul fichier `KairoCast.exe`, sans installation, publié dans les releases du dépôt.
- Le récepteur réutilise le pipeline existant (décodeur H.264, file d'images, activité plein écran).

## Hors périmètre

- Contrôle à distance (souris/clavier) depuis la TV, multi-émetteurs simultanés, émetteur Linux.
- Chiffrement du flux (réseau local, comme une box multimédia classique).

## Anti-objectifs

- Casser ou ralentir la réception AirPlay existante.
- Une latence perceptiblement pire qu'AirPlay sur un réseau correct (> ~300 ms).

## Protocole Kairo Link v1

- Découverte : mDNS `_kairocast._tcp`, nom = nom du récepteur, port **7100**, TXT `v=1`.
- TCP, entiers gros-boutistes.
- Poignée de main : l'émetteur envoie `"KCST"`, `u8 version=1`, `u16 len` + nom UTF-8 du PC.
  Le récepteur répond `"KCOK"`, `u16 largeurMax`, `u16 hauteurMax` (réglage Netteté), `u8 son`
  (1 = activé), ou `"KCNO"`, `u8 raison` (1 = occupé, 2 = version) puis ferme.
- Paquets émetteur → récepteur : en-tête `u8 type`, `u32 longueur`, `i64 pts (µs)`, puis la charge :
  - `1` FORMAT_VIDEO : 6 × `u32` (largeur, hauteur, x, y, largeur utile, hauteur utile) ;
  - `2` VIDEO : une unité d'accès H.264 Annex B (SPS/PPS répétés sur chaque image clé) ;
  - `3` FORMAT_AUDIO : `u32` fréquence, `u8` canaux (PCM 16 bits signé petit-boutiste) ;
  - `4` AUDIO : échantillons PCM ;
  - `5` PING (toutes les secondes), `6` FIN.
- Le récepteur ferme la session après 5 s sans paquet. Longueur > 8 Mo = flux invalide, fermeture.
- Une seule session à la fois : si une recopie (AirPlay ou Link) est en cours, réponse `KCNO 1`.
  Pendant une session Link, les rappels AirPlay sont ignorés.

## Récepteur Android (décidé)

- `LinkProtocol.kt` : lecture/écriture pure du protocole (testée sur JVM).
- `LinkServer.kt` : `ServerSocket` sur un thread, une session à la fois, alimente le même
  `ReceiverListener` que le serveur natif (`onVideoFormat`, `onVideoData`, audio).
- Son PCM : `AudioPlayer` gagne un mode PCM (pas de MediaCodec, écriture directe dans
  `AudioTrack`) ; nouveau type `CT_PCM`.
- `MdnsAdvertiser` annonce aussi `_kairocast._tcp`.
- Version 1.1.0 (versionCode 10100).

## Émetteur Windows (décidé)

- C# .NET 8, WinForms, publication autonome en un seul fichier (`win-x64`, compressé).
- Vidéo : **ffmpeg** en processus fils — capture `ddagrab` (Desktop Duplication), encodeur
  matériel essayé dans l'ordre `h264_nvenc`, `h264_qsv`, `h264_amf`, puis `h264_mf`
  (logiciel Microsoft si aucun GPU) ; sortie H.264 Annex B sur stdout, `bf=0`, GOP 2 s,
  débit selon la définition. Une version **LGPL** de ffmpeg est embarquée dans l'exe et extraite
  au premier lancement dans `%LOCALAPPDATA%\KairoCast` (licence jointe).
- Découpage du flux en unités d'accès par délimiteur AUD (`h264_metadata=aud=insert`).
- Son : capture WASAPI en boucle (NAudio), converti en PCM 16 bits, fréquence native.
- Découverte : requête mDNS minimale écrite à la main (PTR/SRV/A), plus saisie manuelle d'IP.
- Interface : liste des box, écran à recopier, case « Son », bouton Recopier/Arrêter, état.
- Définition : la plus petite entre l'écran du PC et le maximum annoncé par la box (mise à
  l'échelle par ffmpeg), 30 ou 60 i/s.

## Laissé au constructeur

Noms internes, structure des projets de test, valeurs fines de débit, présentation de la fenêtre.

## Livraison

Branche `feature/windows-sender`, tests verts (Gradle + `dotnet test`), essai réel sur la box
192.168.0.62, README + site Kairo mis à jour, release v1.1.0 avec l'APK et `KairoCast.exe`.
