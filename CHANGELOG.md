# Journal des modifications

Les versions jusqu'à la 0.26.0 sont décrites dans les
[notes de chaque release](../../releases).

## Non publié

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
