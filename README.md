# OledGuard 0.4.0

OledGuard protège un écran OLED sous Windows en rendant noir profond ce qui n’est plus utilisé, tout en laissant les zones récemment actives visibles dans un îlot continu et naturel.

## Principe de la version 0.4

Cette version n’affiche plus une mosaïque de cases sombres indépendantes.

- Une modification de contenu ou le passage de la souris crée une **zone active**.
- Le centre de la zone reste entièrement visible.
- Autour, un large dégradé assombrit progressivement l’image jusqu’au noir profond.
- Les activités proches sont automatiquement regroupées pour former une seule zone cohérente.
- Les petits trous à l’intérieur d’une région active sont comblés.
- Après 30 secondes sans nouvelle activité, la région retourne lentement au noir.
- Si le contenu recommence à bouger sous le masque, il réapparaît immédiatement.

## Réglages conseillés

- durée visible après activité : **30 s** ;
- précision de détection : **32 px** ;
- marge entièrement visible : **110 px** ;
- dégradé vers le noir : **220 px** ;
- zone activée par la souris : **120 px** ;
- retour au noir : **5 s**.

La grille de détection n’est pas dessinée directement. Elle est transformée en champ de distance, puis agrandie et interpolée dans une petite texture alpha. Le rendu doit donc apparaître comme un halo continu, pas comme des carrés.

## Commandes

- double-clic sur l’icône : activer ou désactiver ;
- `Ctrl + Alt + O` : activer ou désactiver (`o` ou `O`, c’est identique) ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- clic droit sur l’icône : délai, paramètres et fermeture.

## Consommation

Pour un écran 4K avec une grille de 32 px :

- environ 120 × 68 cellules d’analyse ;
- texture de masque interne d’environ 240 × 136 pixels ;
- aucun historique vidéo ;
- buffers réutilisés ;
- capture réduite, généralement toutes les 250 ms lorsque le masque est actif ;
- calcul du dégradé en temps linéaire sur quelques milliers de cellules.

La mémoire utilisée pour les cartes d’analyse reste de l’ordre de quelques centaines de kilo-octets. Le compositeur Windows peut réserver davantage de mémoire pour la fenêtre transparente, mais OledGuard ne stocke pas d’image 4K complète.

## Compilation avec GitHub Actions

Chaque push sur `main` lance **Build Windows executable**. Quand le workflow est vert, télécharger l’artifact `OledGuard-win-x64`, le décompresser puis lancer `OledGuard.exe`.

## Limite

OledGuard réduit l’exposition des zones statiques mais ne garantit pas l’absence totale de marquage. Conserver aussi les protections intégrées de la dalle, une luminosité raisonnable et l’extinction automatique de l’écran.
