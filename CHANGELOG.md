# Journal des modifications

Les versions jusqu'à la 0.26.0 sont décrites dans les
[notes de chaque release](../../releases).

## Non publié

- Réglage « G-SYNC » dans Optimisation > NVIDIA : il rallume un G-SYNC coupé dans le pilote (réglage `VRR_MODE` publié par
  NVIDIA, valable aussi pour G-SYNC Compatible, c'est-à-dire FreeSync), en mode plein écran, avec la confirmation, la
  sauvegarde et la restauration habituelles. Décoché d'office ; un G-SYNC déjà actif n'est pas touché. Le README disait à
  tort que G-SYNC était absent de l'API officielle, et le diagnostic renvoie maintenant vers ce réglage.
- Contrôle des transitions : la carte passe du repos à des rafales de calcul vérifié à 5, 20, 50 et 100 % de charge, et la
  mémoire écrite avant chaque repos est relue après chaque phase. Un test soutenu à pleine charge ne traverse jamais les
  états bas de la carte, où un décalage mémoire ou un undervolt peut faire fauter une carte qui tient à pleine charge. Il
  relève l'état de la carte par NVML pour prouver qu'elle a bougé : presque immobile ou illisible, le verdict est
  « à surveiller », jamais « réussi ». Il s'ajoute à chaque validation de 15 minutes (2 minutes, toutes les cartes) et se
  lance à la main avec `Aria-Bench.exe --transitions` (`--auto-controle` pour vérifier que le test voit les erreurs).
- Contrôle des cœurs Tensor : calcul INT8 sur les cœurs Tensor (API CUDA du pilote, noyau PTX embarqué, rien à installer
  de plus), chaque résultat comparé au calcul exact fait sur le processeur. Il s'ajoute à la validation de 15 minutes
  des cartes RTX, avant le calcul vérifié et la mémoire, et se lance à la main avec `Aria-Bench.exe --tenseurs`
  (`--auto-controle` pour vérifier que le test voit bien les erreurs). Impossible sur la machine (pilote sans CUDA,
  carte avant Turing) : « non testé » dans le rapport, sans juger le palier. Ne couvre que le mode INT8.

## 0.29.1, le 13 novembre 2026

Notes complètes : [docs/releases/v0.29.1.md](docs/releases/v0.29.1.md).

- Installation par l'exe : la fenêtre se ferme d'elle-même une fois la copie terminée. Dans la 0.29.0, elle restait sur
  « Copie en cours… » alors que les fichiers étaient en place, et Aria ne se relançait pas depuis son dossier.
- README et notes : le zip se décompresse d'abord ; lancé depuis son aperçu, l'exe est seul, sans le banc d'essai.

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
