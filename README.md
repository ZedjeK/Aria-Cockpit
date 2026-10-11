<p align="center"><img src="docs/aria-icon.png" width="128" alt="Logo d'Aria : un A néon glitché traversé d'une onde orange"></p>

<h1 align="center">Aria · PC-Tune</h1>

Outil Windows 11 qui analyse le matériel du PC et propose les réglages Windows, NVIDIA et confidentialité adaptés,
installe les mises à jour et fait le ménage, avec une interface « cockpit HUD » aux couleurs du logo : cyan glacé, bleu roi et orange.

**Rien n'est modifié sans votre accord.** Chaque changement apparaît dans une liste à cocher avec sa valeur actuelle,
la valeur proposée et la raison. Avant toute modification, Aria sauvegarde les valeurs d'origine et crée un point
de restauration Windows : tout se restaure en quelques clics. Le BIOS n'est jamais modifié, seulement lu et expliqué.

## Téléchargement

Dernière version : [page des releases](../../releases/latest).

| Fichier | Pour qui |
|---|---|
| `Aria-<version>-autonome.zip` | Tout le monde : rien à installer (≈ 140 Mo une fois décompressé) |
| `Aria-<version>-leger.zip` | PC qui ont déjà le [runtime .NET 10 Desktop](https://dotnet.microsoft.com/download/dotnet/10.0) (≈ 8 Mo) |

Décompresser, puis lancer `Aria.exe`.

- **Windows 11** requis. Aria demande les **droits administrateur** : elle en a besoin pour lire et modifier les
  réglages du système. Lancez-la depuis votre propre session (si un autre compte administrateur valide l'élévation,
  les réglages personnels comme la souris ou le Mode Jeu s'appliqueraient à ce compte-là).
- L'exécutable **n'est pas signé** : au premier lancement, Windows SmartScreen peut afficher « Windows a protégé votre
  ordinateur ». Cliquez sur « Informations complémentaires » puis « Exécuter quand même ». Le code source complet est
  dans ce dépôt.

## Fonctionnalités

| Page | Ce qu'elle fait |
|---|---|
| **Mon PC** | Matériel détecté, diagnostic avec score de conformité, guide BIOS selon la marque de la carte mère, export de la fiche PC en HTML |
| **Réglages** | Trois groupes (Windows, NVIDIA, Confidentialité) : valeur actuelle → valeur proposée, liste à cocher, application puis résultat |
| **Mises à jour** | Windows Update, applications (winget), pilote NVIDIA et BIOS, avec la même liste à cocher |
| **Nettoyage** | Espace récupérable par élément, suppression après confirmation |
| **Restauration** | Choix d'une sauvegarde, puis retour aux valeurs enregistrées (lui-même annulable) |
| **Rapports** | Tous les rapports (réglages, restaurations, mises à jour, nettoyages, fiches PC) avec leur bilan et un aperçu |

On passe d'une page à l'autre par les onglets de la barre du haut. Mon PC prend la forme d'un cockpit : nom du PC
et jauge de conformité au centre, composants dans des cadres de viseur de part et d'autre, résumé du diagnostic en
dessous. Code couleur : cyan glacé pour ce qui est conforme, bleu roi pour l'information, orange pour ce qui est à
corriger et pour les boutons d'action, rouge pour un échec.

Les confirmations s'affichent dans la fenêtre d'Aria et récapitulent chaque changement avant de l'appliquer.

### Diagnostic (lecture seule)

Branchement des écrans (carte graphique ou carte mère), câble et fréquence, HDR, lien PCIe et Resizable BAR de la
carte NVIDIA, mémoire (canal, vitesse, profil XMP/EXPO), P-cores et E-cores, microcode Intel 13e et 14e génération,
mode de démarrage, Secure Boot et TPM, âge du BIOS et du pilote graphique, santé et espace libre des disques,
réseau, applications au démarrage, protection du système, redémarrage en attente, batterie, G-SYNC (disponible mais
désactivé) et conseil DLSS selon la génération de la carte.

Les corrections qui se font dans le BIOS sont expliquées pas à pas pour ASUS, MSI, Gigabyte et ASRock (plus un guide
générique), avec un bouton pour redémarrer directement dans le BIOS.

## Réglages

### Windows

Fréquence maximale de chaque écran, plan d'alimentation adapté (Performances élevées avec état minimal du processeur
à 20 % sur un PC fixe), Mode Jeu, planification GPU accélérée, optimisations des jeux en mode fenêtré, taux de
rafraîchissement variable, enregistrement en arrière-plan de la Xbox Game Bar, accélération de la souris et, en option
de confort, animations de Windows.

### NVIDIA

Aria parle au pilote via NVAPI (`nvapi64.dll`, installé avec le pilote), avec les seuls identifiants publiés par
NVIDIA dans le [SDK NVAPI](https://github.com/NVIDIA/nvapi) (licence MIT). Les réglages modifiés sont ceux du profil
global (« Paramètres globaux » de l'application NVIDIA) ; les profils propres à chaque jeu restent prioritaires.

| Réglage | Proposé | Condition |
|---|---|---|
| Mode faible latence | Activé (une image en attente) | toujours |
| Synchronisation verticale | Activée par le pilote | G-SYNC actif sur l'écran principal |
| Nombre d'images par seconde maximal | Fréquence max − 3 (ex. 141 à 144 Hz) | G-SYNC actif sur l'écran principal |
| Fréquence préférée | La plus élevée disponible | toujours |
| Taille du cache des shaders | Illimité | toujours |
| Gestion de l'alimentation | Retour à « Normal » | seulement si « Performances maximales » |
| Processeur graphique préféré | Processeur NVIDIA (décoché, autonomie réduite) | portable à deux puces graphiques |
| Plage dynamique (par écran) | Complète (0-255) | écran NVIDIA en plage limitée, RGB, sans HDR |

Un réglage jamais modifié s'affiche « (par défaut) » ; le restaurer remet la valeur d'usine du pilote. Aria ne peut
pas activer G-SYNC lui-même (absent de l'API officielle) : le diagnostic indique où le faire. Fermez le Panneau de
configuration NVIDIA avant d'appliquer : un clic sur « Appliquer » de sa part pourrait remettre ses anciennes valeurs.

### Confidentialité

Réglages officiels (Policy CSP Microsoft, valables sur Windows 11 Pro) ou options écrites par les Paramètres eux-mêmes.
Couper des services n'apporte aucun gain de performances mesurable : l'intérêt est la vie privée et la réduction de la
surface d'attaque.

- **Cochés** :
  - données de diagnostic au minimum (niveau « Obligatoire » ; le niveau zéro est réservé à Entreprise et Éducation) ;
  - demandes de commentaires, expériences personnalisées, identifiant de publicité, historique d'activité ;
  - recherche Bing du menu Démarrer (effet secondaire : plus de recherches récentes dans l'Explorateur) ;
  - recommandations du menu Démarrer, suggestions et astuces (Paramètres, écran de verrouillage) ;
  - suivi des applications lancées, liste de langues envoyée aux sites ;
  - services Télémétrie (DiagTrack) et Fax ;
  - spouleur d'impression **seulement si aucune imprimante réelle** (« Imprimer en PDF » ne fonctionnera plus).
- **Décochés, au choix** :
  - application Copilot (désinstallée pour le compte ; la stratégie « Désactiver Copilot » ne concerne plus la nouvelle
    application ; la restauration ouvre le Microsoft Store) ;
  - services Xbox Live (seulement sans application Xbox ni Game Pass) ;
  - géolocalisation (PC fixe).
- **Jamais touchés** : Windows Search, SysMain, Windows Update, Defender, BITS, audio, Bluetooth, réseau, service des
  accessoires Xbox (manettes).
- **Widgets** : non gérés. Leur stratégie officielle (`HKLM\SOFTWARE\Policies\Microsoft\Dsh`) est verrouillée par Windows,
  même pour un administrateur. Pour masquer le bouton : Paramètres → Personnalisation → Barre des tâches → Widgets.

## Mises à jour

- **Windows Update** : interface officielle de Windows. Cochées d'office : sécurité, correctifs, antivirus.
  Jamais cochés d'office : pilotes proposés par Windows Update, nouvelles versions de Windows, préversions.
- **Applications** : winget (gestionnaire officiel de Windows). Une ligne mal lue n'est jamais proposée.
- **Pilote NVIDIA** : comparaison avec le dernier pilote Game Ready, via le service de recherche du site
  NVIDIA (non officiel). Téléchargement par le navigateur, l'installation reste celle de NVIDIA.
- **BIOS** : version installée et lien de recherche vers la page support ; jamais mis à jour par Aria.

Un point de restauration Windows est créé avant chaque installation (Aria ne désinstalle pas les mises à jour).
Les applications à mettre à jour qui sont ouvertes sont listées avec un compte à rebours de 20 s, puis fermées
normalement (fermeture forcée au bout de 10 s) ; on peut aussi les fermer tout de suite, les exclure ou annuler.
Le téléchargement des applications s'affiche en direct (« Téléchargement · 45 % · 44 Mo / 98 Mo »).

## Nettoyage

| Élément | Coché | Méthode |
|---|---|---|
| Fichiers temporaires (utilisateur et Windows) | oui | fichiers de plus de 7 jours ; fichiers en cours d'utilisation ignorés |
| Cache d'Optimisation de la distribution | oui | `Delete-DeliveryOptimizationCache` (commande officielle) |
| Anciens composants des mises à jour | oui | `DISM /Online /Cleanup-Image /StartComponentCleanup`, sans `/ResetBase` |
| Caches de shaders (NVIDIA, DirectX, AMD, Intel) | non | à vider en cas de saccades après une mise à jour du pilote |
| Rapports de plantage (minidumps) | non | utiles pour diagnostiquer un écran bleu |
| Ancienne installation de Windows (`Windows.old`) | non | Nettoyage de disque (`cleanmgr /sagerun`) ; plus de retour à la version précédente |

Garde-fous : seuls ces emplacements sont touchés ; aucun lien (jonction, lien symbolique) n'est suivi ; une racine
dangereuse (racine du disque, dossier Windows, profil, Program Files…) est refusée ; le dossier `TEMP` n'est accepté que
s'il s'appelle bien « Temp ». La corbeille et les documents ne sont jamais touchés. Une suppression est définitive.

## Fiche PC (HTML)

Boutons « Exporter en HTML » (Mon PC) et « Exporter la fiche PC » (Rapports) : un seul fichier autonome (matériel,
diagnostic, réglages, dernières actions), thème néon à l'écran et version claire à l'impression ou en PDF. La case
« Masquer les infos personnelles », cochée par défaut, remplace le nom du PC et de l'utilisateur, y compris dans les
chemins.

## Sauvegardes et rapports

- Sauvegardes des valeurs d'origine : `C:\ProgramData\Aria\Sauvegardes`
- Rapports : `C:\ProgramData\Aria\Rapports`
- Journal des erreurs : `C:\ProgramData\Aria\erreurs.log`

Les versions 0.12 et antérieures (sous le nom PC-Tune) utilisaient `C:\ProgramData\PCTune` : ce dossier est renommé
automatiquement au premier lancement, les sauvegardes restent donc restaurables.

Une restauration suit le même parcours qu'une application (nouvelle sauvegarde, point de restauration, vérification,
rapport) : elle peut donc elle-même être annulée.

## Version console

    Aria.Cli                        Diagnostic, état actuel, liste à cocher, application des choix validés
    Aria.Cli --simulation           Affiche tout ce qui serait proposé, sans rien modifier
    Aria.Cli --restaurer [fichier]  Revient aux valeurs d'une sauvegarde (même liste à cocher)
    Aria.Cli --aide                 Aide

## Compiler soi-même

Prérequis :

- Windows 11
- [SDK .NET 10](https://dotnet.microsoft.com/download)
- Pour un éditeur : Visual Studio 2026 (version 18) ou plus récent, ou VS Code avec l'extension C# Dev Kit.
  Visual Studio 2022 ne sait pas cibler .NET 10.

Le plus simple : double-cliquer sur **`Lancer Aria.cmd`**. Le script demande les droits administrateur, installe le
SDK .NET 10 via winget s'il manque, compile `publish\Aria.exe`, puis ouvre l'interface.

À la main, dans un terminal **administrateur** :

    dotnet run --project src/Aria.Gui                # lancer
    dotnet publish src/Aria.Gui -c Release           # exécutable unique autonome
    dotnet publish src/Aria.Gui -c Release -p:SelfContained=false   # version légère (runtime .NET 10 requis)

Résultat : `src/Aria.Gui/bin/Release/net10.0-windows/win-x64/publish/Aria.exe`

Version console en simulation, depuis la racine du projet :

    powershell -ExecutionPolicy Bypass -File outils\Lancer-Aria.ps1 -Console -Simulation

## Structure du projet

- `src/Aria.Core` : moteur (détection, diagnostic, réglages, NVIDIA, confidentialité, mises à jour, nettoyage,
  sauvegarde, rapports), sans interface
- `src/Aria.Gui` : interface Windows 11 (WPF + WPF-UI) → `Aria.exe`
- `src/Aria.Cli` : version console → `Aria.Cli.exe`
- `config/bios/*.json` : base de connaissances BIOS par marque (où trouver chaque option)
- `src/Aria.Gui/Assets/aria-icon.svg` : source du logo ; `Aria.ico` en est le rendu de 16 à 256 px

Les fichiers BIOS sont intégrés à l'exécutable. Pour corriger un chemin sans recompiler : créer un dossier `bios` à
côté d'`Aria.exe` et y placer le fichier modifié (même nom) ; il remplace alors la version intégrée.

## Crédits

- Développé par Jérémy Zemani. Anciennement PC-Tune (jusqu'à la version 0.12).
- Interface : [WPF-UI](https://github.com/lepoco/wpfui) (MIT) et CommunityToolkit.Mvvm (MIT).
- Identifiants des fonctions et réglages NVIDIA : [SDK NVAPI](https://github.com/NVIDIA/nvapi) (MIT).
- Table des modèles de cartes NVIDIA : [ZenitH-AT/nvidia-data](https://github.com/ZenitH-AT/nvidia-data)
  (domaine public). Copie intégrée dans `src/Aria.Core/Updates/nvidia-gpu-data.json`, mise à jour en ligne si possible.
- Recommandations G-SYNC : [Blur Busters, G-SYNC 101](https://www.blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/).
- Réglages de confidentialité : documentation Microsoft (Policy CSP et « Manage connections from Windows operating
  system components to Microsoft services »).
