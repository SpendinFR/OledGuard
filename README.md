# OledGuard

OledGuard est un prototype Windows destiné à réduire l’exposition prolongée des zones statiques sur un écran OLED.

## Principe

- L’écran est découpé en zones de 64 × 64 pixels par défaut.
- Une capture fortement réduite est comparée à basse fréquence.
- Une zone qui ne change pas pendant 30 secondes devient progressivement noire.
- Dès que son contenu change, elle redevient visible immédiatement.
- Lorsque la souris approche, la zone réapparaît avec un fondu court et reste visible tant que le pointeur est présent.
- Après le départ de la souris, elle attend un court instant puis revient progressivement au noir si elle est toujours statique.

Le compteur repart de zéro à chaque changement. Une application comme Codex qui affiche du contenu par intermittence reste donc visible tant qu’elle produit au moins un changement avant l’expiration du délai.

## Commandes

- Double-clic sur l’icône près de l’horloge : activer ou désactiver.
- `Ctrl + Alt + O` : activer ou désactiver globalement.
- `Ctrl + Alt + R` : révéler tout l’écran pendant 10 secondes.
- Clic droit sur l’icône : délai 5, 15, 30 ou 60 secondes, paramètres et fermeture.

Le réglage conseillé est **30 secondes**. Cinq secondes est volontairement disponible comme mode très agressif, mais provoquera beaucoup plus de fondus pendant la lecture.

## Optimisation

Le prototype évite la capture vidéo continue :

- aucune image 4K n’est stockée dans l’historique ;
- la capture est immédiatement réduite à quelques centaines de pixels de côté ;
- un seul tampon de capture et un seul tampon précédent sont réutilisés ;
- la vérification se fait toutes les 1,5 secondes tant que tout est visible ;
- lorsqu’une zone est noire, elle est vérifiée toutes les 0,5 seconde afin de révéler rapidement un changement ;
- l’overlay est dessiné par la composition Windows et ne reçoit ni focus ni clic ;
- l’overlay est exclu de la capture pour que l’application continue à voir le contenu situé dessous.

La consommation exacte dépend du pilote, du nombre d’écrans, de leur résolution et du comportement de WPF. Cette version vise une empreinte faible, mais elle doit être mesurée sur la machine réelle avec le Gestionnaire des tâches et un outil de suivi GPU.

## Configuration requise

- Windows 10 version 2004 ou plus récent, ou Windows 11.
- PC x64.
- Pour compiler : SDK .NET 8 x64.

`WDA_EXCLUDEFROMCAPTURE`, utilisé pour exclure le masque de l’analyse, est officiellement pris en charge à partir de Windows 10 version 2004.

## Compiler

1. Installer le SDK .NET 8 x64.
2. Extraire le dossier OledGuard.
3. Double-cliquer sur `build.cmd` à la racine.
4. L’exécutable autonome sera créé dans `dist/OledGuard/OledGuard.exe`.

Commande équivalente :

```powershell
dotnet publish .\src\OledGuard\OledGuard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o .\dist\OledGuard
```

## Réglages par défaut

| Réglage | Valeur |
|---|---:|
| Délai statique | 30 s |
| Taille de zone | 64 px |
| Fondu vers visible | 140 ms |
| Fondu vers noir | 900 ms |
| Rayon souris | 180 px |
| Maintien après souris | 850 ms |
| Vérification normale | 1 500 ms |
| Vérification avec zones masquées | 500 ms |

Les paramètres sont sauvegardés dans :

```text
%LOCALAPPDATA%\OledGuard\settings.json
```

## Limites du prototype

- Il n’a pas encore été validé sur toutes les configurations HDR, multi-écrans et mise à l’échelle DPI.
- Certains jeux, logiciels anti-triche, écrans sécurisés ou contenus protégés peuvent empêcher l’overlay ou la capture.
- Le masque est construit sur une grille ; les limites peuvent être légèrement visibles selon la taille choisie.
- Le logiciel réduit le risque d’exposition statique mais ne garantit pas l’absence de marquage permanent.
- Il faut conserver les protections matérielles de l’écran : déplacement de pixels, entretien de dalle et extinction automatique.

## Sécurité d’utilisation

Avant un usage quotidien prolongé :

1. Tester pendant quelques minutes avec le délai à 5 secondes.
2. Vérifier que toute zone modifiée réapparaît correctement.
3. Tester `Ctrl + Alt + O` et le menu de l’icône.
4. Revenir ensuite au délai de 30 secondes.
5. Garder l’extinction automatique de l’écran activée dans Windows.

## Structure

- `ScreenSampler.cs` : capture GDI fortement réduite, avec buffers réutilisés.
- `MonitorSession.cs` : détection, temporisation, révélation et animation.
- `OverlayWindow.cs` / `MaskSurface.cs` : overlay noir transparent et traversable.
- `ProtectionController.cs` : activation, multi-écrans et reconfiguration.
- `TrayService.cs` / `HotkeyHost.cs` : commandes quotidiennes.

## Compilation avec GitHub Actions

Le dépôt contient aussi `.github/workflows/build-windows.yml`. Après l’avoir placé dans un dépôt GitHub, lancer manuellement le workflow **Build Windows executable** produit une archive `OledGuard-win-x64` sans installer le SDK localement.
