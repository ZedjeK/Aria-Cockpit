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

Le réglage GPU guidé devient beaucoup plus exigeant : un réglage n'est gardé que s'il tient aussi sous le calcul pur, la mémoire vidéo et le ray tracing.

- **Contrôles de stabilité** : un réglage stable au banc 3D peut calculer faux sous une IA locale (sur la tour de l'auteur : 17 130 résultats faux en une minute, aucun aux valeurs d'origine). Chaque palier du cœur commence donc par 1 minute de calcul vérifié, chaque palier mémoire par 2 minutes de test de la mémoire vidéo, et un auto-contrôle de 20 secondes vérifie d'abord que ces tests voient bien les erreurs
- **Équilibre fait tout le parcours** : paliers du cœur, paliers mémoire, puis validation de 15 minutes, sans question entre les étapes (environ 45 minutes sur une RTX 4080 SUPER)
- **Ray tracing** : nouveau préréglage **Ultra RT** dans le banc d'essai (ombres, occlusion ambiante et reflets tracés par DXR 1.1). Sur une carte RTX, la validation ajoute un tour de caméra en Ultra RT, images vérifiées : les cœurs RT gardent la carte collée à son plafond de fréquence, là où un décalage trop poussé lâche. Les mesures restent en Ultra, qui pousse la carte jusqu'à sa limite de puissance
- **Mesures protégées** : le banc garde le premier plan (sinon le pilote le bride à 30 FPS), et l'assistant s'arrête si une IA locale ou un jeu occupe la mémoire vidéo
- **Banc d'essai seul** : la mesure dans un jeu disparaît de l'assistant, au profit du banc, plus fiable. Un Équilibre mesuré en jeu est à refaire pour débloquer Économie et Performance
- **Économie baisse aussi les watts** : après le plafond, la limite de puissance peut descendre à son tour (jusqu'à 60 % de la valeur d'usine), pour garantir une consommation maximale même dans les scènes les plus lourdes
- **Profils GPU** : enregistre un réglage (celui des curseurs ou un résultat de l'assistant) dans « Mes profils », et recharge-le plus tard
- **Rapport de chaque passage** : à la fin d'un mode, Aria écrit sa conclusion détaillée dans la page Rapports, avec le déroulé palier par palier et la raison de chaque verdict

Rappel de la 0.19.x : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages, diagnostic de Mon PC corrigé après les retours des premiers testeurs, conseil RAM pour 4 barrettes DDR5, tests automatiques.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, contrôles de stabilité, validation de 15 minutes, profils, rapport de fin de mode, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra RT (ray tracing), FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
