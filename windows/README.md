# Kairo Cast pour Windows

## Pare-feu Windows

Au premier lancement, Windows peut demander d'autoriser l'application : accepter sur le **réseau privé**,
sinon la découverte des box (mDNS, UDP 5353) et la connexion ne fonctionneront pas.
L'appli appelle aussi `Firewall.EnsureRule()` (KairoCast.Link) au démarrage : elle crée les règles entrantes
UDP 5353 et TCP (pour l'exécutable) avec une seule demande d'élévation UAC. Si l'élévation est refusée,
l'appli continue et le journal l'indique.

## Dupliquer ou Étendre

Réglage `ShareMode` de `%APPDATA%\KairoCast\settings.json` (`Duplicate` par défaut, ou `Extend`), modifiable dans la
fenêtre principale (section **Mode**) et dans le panneau Win+K (boutons **Dupliquer** / **Étendre**). Changer de mode
pendant un partage relance la session.

- **Dupliquer** : comportement historique, capture de l'écran principal. Si le PC a plusieurs écrans, la fenêtre
  principale propose **Écran à recopier** (réglage `DuplicateScreen`, nom GDI `\.\DISPLAYn` ; écran disparu = principal).
- **Étendre** : capture d'un écran virtuel que Windows traite comme un deuxième écran étendu, placé à droite des autres,
  dans la définition envoyée à la box (1920×1080, ou 2560×1440 en qualité 1440p).

### Écran virtuel (`KairoCast.App/Display`)

Pilote : [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (MIT, signé SignPath),
matériel `Root\MttVDD`. `DisplayLogic.cs` contient la logique pure (testée dans `KairoCast.Tests`), `DisplayNative.cs`
les appels user32, `VirtualDisplay.cs` la détection (SetupAPI), l'installation et l'allumage.

- **Installation (une fois)** : `winget install --id VirtualDrivers.Virtual-Display-Driver -e` (paquet *portable* :
  application VDD Control, `devcon.exe` signé Microsoft et pilote signé, dans
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages\VirtualDrivers.Virtual-Display-Driver_*`, sans élévation). Puis
  KairoCast.exe se relance élevé avec `--vdd-install <dossier du paquet>` (**une seule demande UAC**) : certificat du
  signataire ajouté à `LocalMachine\TrustedPublisher` (sinon Windows ouvre une seconde fenêtre de confirmation),
  `C:\VirtualDisplayDriver\vdd_settings.xml` écrit s'il n'existe pas (un moniteur, 1280×720 à 3840×2160, 60 Hz),
  puis `devcon install MttVDD.inf Root\MttVDD`. Windows allume le nouvel écran d'office : l'appli l'éteint aussitôt.
  En ligne de commande : `KairoCast.exe --install-virtual-display`.
- **Allumage / extinction, sans élévation** (`DisplayConfig.cs`) : API CCD `QueryDisplayConfig`/`SetDisplayConfig`,
  comme Paramètres › Écran. Pour allumer, le chemin « source du pilote → moniteur MTT1337 » est ajouté aux chemins
  actifs des écrans réels (qui ne changent pas) ; si Windows avait mis l'écran virtuel en recopie (cas d'un portable
  juste après l'installation), il en est d'abord retiré. Puis `ChangeDisplaySettingsEx` règle la définition et la place
  à droite des autres écrans. Pour éteindre, les chemins vers le moniteur virtuel sont retirés des chemins actifs.
  Le pilote reste installé et activé ; hors partage, son écran est simplement déconnecté du bureau. Le réglage
  `VirtualDisplayOn` note qu'il est allumé : après un arrêt brutal, l'appli l'éteint au démarrage suivant.

- **Pourquoi pas le tube de contrôle du pilote** : `\.\pipe\MTTVirtualDisplayPipe` est ouvert à tous
  (`D:(A;;GA;;;WD)`), mais `SETDISPLAYCOUNT` réécrit `vdd_settings.xml` puis réinitialise l'adaptateur : plus lent,
  et le dossier de configuration n'est pas modifiable sans droits d'administrateur. Désactiver le périphérique
  (Gestionnaire de périphériques) demanderait une élévation à chaque partage.
- **Identification** : la sortie est celle dont l'adaptateur s'appelle `Virtual Display Driver` (ou dont le moniteur
  a l'EDID `MTT1337`). La capture prend son HMONITOR (`EnumDisplayMonitors`) ; le journal indique aussi la sortie DXGI
  correspondante (adaptateur de rendu et index).
- **Diagnostic** : `KairoCast.exe --displays` liste les écrans vus par l'appli et l'état du pilote ;
  `KairoCast.exe --box <ip> --extend` lance un partage Étendre en ligne de commande, `--screen \.\DISPLAY2` un partage
  Dupliquer d'un autre écran. Les lignes « Écran virtuel : » du journal détaillent chaque étape.
- **Désinstallation** : Gestionnaire de périphériques › Cartes graphiques › Virtual Display Driver › Désinstaller
  l'appareil (cocher la suppression du pilote), puis `winget uninstall --id VirtualDrivers.Virtual-Display-Driver -e`.
