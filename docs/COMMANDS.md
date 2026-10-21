# Commandes et deltas, protocole 1

```json
{"protocol":1,"document":"opaque-document-id","expected_revision":3,"command":"SetCell","params":{"address":"A1","input":"=10+20"}}
```

Le protocole, le nom de commande, les types de paramètres et les règles propres à l’éditeur sont validés en Rust. Les commandes de mutation exigent la révision courante. `CreateDocument`, `OpenDocument`, `Snapshot` et `CloseDocument` n’exigent pas de révision attendue.

| Commande | Paramètres |
|---|---|
| Ping | aucun |
| CreateDocument | kind : writer / sheets / slides, title |
| OpenDocument | json : contenu du fichier |
| Snapshot / CloseDocument | document dans l’enveloppe |
| SaveDocument | document et expected_revision ; renvoie la sérialisation, C# écrit le fichier |
| RenameDocument | title |
| Undo / Redo | aucun paramètre |
| Batch | commands : tableau de {command, params} ; une révision et une entrée d’historique |
| SetParagraph | id, text et/ou style, runs, image et/ou table facultatifs ; group facultatif pour la frappe |
| InsertParagraph | id, index, text, style facultatif |
| DeleteParagraph | id |
| SetCell | address, input ; input vide efface la cellule, préfixe = pour une formule |
| AddSlide | id, index facultatif ; slide complet réservé notamment à la restauration |
| DeleteSlide | id |
| AddObject | slide, object, index facultatif |
| DeleteObject | slide, id |
| UpdateObject / MoveObject / ResizeObject / UpdateObjectText | slide, id et propriétés à remplacer |
| UpdateSlide | id, puis background (#RRGGBB), notes, transition (none, fade, slide) |

Style Writer : bold, italic, underline, font_family, font_size (6–96), alignment (left / center / right). Objet Slides : id, kind (text / rectangle / rounded / ellipse / triangle / diamond / line), text, x, y, width, height, fill (#RRGGBB), italic, underline, align, line, line_width, rotation (°), opacity (0,05–1). L’ordre dans le tableau est l’ordre de dessin.

```json
{"protocol":1,"ok":true,"document":"id","revision":4,"canUndo":true,"canRedo":false,"changes":[{"kind":"CellChanged","data":{"address":"A1","cell":{"input":"=10+20","display":"30"}}}]}
```

Les changements sont ordonnés. Les deltas disponibles sont DocumentRenamed, PageLayoutChanged, ParagraphChanged / Inserted / Removed, CellChanged / Recalculated, SlideAdded / Removed et ObjectAdded / Removed / Moved / Updated. Un snapshot complet est réservé à la création, l’ouverture ou la resynchronisation.

Une erreur métier renvoie `ok:false`, `error:{code,message}`, la révision inchangée et `changes:[]`. Codes métier : InvalidCommand, RevisionMismatch. Les erreurs de transport utilisent un code C ABI distinct. Les erreurs de formules (#PARSE!, #REF!, #DIV/0!, #CYCLE!, #VALUE!, #NUM!, #DEPTH!, #LIMIT!) sont des résultats de cellule affichables ; la formule saisie est conservée.

## Extensions bureautiques (protocole 1, stockage schéma 2)

Writer : `SplitParagraph` (id, new_id, start, end en unités UTF-16), `MergeParagraph` (id), `ReplaceAll` (find non vide, replacement). Les opérations composées renvoient plusieurs deltas ciblés et s’annulent atomiquement. Les styles ajoutent color, highlight, list, indent, line_spacing et spacing_after.

Writer enrichi : `FormatText` (id, start, end, style partiel de caractères), `ReplaceText` (id, start, end, text), `ReplaceRanges` (id, edits : start/end/text non chevauchants, au plus 100 000). Les bornes sont en UTF-16 et doivent respecter les caractères Unicode. Les champs de style autorisés sont bold, italic, underline, font_family, font_size, color et highlight. `SetParagraph` et `InsertParagraph` acceptent aussi `runs` : plages triées `{start,length,style}` avec un style de caractères complet. Un tableau vide retire les plages explicites. Les styles identiques adjacents sont fusionnés ; la validation limite chaque paragraphe à 20 000 plages. Les remplacements, séparations et fusions adaptent les positions et conservent les styles. L’annulation restaure le texte et toutes ses plages.

Sheets : `SortRange` (range, column = décalage dans la plage, descending, header) réordonne les lignes d’une plage selon les valeurs affichées (nombres puis texte, vides en dernier) ; l’inverse est `RestoreCells` (liste address/cell). Environ 50 fonctions ajoutées : SOMME.SI.ENS, NB.SI.ENS, MOYENNE.SI.ENS, SI.CONDITIONS, SI.NON.DISP, CHOISIR, RECHERCHEH, SOMMEPROD, RANG, ECARTYPE, VAR, PLAFOND, PLANCHER, FACT, COMBIN, PGCD, PPCM, ALEA, TROUVE, CHERCHE, SUBSTITUE, REPT, NOMPROPRE, JOINDRE.TEXTE, DATE, AUJOURDHUI, ANNEE, MOIS, JOUR, JOURSEM, SIN, COS, TAN. Page Writer : `header`, `footer` (jetons {page}, {pages}), `header_align`, `footer_align`.
Sheets : `SetCellStyle` (address, style : bold, format), `RestoreCell` pour les opérations inverses. Formats : general, number, currency, percent. Formules : SUM / SOMME, AVERAGE / MOYENNE, MIN, MAX, COUNT / NB. Les formats changent le display, jamais l’input ni l’évaluation des références.

Slides : `DuplicateSlide` (id), `MoveSlide` (id, index), `DuplicateObject` (slide, id, new_id facultatif), `ReorderObject` (slide, id, index), `AlignObject` (slide, id, alignment : left / center / right / top / middle / bottom). Deltas supplémentaires : SlideReordered, ObjectReordered. Undo/Redo reconstruit les opérations inverses effectivement appliquées afin de préserver les identifiants lors du rétablissement.


## Mise en page Writer (protocole 1, stockage schéma 4)

`SetPageLayout` accepte `layout: {width,height,top,right,bottom,left}` en pixels logiques à 96 dpi. `layout:null` restaure le défaut A4 à marges de 96 pixels. Les six valeurs sont finies, les dimensions entre 200 et 2 400, les marges positives ou nulles et la zone de texte au moins 144 × 144. Le delta `PageLayoutChanged` porte `layout` ; Undo/Redo restaure sa valeur précédente. Les styles de paragraphe ajoutent `page_break_before` (false par défaut).

Le stockage schéma 4 protège les données de page contre leur suppression silencieuse par les anciennes versions. Les formats plats sans ces données restent en schéma 2, ou 3 en présence de plages enrichies. Les anciens natifs contenant une source DOCX retrouvent leur format papier à partir du paquet original.

Tableaux Writer : `InsertParagraph` et `SetParagraph` acceptent `table` : `{rows, header}` où `rows` est une grille rectangulaire de textes (1 à 200 lignes, 1 à 20 colonnes, 5 000 caractères par cellule) et `header` met la première ligne en en-tête. Un tableau est son propre paragraphe : son texte est vide, il n’a pas d’image et ne peut ni être scindé ni fusionné. `null` retire le tableau. Un document contenant un tableau est enregistré en schéma natif 5.
