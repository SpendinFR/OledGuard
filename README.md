# OledGuard V1

OledGuard V1 garde l’écran noir profond par défaut et révèle uniquement ce qui est utile : la zone réellement active de la fenêtre au premier plan, ainsi qu’un petit carré autour de la souris.

## Comportement

- Quand une fenêtre passe au premier plan, elle est brièvement révélée.
- Ensuite, seules les zones qui changent réellement restent visibles.
- Les parties ultra-statiques de cette fenêtre retournent progressivement au noir après le délai choisi.
- Les changements de fond, derrière la fenêtre active, restent noirs.
- La souris peut découvrir n’importe quelle zone noire sans déplacer le focus.
- Les micro-animations isolées, comme un petit point clignotant, sont ignorées par défaut.
- Un balayage noir périodique traverse les zones encore visibles afin d’offrir une courte pause aux pixels qui restent actifs en continu.

Le dégradé est monochrome et carré : visible, sombre, très sombre, noir. Il n’ajoute aucune couleur ni luminosité.

## Réglages conseillés

- noir après activité : **30 s** ;
- révélation d’une nouvelle fenêtre : **5 s** ;
- précision : **24 px** ;
- filtre micro-animation : **2 cellules** ;
- centre visible : **1 cellule** ;
- dégradé : **5 cellules / 7 niveaux** ;
- souris : **48 px + 4 cellules de dégradé** ;
- maintien souris : **30 s** ;
- balayage de repos : **toutes les 120 s, pendant 7 s**.

## Raccourcis

- `Ctrl + Alt + O` : activer ou désactiver ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- double-clic sur l’icône : activer ou désactiver ;
- clic droit sur l’icône : paramètres et fermeture.

## Mémoire et performances

OledGuard ne conserve aucune image 4K complète :

- capture réduite à quelques centaines de pixels ;
- une seule image précédente réduite ;
- carte alpha de quelques milliers de cellules ;
- aucun historique vidéo ;
- buffers réutilisés ;
- rendu mis à jour uniquement lorsqu’un fondu, une activité ou un balayage le nécessite.

À 3840 × 2160 avec des cellules de 24 px, les données propres à OledGuard représentent seulement quelques mégaoctets. Le compositeur Windows réserve également une surface transparente plein écran ; la cible est **moins de 100 Mo pour un écran 4K**, mais la valeur exacte dépend du pilote, de Windows et du nombre de moniteurs.

## Compilation

Chaque push sur `main` lance le workflow **Build Windows executable**. Quand il est vert, télécharger l’artifact `OledGuard-win-x64`, le décompresser et lancer `OledGuard.exe`.

## Important

Le « balayage de repos » ne répare pas physiquement une dalle. Il réduit temporairement l’émission lumineuse de pixels actifs. OledGuard complète les protections de l’écran ; il ne garantit pas l’absence totale de marquage.
