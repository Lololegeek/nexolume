# Architecture de Nexolume

## Responsabilités

`apps/Desktop` possède les vues XAML, la navigation, la sélection, les brouillons de saisie et le rendu Avalonia / Skia. Les données visibles sont une projection locale du moteur. Les brouillons ne deviennent un contenu confirmé qu’après acceptation d’une commande Rust. Les traitements FFI et les écritures de fichiers s’exécutent de manière asynchrone.

`src/OfficeBridge` est l’unique appelant P/Invoke. `OfficeEngine` possède un SafeHandle. `DocumentSession` sérialise les opérations par document, applique les deltas et conserve le chemin / état de sauvegarde. Le contenu et l’historique appartiennent au moteur. Une incohérence de révision provoque un rechargement du snapshot et un rejet explicite de la modification obsolète ; elle n’est pas rejouée silencieusement.

## Workspace Rust

| Crate | Responsabilité |
|---|---|
| document-core | Documents, paragraphes, cellules, diapositives, objets et identifiants |
| command-system | Enveloppes et schéma de commandes JSON |
| history-engine | Piles communes d’opérations inverses, transactions et regroupement de frappe |
| change-tracking | Deltas typés par discriminant, données ciblées |
| writer-engine | Modifications / validation de paragraphes et styles |
| sheets-engine | Parseur d’expressions, dépendances, évaluation, cycles, propagation |
| slides-engine | Diapositives, objets, déplacements et validation géométrique |
| file-formats | JSON natif versionné, validation à l’ouverture et recalcul |
| office-ffi | Registres de handles et buffers, frontière C ABI, routage des sessions |

Les inverses portent sur le paragraphe, la cellule ou l’objet concerné. Supprimer une diapositive conserve cette diapositive pour la restauration, sans recopier la présentation entière. La frappe groupée conserve la valeur initiale et la dernière valeur d’un paragraphe au lieu de garder toutes les frappes intermédiaires. Un nouveau changement vide Redo. Une transaction échouée applique les inverses déjà produits, sans modifier la révision.

## Rendu

Writer réutilise TextBox pour caret, sélection et IME. Son RichTextPresenter surcharge le point d’extension CreateTextLayout d’Avalonia : les propriétés de caractères sont transmises au même TextLayout qui sert à dessiner, mesurer, placer le curseur et sélectionner le texte. Les brouillons suivent les plages enrichies entre les validations Rust ; seuls les styles acceptés par le moteur deviennent des données confirmées. Les overlays d’orthographe utilisent les rectangles de ce TextLayout. Sheets dessine les cellules visibles dans un contrôle unique : aucune instanciation de millions de contrôles. Slides dessine une surface logique 960 × 540, transformée par Viewbox. Le déplacement est prévisualisé localement et confirmé au relâchement.

Le moteur calcule et stocke les positions / propriétés. C# utilise les coordonnées courantes de la souris pour proposer une commande. Il n’existe ni serveur HTTP local ni processus moteur séparé.

## Évolutions

Les formats sont isolés pour accueillir des importeurs/exporteurs. Remplacer le rendu Writer par un moteur de pagination ne nécessite pas de déplacer l’historique dans C#. Les propriétés de paragraphe restent distinctes des plages de caractères (start/length en UTF-16, styles explicites non chevauchants). Les fichiers natifs nécessitant ces plages, sans réglages de page, utilisent le schéma 3 pour empêcher les anciennes versions de supprimer silencieusement les styles ; le modèle interne normalise les anciens fichiers et les fichiers plats restent en schéma 2. Les deltas ParagraphChanged transportent le texte, le style de paragraphe et ses plages enrichies.


## Sortie paginée Writer

`WriterPrintLayout` façonne les paragraphes du snapshot avec Avalonia TextLayout, puis distribue ses TextLine entières dans les pages. Il conserve les plages enrichies, l’alignement, les marqueurs de listes et les sauts explicites. Le snapshot possède ses layouts et les libère à la fermeture de l’aperçu. La saisie reste dans les TextBox de la surface extensible.

`PrintPageControl` dessine ces lignes pour l’aperçu, le PDF Skia et les pages Windows. Les facteurs d’échelle font partie des transformations Avalonia, afin que le backend ne les réinitialise pas. Le PDF convertit 96 pixels logiques en 72 points, écrit un fichier temporaire puis remplace sa destination. L’impression Windows rend chaque page en bitmap BGRA à 192 dpi, puis transmet les pages au DC GDI du pilote ; elle n’est pas vectorielle. Le chemin de document, les révisions et l’historique ne sont pas modifiés par une sortie.

`PageLayout` appartient au document Rust. DOCX importe les réglages de la section finale ; leur modification touche pgSz/pgMar de cette section et préserve ses autres propriétés. ODT utilise styles.xml et un master-page global. Les réglages et sauts nécessitent le schéma de stockage 4, tout en conservant le schéma interne 2.
