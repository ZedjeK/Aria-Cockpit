**Aria Cockpit** {VERSION} : le tableau de bord de ton PC de jeu. Analyse du matériel, télémétrie GPU en direct, réglages Windows, NVIDIA et confidentialité, réglage GPU guidé (Équilibre, Économie, Performance) avec son banc d'essai 3D, mises à jour, nettoyage, restauration et rapports.

**Rien n'est modifié sans ton accord** : chaque changement est proposé, sauvegardé et réversible depuis la page Restauration.

## Quel fichier choisir ?

| Fichier | Pour qui |
|---|---|
| `Aria-Cockpit-{VERSION}-autonome.zip` | Tout le monde : rien à installer |
| `Aria-Cockpit-{VERSION}-leger.zip` | PC qui ont déjà le [runtime .NET 10 Desktop](https://dotnet.microsoft.com/download/dotnet/10.0) |

Décompresser (garder `Aria-Bench.exe`, le banc d'essai, à côté), puis lancer `Aria-Cockpit.exe` (Windows 11, droits administrateur demandés).

## Premier lancement

L'exécutable n'est pas signé : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur ». Clique sur « Informations complémentaires » puis « Exécuter quand même ». Le code source complet est dans ce dépôt.

## Nouveautés de la {VERSION}

- **Origine = valeurs d'usine** : « Revenir d'origine » et la mesure de départ d'Équilibre utilisent la limite de puissance d'usine lue sur la carte, même si NVIDIA App ou Afterburner l'avaient baissée
- **Contrôle avant mesure** : si l'overclock de NVIDIA App est actif, l'assistant s'arrête et explique où le désactiver (lecture seule, rien n'est écrit chez NVIDIA) ; nouvelle vérification dans Mon PC
- **Performance revu** : part de la courbe d'usine à puissance maximale et monte jusqu'au plus haut palier stable ; à la fin, l'**avis d'Aria** compare le gain à Équilibre (FPS, watts, température, FPS par watt) et dit si ça vaut le coup
- **Revenir à Équilibre** en un clic à la fin d'Économie ou de Performance, et **Réglage d'Équilibre** dans le réglage manuel
- **Corrections** : mesures jugées « non concluantes » à tort avec un plafond et un décalage, bouton « Revenir d'origine » grisé au démarrage, plafond fantôme affiché après un redémarrage, mesure plafonnée refaite à chaque mode
- **Journal des réglages** (`gpu-profile.log`) et tests automatisés

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, validation de 15 minutes, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
