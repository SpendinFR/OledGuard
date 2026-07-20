# OledGuard V1.1

OledGuard protège un écran OLED pendant l'utilisation quotidienne de Windows.

## Comportement

- La fenêtre réellement au premier plan est détectée avec les limites DWM de Windows.
- Tout ce qui se trouve autour de cette fenêtre devient noir, même avec l'Explorateur de fichiers et les boîtes de dialogue.
- Si une autre fenêtre passe au premier plan, le masque suit immédiatement sa nouvelle position.
- Dans la fenêtre active, l'image est analysée par zones de 64 × 64 px par défaut.
- Une zone qui ne change plus pendant 30 secondes s'assombrit uniformément, puis atteint environ 94 % de noir.
- Les petits curseurs ou points clignotants isolés sont ignorés pour éviter d'ouvrir une grande zone.
- La souris révèle une traînée ronde avec un dégradé doux, y compris dans les parties noires.
- Un balayage sombre traverse occasionnellement la fenêtre active afin d'offrir une courte pause aux pixels qui restent constamment visibles.

Le balayage n'ajoute pas de couleur ni de blanc : il ne fait qu'assombrir temporairement l'image, ce qui est plus cohérent avec l'objectif de protection OLED.

## Raccourcis

- `Ctrl + Alt + O` : activer ou désactiver OledGuard.
- `Ctrl + Alt + R` : révéler tout l'écran pendant 10 secondes.

La touche `O` fonctionne sans distinction entre minuscule et majuscule.

## Consommation

Avec les réglages par défaut sur un écran 4K :

- capture d'analyse proche de 360 × 204 pixels ;
- carte de masque proche de 240 × 136 pixels ;
- quelques tableaux de cellules et de minuteries ;
- aucune image 4K conservée par le moteur d'analyse.

La fenêtre transparente de composition peut utiliser un tampon géré par Windows et le pilote. L'objectif reste inférieur à 100 Mo par écran 4K, mais la valeur exacte dépend du pilote graphique et de la composition du bureau.

## Construction

Le workflow GitHub Actions compile automatiquement un exécutable Windows autonome. Après un build vert, télécharge l'artifact `OledGuard-win-x64`, décompresse-le, puis lance `OledGuard.exe`.
