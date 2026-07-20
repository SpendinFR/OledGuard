# OledGuard 0.7.0

OledGuard protège un écran OLED sous Windows en rendant noir profond ce qui n’est plus utilisé, tout en conservant visibles les zones réellement actives.

## Comportement

Cette version repart du moteur 0.4 et conserve son principe de zone active, avec un rendu plus net :

- petites zones carrées de 24 px par défaut ;
- centre visible, puis plusieurs niveaux nets de sombre jusqu’au noir profond ;
- aucun flou bilinéaire ni grand halo rond ;
- distance carrée autour des activités ;
- passage de la souris maintenu visible 30 secondes ;
- retour temporel progressif au noir ;
- une modification réelle réaffiche immédiatement la zone ;
- les micro-animations isolées, comme un seul point qui clignote, sont ignorées par défaut.

## Réglages conseillés

- durée visible après activité : **30 s** ;
- taille des carrés : **24 px** ;
- filtre micro-animation : **2 cellules voisines minimum** ;
- zone nette : **24 px** ;
- transition carrée : **72 px** ;
- carré révélé autour de la souris : **72 px** ;
- retour au noir : **2,6 s**.

Le filtre micro-animation peut être réglé à 1 pour afficher les changements minuscules, ou augmenté à 3/4 si une interface contient beaucoup de petits indicateurs clignotants.

## Commandes

- double-clic sur l’icône : activer ou désactiver ;
- `Ctrl + Alt + O` : activer ou désactiver (`o` et `O` sont identiques) ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- clic droit sur l’icône : délai, paramètres et fermeture.

## Consommation

Pour un écran 4K avec des cellules de 24 px :

- environ 160 × 90 cellules ;
- quatre échantillons par axe et par cellule ;
- aucun historique vidéo ;
- buffers réutilisés ;
- masque alpha minuscule agrandi par le compositeur Windows ;
- détection généralement toutes les 250 ms lorsque le masque est actif.

La mémoire des cartes de travail reste très faible. OledGuard ne conserve pas de capture 4K complète.

## Compilation avec GitHub Actions

Chaque push sur `main` lance **Build Windows executable**. Quand le workflow est vert, télécharger l’artifact `OledGuard-win-x64`, le décompresser puis lancer `OledGuard.exe`.

## Limite

OledGuard réduit l’exposition des zones statiques mais ne garantit pas l’absence totale de marquage. Conserver les protections intégrées de la dalle, une luminosité raisonnable et l’extinction automatique de l’écran.
