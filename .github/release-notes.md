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

- **Sécurité, à installer** : les tâches de démarrage « Profil GPU » et « Limite de charge » tournent en administrateur sans fenêtre UAC, mais lançaient l'exe là où tu l'avais dézippé (Bureau, Téléchargements…), un dossier modifiable sans droits : un programme lancé sans droits pouvait le remplacer et obtenir les droits administrateur à l'ouverture de session suivante. Elles lancent désormais une copie rangée dans `C:\Program Files\Aria Cockpit`, où seuls les administrateurs écrivent ; au premier lancement de la 0.24, les tâches existantes sont repointées vers elle. Le composant NVIDIA `nvapi64.dll` n'est plus chargé que depuis le dossier système de Windows
- **Ce que contient chaque mise à jour Windows** : sous chaque ligne, la description de Microsoft, le nombre de failles de sécurité corrigées (CVE) et un lien vers l'article KB ou la page d'information de Microsoft
- **Notes de version des applications** : après la recherche, Aria lit en arrière-plan les notes de version de chaque application à mettre à jour (`winget show`) et affiche un lien « Notes de version de l'éditeur », avec un aperçu du texte quand le paquet le contient. Tous les paquets n'en fournissent pas
- **Historique de Windows Update** : les 40 dernières opérations, y compris ce que Windows fait seul en arrière-plan, avec leur résultat, la mention « depuis Aria Cockpit » pour celles lancées depuis Aria et un lien « En savoir plus ». Les définitions Defender, installées plusieurs fois par jour, sont regroupées ; les mises à jour du Microsoft Store ont un nom lisible
- **Rapport de mises à jour** plus complet : pour chaque mise à jour installée, les failles corrigées et le lien vers l'article ou les notes de version
- Seules les adresses web (http, https) sont ouvertes, dans le navigateur et sans les droits administrateur : un chemin local glissé dans un paquet winget est ignoré

Rappel de la 0.23.0 : fonctions reprises de CTI-Cockpit (menu des catégories dans Réglages et Restauration, pare-feu et dernier correctif dans le diagnostic, vrai nom des programmes au démarrage, carte Batterie, une seule fenêtre à la fois, ménage automatique du dossier des données) et limite de charge de la batterie des portables (Dell et HP ; ASUS et Lenovo grand public en expérimental).

Rappel de la 0.22.0 : courbe de ventilation dans Réglage GPU (préréglages, courbe personnalisée, « Mes courbes », sécurités à 85 °C, retour au pilote à la fermeture) ; toutes les données dans ton profil, `C:\Users\<toi>\.Aria-Cockpit`, modifiables par les seuls administrateurs.

Rappel de la 0.21.0 : le préréglage Ultra RT et le contrôle en ray tracing de la validation ont été retirés (ils n'aidaient pas à trouver un meilleur réglage) ; le banc Ultra donne exactement la même image qu'avant.

Rappel de la 0.20.0 : le réglage GPU guidé ne garde un réglage que s'il tient aussi sous le calcul pur et la mémoire vidéo (contrôles de stabilité à chaque palier, après 17 130 résultats faux en une minute sur la tour de l'auteur avec un réglage pourtant stable au banc 3D). Équilibre fait tout le parcours (cœur, mémoire, validation de 15 minutes) ; mesures protégées (premier plan, mémoire vidéo occupée) ; banc d'essai seul, sans mesure en jeu ; Économie baisse aussi les watts ; profils GPU ; rapport de chaque passage.

Rappel de la 0.19.x : origine aux valeurs d'usine, contrôle de l'overclock de NVIDIA App, Performance avec l'avis d'Aria, retour à Équilibre en un clic, journal des réglages, diagnostic de Mon PC corrigé après les retours des premiers testeurs, conseil RAM pour 4 barrettes DDR5, tests automatiques.

## Contenu

- **Mon PC** : diagnostic matériel, télémétrie GPU en direct, analyse de performance en jeu, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel), confidentialité, programmes au démarrage, applications préinstallées
- **Réglage GPU** : limite de puissance, plafond et décalages de fréquence (NVML officiel), assistant guidé Équilibre → Économie ou Performance, contrôles de stabilité, courbe de ventilation, validation de 15 minutes, profils, rapport de fin de mode, garde-fou anti-plantage
- **Banc d'essai Aria** : scène 3D Direct3D 12 en 1080p, 1440p ou 4K, préréglages Faible à Ultra, FPS mesurés, images de contrôle vérifiées (une erreur de calcul trahit un réglage instable) ; l'assistant s'en sert pour un verdict automatique
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS, et vérification des nouvelles versions d'Aria Cockpit
- **Nettoyage**, **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
