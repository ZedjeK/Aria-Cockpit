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

- **Ray tracing retiré** : le préréglage **Ultra RT** du banc d'essai et le contrôle en ray tracing de la validation, arrivés avec la 0.20.0, disparaissent. Ils ne servaient pas à trouver un meilleur réglage : sur la tour de l'auteur, l'Ultra RT reste loin de la limite de puissance (260 W contre 318 W en Ultra), et rien ne montrait que son contrôle repère un réglage que le calcul et la mémoire vérifiés auraient laissé passer. La validation revient à 15 minutes de banc, puis calcul et mémoire vérifiés
- Le banc d'essai Ultra donne exactement la même image qu'avant : les mesures et les réglages déjà validés restent comparables, rien n'est à refaire

Rappel de la 0.20.0 : le réglage GPU guidé ne garde un réglage que s'il tient aussi sous le calcul pur et la mémoire vidéo (contrôles de stabilité à chaque palier, après 17 130 résultats faux en une minute sur la tour de l'auteur avec un réglage pourtant stable au banc 3D). Équilibre fait tout le parcours (cœur, mémoire, validation de 15 minutes) ; mesures protégées (premier plan, mémoire vidéo occupée) ; banc d'essai seul, sans mesure en jeu ; Économie baisse aussi les watts ; profils GPU ; rapport de chaque passage.

Rappel de la 0.19.x : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages, diagnostic de Mon PC corrigé après les retours des premiers testeurs, conseil RAM pour 4 barrettes DDR5, tests automatiques.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, contrôles de stabilité, validation de 15 minutes, profils, rapport de fin de mode, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
