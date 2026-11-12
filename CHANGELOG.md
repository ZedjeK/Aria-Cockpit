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
