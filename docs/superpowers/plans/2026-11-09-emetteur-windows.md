# Plan — Kairo Cast pour Windows

Spec : `docs/superpowers/specs/2026-11-09-emetteur-windows-design.md`

- **Task 1** — `LinkProtocol.kt` + tests JVM : poignée de main, en-têtes, FORMAT_VIDEO, FORMAT_AUDIO, rejets (longueur, magie, version).
- **Task 2** — `AudioPlayer` mode PCM (`CT_PCM`, fréquence/canaux) + `MirrorPipeline` le transmet ; test de la sélection du mode.
- **Task 3** — `LinkServer.kt` (session unique, délai 5 s, occupé) + test JVM sur socket locale avec un listener factice.
- **Task 4** — Branchement dans `ReceiverService` (démarrage/arrêt, exclusion AirPlay/Link) et annonce `_kairocast._tcp` ; version 1.1.0.
- **Task 5** — Solution .NET `windows/` : bibliothèque `KairoCast.Core` (protocole, découpage AUD, mDNS) + tests xUnit.
- **Task 6** — Émetteur : processus ffmpeg (choix de l'encodeur), capture son WASAPI, envoi, PING.
- **Task 7** — Fenêtre WinForms, ffmpeg LGPL embarqué, publication en un seul exe.
- **Task 8** — Essai réel PC → box, correctifs.
- **Task 9** — README, site Kairo, release v1.1.0.
