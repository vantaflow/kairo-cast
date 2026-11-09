# Kairo Cast pour Windows

## Pare-feu Windows

Au premier lancement, Windows peut demander d'autoriser l'application : accepter sur le **réseau privé**,
sinon la découverte des box (mDNS, UDP 5353) et la connexion ne fonctionneront pas.
L'appli appelle aussi `Firewall.EnsureRule()` (KairoCast.Link) au démarrage : elle crée les règles entrantes
UDP 5353 et TCP (pour l'exécutable) avec une seule demande d'élévation UAC. Si l'élévation est refusée,
l'appli continue et le journal l'indique.

## Écran de la TV : Dupliquer ou Étendre (Win+P)

Réglages de `%APPDATA%\KairoCast\settings.json` : `UseVirtualDisplay` (vrai par défaut) et `DuplicateScreen`
(nom GDI `\\.\DISPLAYn` de l'écran recopié ; absent ou disparu = principal).

- **Sans pilote** (ou `UseVirtualDisplay` faux) : comportement historique, capture de l'écran principal ou de
  `DuplicateScreen`. La disposition des écrans et le pilote ne sont jamais touchés.
- **Pilote prêt** : pendant un partage, la TV est un écran de Windows. Il arrive en recopie de `DuplicateScreen` (ou
  du principal), puis Win+P / Paramètres décident ; la capture suit toujours l'écran de la TV (en recopie, c'est la
  source qu'il partage, `\\.\DISPLAY1` par exemple ; en extension, sa propre source).

### Écran virtuel (`KairoCast.App/Display`)

Pilote : [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT, signé SignPath),
matériel `Root\MttVDD`, moniteur EDID `MTT1337`.

| Fichier | Rôle |
| --- | --- |
| `DisplayLogic.cs` | logique pure : chemin à allumer (recopie / extension), disposition vue par Win+P, LUID après redémarrage, fenêtres à rapatrier, tâches planifiées |
| `VirtualScreen.cs` | orchestration (début, fin, reprise après plantage) derrière `IDisplaySystem`, `IVirtualDevice`, `IRestoreStore` ; marqueur `FileRestoreStore` |
| `DisplayConfig.cs` | `DisplaySystem` : API CCD (`QueryDisplayConfig` / `SetDisplayConfig`) et fenêtres (`EnumWindows`) |
| `VirtualDisplay.cs` | détection SetupAPI, installation / préparation (élevée), `VirtualDevice` (tâches planifiées) |
| `DisplayNative.cs` | énumération GDI, modes, définition de l'écran virtuel |

Les deux premiers sont compilés dans `KairoCast.Tests` et testés avec des doubles (aucun appel à Windows).

- **Repos** : le périphérique est **désactivé**. Le pilote ne sait pas retirer son moniteur autrement (son tube
  `MTTVirtualDisplayPipe` n'a pas de commande pour cela, `SETDISPLAYCOUNT 0` est ramené à 1), et un moniteur présent
  mais éteint serait rallumé par Win+P › Étendre ou par Windows au branchement d'un écran.
- **Préparation (une fois, sur clic)** : `winget install --id VirtualDrivers.Virtual-Display-Driver -e` si le pilote
  manque (paquet portable dans `%LOCALAPPDATA%\Microsoft\WinGet\Packages\VirtualDrivers.Virtual-Display-Driver_*`),
  puis KairoCast.exe se relance élevé avec `--vdd-setup <SID> <dossier du paquet>` (**une demande UAC**) : certificat
  du signataire dans `LocalMachine\TrustedPublisher`, `C:\VirtualDisplayDriver\vdd_settings.xml` s'il n'existe pas,
  `devcon install MttVDD.inf Root\MttVDD`, `pnputil /disable-device`, puis les tâches `\KairoCast\VirtualDisplayOn`
  et `\KairoCast\VirtualDisplayOff` (au nom de l'utilisateur, `HighestAvailable`, sans déclencheur, action
  `%SystemRoot%\System32\pnputil.exe /enable-device|/disable-device "<identifiant d'instance>"`). La disposition est
  sauvegardée avant et restaurée après (`VirtualScreen.Guard`).
- **Début de partage** (`VirtualScreen.Start`) : `QueryDisplayConfig` (chemins + modes actifs, y compris un clone HDMI)
  sauvegardé dans `%LOCALAPPDATA%\KairoCast\display-restore.json` **avant** toute modification ; `schtasks /run` de
  la tâche « On » ; attente du moniteur ; puis `SetDisplayConfig` avec les chemins **sauvegardés** (pas ceux que
  Windows a pu appliquer à l'arrivée du moniteur) plus le chemin « source de l'écran principal → MTT1337 ». Si Windows
  refuse la recopie, l'écran virtuel est ajouté en extension, les autres écrans restant tels quels.
- **Pendant** : `SystemEvents.DisplaySettingsChanged` → `StreamSession.FollowTarget` recrée la capture sur la nouvelle
  source de la TV (Win+P), la liaison avec la box n'est pas coupée. « Écran du PC uniquement » : plus d'image, la box
  garde la dernière.
- **Fin** (`VirtualScreen.Stop` : arrêt, erreur, déconnexion de la box, fermeture de l'appli, fin de session Windows,
  exception non gérée) : liste des fenêtres sur l'écran virtuel s'il était étendu (hors `Progman`, `WorkerW`, barres
  des tâches, fenêtres d'outils, invisibles ou masquées) ; `SetDisplayConfig` de la disposition sauvegardée (LUID
  d'adaptateurs remis à jour par chemin de périphérique si Windows a redémarré ; à défaut, disposition mémorisée par
  Windows) ; tâche « Off » ; nouvelle restauration si Windows a changé la disposition au départ du moniteur ; fenêtres
  replacées sur la zone de travail de l'écran principal ; marqueur effacé. Marqueur conservé si rien n'a pu être
  restauré : nouvel essai au lancement suivant.
- **Lancement** (`VirtualScreen.Recover`) : marqueur présent (plantage) → même fin de partage ; pilote prêt mais
  activé hors partage → désactivé.
- **Diagnostic** : `KairoCast.exe --displays` (lecture seule) liste les écrans, la disposition et l'état du pilote.
  `KairoCast.exe --box <ip>` utilise l'écran virtuel s'il est prêt (`--no-virtual` pour s'en passer, `--screen
  \\.\DISPLAY2` pour l'écran recopié). Les lignes « Écran virtuel : » du journal détaillent chaque étape.
- **Désinstallation** : supprimer les deux tâches (`schtasks /delete /tn \KairoCast\VirtualDisplayOn /f`, idem
  `Off`), Gestionnaire de périphériques › Cartes graphiques › Virtual Display Driver › Désinstaller l'appareil (cocher
  la suppression du pilote), puis `winget uninstall --id VirtualDrivers.Virtual-Display-Driver -e`.
