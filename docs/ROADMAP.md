# État du prototype

## Validé automatiquement

- Compilation du workspace Rust et de la solution .NET.
- UTF-8 aller-retour par P/Invoke, 100 cycles de documents et 100 cycles d’instances / buffers natifs.
- Commandes, révisions, transactions avec rollback, historique central et deltas sans snapshot après mutation.
- Writer : saisie Avalonia, styles de paragraphe et de sélection, listes, plan, statistiques, concentration, autocorrection, vérification orthographique Windows, dictionnaire personnel, recherche/remplacement avancés, Undo/Redo, écriture et réouverture, réglages papier/marges, sauts explicites, aperçu paginé et export PDF.
- Sheets : saisie Avalonia, 10 + 20 = 30, modification de source et recalcul, Undo/Redo, sauvegarde et réouverture.
- Slides : formes (rectangle, arrondi, ellipse, triangle, losange, ligne), texte italique/souligné/aligné, rotation, opacité, contour, fond, transition, notes du présentateur, diaporama avec fondu/glissement, Undo/Redo et réouverture.
- Shell : onglets, titres de style Writer, zoom, thèmes, fermeture d’un document enregistré.
- Démarrage réel via le backend Windows Avalonia, trois éditeurs et sauvegarde / fermeture en mode masqué.

Les captures de rendu Skia sont produites par `tests/DotNet`. Le test Windows masqué est distinct de ces captures. Les dialogues de sélection de fichiers utilisent StorageProvider et ne sont pas automatisés de bout en bout sur le bureau de l’utilisateur.

## Prochaines étapes

1. Writer : ~~sélection riche sur plusieurs paragraphes~~ (fait : Ctrl+A, Maj+flèches, souris, copier/couper/coller, mise en forme, suppression en une étape d’annulation), formats de presse-papiers et style de frappe au curseur.
2. Writer : pagination dans la surface éditable, édition des sections et en-têtes/pieds de page. L’aperçu et le PDF disposent déjà d’une pagination par lignes.
3. Writer : tableaux et images éditables, notes, sommaire, commentaires et suivi des modifications.
4. Writer : validation du pilote d’impression (Spouleur arrêté et inaccessible dans cet environnement), récupération après crash et correction grammaticale.
5. Sheets : édition directement dans la cellule, plusieurs feuilles et graphe de dépendances persistant pour grands volumes, avec mesures.
6. Slides : images, animations par objet, formats PPTX/ODP.

DOCX/ODT sont pris en charge pour Writer avec les limites documentées dans OFFICE_FORMATS.md. Les objets avancés DOCX sont conservés dans le paquet, mais ne sont pas tous affichés ni éditables. XLSX, PPTX et import PDF ne sont pas pris en charge. Export PDF disponible pour Writer. Pas d’installateur ni d’associations de fichiers système dans le prototype portable.

## Validation des outils bureautiques

Les scénarios automatisés couvrent aussi le fractionnement Unicode et la fusion des paragraphes au clavier, listes, couleurs et interlignes, recherche/remplacement puis annulation, formats des cellules et agrégats, duplication avec identifiants stables après Undo/Redo, calques, centrage, ordre des diapositives et navigation/quitter du diaporama. Les anciens fichiers de schéma 1 sont testés à l’ouverture et migrés en schéma 2.
