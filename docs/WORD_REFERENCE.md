# Référence Word et adaptations Nexolume

## Éléments observés

Cible demandée : `C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE`.

- REA `inspect_pe_resources` : PE32+, SHA-256 `a02ec8e28e1210a13f1c8d1874c9f10d6ed987a18365cbaae72d8403ba2d906d`, 223 ressources et 453 entrées examinées. Evidence : `ev_89f0d36764be20dc8a2daba54e59cbd6032226d842fbb9e7c44f8ad3457af4ae`.
- Cet inventaire est statique. Il établit l’identité de l’exécutable et ses ressources, pas la disposition des commandes ou leur exécution.
- Une instance Word COM dédiée, invisible, a été créée, interrogée et fermée sans sauvegarde. Version observée : 16.0, build 16.0.20430. Résultat local : `artifacts/reference/word-automation.json`.
- Document vierge observé : Aptos 12 points, page 595,3 × 841,9 points, marges d’environ 70,85 points, espacement après de 8 points. Le réglage d’interligne observé était 13,9 points.
- Les libellés Coller, Couper, Copier, Gras, Italique, Souligner, Couleur de police, Surlignage, Puces, Numérotation, Retraits, Rechercher et Remplacer ont été exposés par cette installation via CommandBars. Certaines autres requêtes d’identifiants Office n’étaient pas exposées : elles ne constituent pas une preuve d’absence de ces fonctions dans Word.

## Choix de Nexolume

Les groupes du ruban reprennent les repères de la capture Word fournie par l’utilisateur. Leur disposition précise est un choix Nexolume, pas une mesure tirée du binaire. L’identité ardoise / bleue est conservée ; le ruban s’adapte aux petites fenêtres en regroupant la galerie de styles dans un menu.

Le document utilise désormais Aptos 12 points par défaut, avec une conversion explicite entre points affichés et pixels logiques Avalonia. Les marges de rendu sont proches de 2,5 cm. Le ruban est masquable, les commandes sont espacées et les champs disposent de noms compréhensibles.

Les fonctionnalités ajoutées sont des implémentations indépendantes dans les moteurs Rust et l’interface Avalonia. Les styles Writer portent encore sur le paragraphe entier. Les images, tableaux, annotations, suivi des modifications, pagination et formats Microsoft Office restent des travaux futurs.
