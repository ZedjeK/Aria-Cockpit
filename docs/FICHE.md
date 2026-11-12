# Fiche projet : Aria Cockpit

Ce que cette appli a de propre. À relire avant toute modification, et à tenir à
jour quand l'appli change.

| | |
| --- | --- |
| Nom | Aria Cockpit (dépôt et exe : `Aria-Cockpit`) |
| Promesse | Le tableau de bord de ton PC de jeu : il analyse le matériel, surveille la carte graphique et propose les bons réglages Windows et NVIDIA. Rien n'est modifié sans accord, tout se restaure. |
| Élément orange du logo | Un compteur, au pied du A |
| Fond animé | Balayage horizontal : un faisceau traverse la zone des pages et allume des points orange. Aléatoire : la position et le nombre des points, à chaque passage. Par défaut : 2 à 5 points, vitesse normale. |
| Stack | C# et .NET 10, WPF, exe autonome en un seul fichier |
| Droits administrateur | Oui, pour toute l'appli (interface et console) : registre de la machine, services, pilote NVIDIA, tâches planifiées |
| Démarrage automatique | Oui, désactivé par défaut. Tâche planifiée « Aria Cockpit\Zone de notification », argument `--tray`. Deux autres tâches possibles : profil GPU (`--gpu-profile`) et limite de charge (`--limite-charge`). |
| Port local (interface web) | aucun |
| Données de l'utilisateur | `%USERPROFILE%\.Aria-Cockpit`, modifiable par les seuls administrateurs ; secrètes : aucune |
| Écritures hors du dossier de données | Voir la liste ci-dessous |
| Connexions sortantes | Voir la liste ci-dessous |
| Composants tiers embarqués | WPF-UI, CommunityToolkit.Mvvm, System.Management, polices Space Grotesk et IBM Plex Mono, runtime .NET, données `nvidia-gpu-data.json`. Détail : `THIRD-PARTY-NOTICES.txt` |
| Réussite de la v0.1 | sans objet : l'appli a dépassé la 0.1 |

## Écritures hors du dossier de données

- `C:\Program Files\Aria Cockpit` : une copie de l'exe, que lancent les tâches planifiées.
- Tâches planifiées : jusqu'à trois, créées et supprimées à la demande.
- Registre de la machine et de l'utilisateur : les réglages que l'appli applique, après sauvegarde.
- Services Windows : type de démarrage de services existants, après sauvegarde. Aucun service n'est créé.
- Point de restauration Windows : créé avant les changements.
- Fichiers supprimés par le nettoyage : fichiers temporaires et caches, après confirmation.
- `%TEMP%` : sorties du banc d'essai, copie de la base de NVIDIA App, fichier de tâche supprimé aussitôt.

## Connexions sortantes

- `api.github.com` : dernière version publiée d'Aria Cockpit.
- `gfwsl.geforce.com` : dernière version du pilote NVIDIA.
- `raw.githubusercontent.com` : table des cartes NVIDIA, tenue par un dépôt tiers (`ZenitH-AT/nvidia-data`). Une copie est embarquée si la lecture échoue.
- Windows Update et winget : appelés par Windows, à la demande de l'appli.

Les autres adresses (pages Microsoft, recherche de BIOS) sont des liens ouverts
dans le navigateur : l'appli ne s'y connecte pas elle-même.

## Écarts connus avec les règles communes

- Toute l'appli tourne en administrateur, pas seulement les opérations qui l'exigent.
- L'appli écrit dans `%TEMP%`.
- La mise à jour se limite à signaler une version et à ouvrir sa page.
- L'argument de démarrage est `--tray`, pas `--silencieux`.
