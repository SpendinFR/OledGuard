# OledGuard 4 â€” moteur natif Windows

OledGuard dÃ©tecte les Ã©lÃ©ments lumineux qui restent immobiles sur une TV ou un moniteur OLED, puis les assombrit localement sans bloquer la souris : textes blancs, croix de fermeture, icÃ´nes, bordures et grandes zones blanches.

Cette version a Ã©tÃ© rÃ©Ã©crite de zÃ©ro en **C#/.NET natif**. Elle n'utilise pas Python et ne reprend pas l'ancien moteur cumulatif.

## Fonctionnement par dÃ©faut

- choix de l'Ã©cran OLED au premier dÃ©marrage ;
- capture rÃ©duite de l'Ã©cran toutes les 750 ms ;
- dÃ©tection du mouvement pixel par pixel ;
- dÃ©but de l'assombrissement aprÃ¨s 30 secondes d'immobilitÃ© ;
- force progressive jusqu'Ã  60 % maximum ;
- disparition rapide du masque dÃ¨s qu'un Ã©lÃ©ment bouge ;
- petite zone rÃ©vÃ©lÃ©e autour du curseur ;
- overlay transparent aux clics et exclu de la capture pour Ã©viter une boucle visuelle.

Tous les seuils sont modifiables depuis **clic droit sur l'icÃ´ne OledGuard â†’ ParamÃ¨tres**.

## TÃ©lÃ©charger l'exÃ©cutable compilÃ©

1. Ouvrir l'onglet **Actions** du dÃ©pÃ´t.
2. Ouvrir le dernier workflow vert **Build OledGuard native Windows**.
3. TÃ©lÃ©charger l'artifact **OledGuard-native-win-x64**.
4. DÃ©compresser puis lancer `OledGuard.exe`.

L'exÃ©cutable est autonome : aucun Python et aucun .NET Ã  installer.

## Commandes

Depuis l'icÃ´ne prÃ¨s de l'horloge Windows :

- activer ou mettre en pause la protection ;
- rÃ©vÃ©ler tout l'Ã©cran pendant 20 secondes ;
- rÃ©initialiser la dÃ©tection ;
- modifier les paramÃ¨tres ;
- choisir un autre Ã©cran ;
- lancer automatiquement OledGuard avec Windows ;
- quitter complÃ¨tement le programme.

Un double-clic sur l'icÃ´ne active ou met en pause la protection.

## Limites

Le programme complÃ¨te les protections intÃ©grÃ©es de la TV, mais ne garantit pas l'absence totale de marquage. Les jeux en plein Ã©cran exclusif, certains contenus DRM et certaines configurations HDR peuvent empÃªcher Windows d'afficher l'overlay correctement. Le mode fenÃªtrÃ© sans bordure est le plus compatible.