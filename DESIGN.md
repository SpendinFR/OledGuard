# Architecture V1.1

## 1. Géométrie de la fenêtre active

`GetForegroundWindow` identifie la fenêtre au premier plan. `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` fournit ses limites visibles. La fenêtre propriétaire est réunie avec une boîte de dialogue éventuelle. Les fenêtres du bureau et de la barre des tâches sont exclues.

Chaque moniteur calcule l'intersection entre cette géométrie et sa propre surface. En dehors de cette intersection, l'alpha cible est noir.

## 2. Analyse statique

La capture GDI est immédiatement réduite. Les cellules de 32 px sont regroupées par blocs de 2 × 2, donc une zone statique représente environ 64 × 64 px. Une modification significative remet à zéro l'âge du bloc entier. Les changements isolés sans voisin sont ignorés pour éviter qu'un curseur clignotant maintienne une grande zone visible.

Après le délai de grâce, l'opacité du bloc évolue avec une courbe smoothstep jusqu'à l'opacité statique maximale.

## 3. Révélation de la souris

Le pointeur applique des tampons circulaires sur la petite grille. Le centre devient totalement transparent et le bord utilise un dégradé. Chaque partie du chemin conserve sa propre expiration, ce qui crée une traînée naturelle sans calculer une texture 4K.

## 4. Balayage de repos

Toutes les 90 secondes par défaut, une bande noire à bord progressif traverse la fenêtre active pendant 7 secondes. Elle est combinée avec le masque existant par maximum d'opacité. Le balayage ne prétend pas régénérer la dalle : il réduit seulement momentanément l'émission des pixels continuellement actifs.

## 5. Mémoire

Le moteur conserve seulement :

- deux images BGRA réduites ;
- une carte de cellules ;
- une carte de zones ;
- une petite carte alpha 2× interpolée par WPF.

Le coût dominant éventuel reste le tampon de composition de la fenêtre transparente, contrôlé par Windows et le pilote.
