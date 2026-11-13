# Journal des modifications

Les versions jusqu'à la 0.26.0 sont décrites dans les
[notes de chaque release](../../releases).

## Non publié

- Rien pour l'instant.

## 0.29.0, le 13 novembre 2026

Notes complètes : [docs/releases/v0.29.0.md](docs/releases/v0.29.0.md).

- Installation par l'exe : lancé hors de son dossier, l'exe propose de se ranger dans `.Aria-Cockpit\programme`
  avec son banc d'essai, et de créer un raccourci sur le Bureau (case décochée d'office). La proposition se coupe dans
  Paramètres, ou avec `--portable`. L'installation vérifie les droits du dossier et refuse si un programme sans droits
  pouvait le modifier.
- Paramètres, carte « Installation » : état de l'installation, case pour remettre la proposition, création, mise à jour
  et retrait du raccourci du Bureau.
- Fond animé : le faisceau monte jusqu'en haut de la fenêtre, derrière la barre de titre, au lieu de s'arrêter
  sous elle avec un bord coupé net.

## 0.28.0, le 13 novembre 2026

Notes complètes : [docs/releases/v0.28.0.md](docs/releases/v0.28.0.md).

- Menu latéral à gauche, à la place de la barre d'onglets du haut. « Paramètres » y est écrit en toutes lettres.
- Panneaux à coins coupés, avec un fin liseré cyan, sans halo. Un panneau d'alerte garde sa couleur.
- Boutons : le secondaire passe en contour cyan, et un niveau discret apparaît pour annuler, fermer ou remettre à plus tard.
- Le cyan marque ce qui est actif : la catégorie choisie dans Optimisation et Restauration.
- Icône de notification : une seule pastille à la fois (orange, cyan, bleue). La pastille jaune devient orange ; la bleue signale une mise à jour d'Aria.
- Menu de l'icône en blocs titrés, avec « Voir la mise à jour » quand une version est disponible, et « Paramètres ».

## 0.27.0, le 13 novembre 2026

Notes complètes : [docs/releases/v0.27.0.md](docs/releases/v0.27.0.md).

- Fond animé : un faisceau balaie la fenêtre et allume des points tirés au hasard. Il se règle dans les Paramètres (affichage, vitesse, nombre de points) et s'arrête quand la fenêtre est rangée ou réduite.
- Panneaux opaques, pour que le texte reste lisible au-dessus du fond.
- Fiche projet (`docs/FICHE.md`) et section « Limites à connaître » du README.
- Les réglages de l'appli sont dans `reglages.json`. L'ancien `parametres.json` est copié une fois, puis laissé tel quel.
- `journal.json` garde les 500 dernières opérations : date, type, bilan et rapport correspondant. Les rapports restent où ils sont.
- Les fichiers de travail (banc d'essai, lecture de NVIDIA App, création de tâche) vont dans `cache\`, dans le dossier des données, et plus dans le dossier temporaire de Windows.
- Mise à jour d'Aria Cockpit : le bandeau ouvre une fenêtre qui montre ce qui change, télécharge la nouvelle version sur demande et contrôle sa taille et son empreinte SHA-256. Un fichier différent de celui de la release est supprimé.
- « Installer et relancer » met la version en cours de côté dans `Sauvegardes\versions`, remplace les exes et relance Aria. Si une étape échoue, tout est remis comme avant. Une seule version est gardée, la précédente.
- Release : un tag `vX.Y.Z` poussé déclenche les tests, la fabrication des deux zips et leur attestation de provenance, puis une release en brouillon, à relire et à publier à la main. Les notes de chaque version sont dans `docs/releases/`. `tools\empaqueter.ps1` fabrique les mêmes zips sur son PC.
- Paramètres, carte « Mises à jour d'Aria Cockpit » : décocher la recherche au lancement, chercher à la main, revenir à la version gardée.
