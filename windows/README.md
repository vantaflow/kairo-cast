# Kairo Cast pour Windows

## Pare-feu Windows

Au premier lancement, Windows peut demander d'autoriser l'application : accepter sur le **réseau privé**,
sinon la découverte des box (mDNS, UDP 5353) et la connexion ne fonctionneront pas.
L'appli appelle aussi `Firewall.EnsureRule()` (KairoCast.Link) au démarrage : elle crée les règles entrantes
UDP 5353 et TCP (pour l'exécutable) avec une seule demande d'élévation UAC. Si l'élévation est refusée,
l'appli continue et le journal l'indique.
