**Aria · PC-Tune** {VERSION} : analyse du matériel, réglages Windows, NVIDIA et confidentialité, mises à jour, nettoyage, restauration et rapports, dans une interface « cockpit HUD » cyan glacé, bleu roi et orange.

**Rien n'est modifié sans votre accord** : chaque changement est proposé dans une liste à cocher, sauvegardé et couvert par un point de restauration Windows.

## Quel fichier choisir ?

| Fichier | Pour qui |
|---|---|
| `Aria-{VERSION}-autonome.zip` | Tout le monde : rien à installer |
| `Aria-{VERSION}-leger.zip` | PC qui ont déjà le [runtime .NET 10 Desktop](https://dotnet.microsoft.com/download/dotnet/10.0) |

Décompresser, puis lancer `Aria.exe` (Windows 11, droits administrateur demandés).

## Premier lancement

L'exécutable n'est pas signé : Windows SmartScreen peut afficher « Windows a protégé votre ordinateur ». Cliquez sur « Informations complémentaires » puis « Exécuter quand même ». Le code source complet est dans ce dépôt.

## Contenu

- **Mon PC** : diagnostic matériel, score de conformité, guide BIOS par marque, fiche PC en HTML
- **Réglages** : Windows, NVIDIA (NVAPI officiel : faible latence, G-SYNC, limite d'images, cache des shaders, plage dynamique…) et Confidentialité
- **Mises à jour** : Windows Update, applications (winget), pilote NVIDIA, BIOS
- **Nettoyage** : temporaires, anciens composants Windows (DISM), caches, Windows.old
- **Restauration** et **Rapports**

Détails dans le [README]({REPO_URL}#readme).
