# Nexolume

Suite bureautique desktop hors ligne pour Windows x64 : C# / .NET 10, Avalonia 11.3.22 et moteurs Rust. Writer, Sheets et Slides partagent une seule application et un historique natif. L’interface conserve les repères d’un traitement de texte à ruban, avec une identité ardoise et bleue propre à Nexolume.

## Lancer

La distribution portable `artifacts/Nexolume-win-x64/Nexolume.exe` inclut .NET et la DLL Rust. Extraire **tout** le ZIP avant de lancer l’exécutable ; aucune installation ni connexion n’est nécessaire pour travailler.

Depuis les sources, installer .NET SDK 10 et Rust stable pour Windows MSVC avec les outils C++ Visual Studio nécessaires à l’édition de liens Rust. Aucun code C++ ne fait partie du projet. La restauration initiale des paquets nécessite Internet.

```powershell
.\scripts\build.ps1
.\scripts\run.ps1 -NoBuild
.\scripts\test.ps1
# Compiler, tester et produire la distribution autonome :
.\scripts\publish.ps1
```

## Fonctionnalités disponibles

- **Accueil** : créer les trois types de documents, ouvrir des fichiers locaux et rechercher les documents récents avec leurs chemins et dates de modification.
- **Shell** : menu Fichier, icônes vectorielles, bandeau d’erreur et dialogue de fermeture cohérents, documents en onglets, titre modifiable avec Entrée, indicateur de modifications, confirmation avant fermeture, thèmes clair et sombre, erreurs affichées dans la barre d’état.
- **Writer** : saisie UTF-8, plusieurs paragraphes, gras / italique / souligné, police, taille, couleur et surlignage **sur la sélection dans un paragraphe**. L’alignement et les styles de titre restent des propriétés de paragraphe. Entrée scinde le paragraphe, Retour arrière au début fusionne avec le précédent, Maj+Entrée insère une nouvelle ligne simple. Le presse-papiers conserve ici le texte brut. Zoom de 50 à 150 % et règle de repérage visuel.
- **Sheets** : grille dessinée sur la zone visible, navigation clavier et souris, texte et nombres, formules `+ - * /`, parenthèses, signes unaires, références et `SUM(A1:A5)`. Valeur saisie et résultat séparés. Dépendances, recalcul, cycles et erreurs gérés en Rust.
- **Slides** : diapositives avec vignettes, zones de texte, rectangles et ellipses, déplacement et redimensionnement à la souris, modification du texte, des coordonnées et de la couleur dans le panneau latéral. Un glissement correspond à une seule entrée d’historique.
- **Commun** : Undo/Redo Rust, transactions atomiques, deltas versionnés, sauvegarde et chargement natifs. L’écriture utilise un fichier temporaire, un flush disque et un remplacement avec sauvegarde `.bak` pour un fichier existant.
- **Formats Writer** : ouverture, édition et enregistrement en `.docx` (Word) et `.odt` (LibreOffice/OpenDocument), en plus de `.oswriter`.

Formats natifs JSON versionnés (schémas 2 à 4 ; lecture des anciens fichiers de schéma 1) : `.oswriter`, `.ossheet`, `.osslides`. Ces extensions issues du cahier des charges restent reconnues sous le nom Nexolume.

## Raccourcis

| Raccourci | Action |
|---|---|
| Ctrl+N | Nouveau texte |
| Ctrl+O | Ouvrir |
| Ctrl+S / Ctrl+Maj+S | Enregistrer / Enregistrer sous |
| Ctrl+Z / Ctrl+Y / Ctrl+Maj+Z | Annuler / Rétablir |
| Ctrl+W | Fermer l’onglet actif |
| Sheets : flèches, Page précédente/suivante | Naviguer |
| Sheets : Entrée ou F2 | Modifier la cellule sélectionnée |
| Sheets : Suppr / Échap | Effacer la cellule / abandonner la saisie |
| Sheets : molette / Maj+molette | Défilement vertical / horizontal |

## Validation et limites

`test.ps1` exécute les tests Rust, les assertions .NET contre la vraie DLL, les scénarios de saisie et de souris Avalonia en environnement headless avec rendu Skia, puis un démarrage **natif Windows** masqué. Les captures sont enregistrées dans `artifacts/validation`. Aucun contrôle du bureau de l’utilisateur n’est nécessaire. Le démarrage natif masqué vérifie la création des trois éditeurs, des commandes, la sauvegarde et leur fermeture ; il ne remplace pas une exploration manuelle de tous les dialogues Windows.

Prototype réduit : pas encore de sélection sur plusieurs paragraphes, pagination dans la surface de saisie, insertion d’images, graphiques ou plugins. Writer utilise une surface de saisie extensible et un aperçu paginé partagé avec le PDF et l’impression ; les objets avancés des DOCX importés sont conservés à l’enregistrement mais leur affichage reste simplifié. La règle graduée est un repère visuel, sans taquets modifiables. Sheets propose une feuille par document, une sélection de plages et des opérations atomiques limitées à 1 000 cellules. Les nombres de formule utilisent le point décimal et les fonctions `SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT` (alias `SOMME`, `MOYENNE`, `NB` disponibles). Le graphe de dépendances est reconstruit à chaque édition ; les calculs et deltas restent ciblés sur les dépendants. Aucun benchmark de grands classeurs n’est revendiqué.

Les fichiers natifs sont limités à 16 Mio, les documents DOCX/ODT à 10 Mio, et une cellule à 8 192 octets. Les plages SUM sont limitées à 10 000 cellules et les profondeurs d’expression / dépendance sont bornées. Historique en mémoire uniquement, non conservé après fermeture.

Les fichiers de préférences et le journal d’erreurs sont dans `%LOCALAPPDATA%/Nexolume`. Voir [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/COMMANDS.md](docs/COMMANDS.md), [docs/FFI.md](docs/FFI.md) et [docs/ROADMAP.md](docs/ROADMAP.md).

## Passe UI

Les trois espaces partagent des champs, rubans et états de sélection cohérents. Sheets propose aussi l’accès direct à une adresse, les barres de défilement et la somme des cellules au-dessus. Slides dispose d’un inspecteur contextuel, de valeurs numériques lisibles et d’une palette de remplissage.

Les tests d’interface couvrent 24 rendus supplémentaires : quatre surfaces, deux thèmes et trois tailles (1000, 1440 et 1920 px), plus la recherche des documents récents, la navigation directe, la somme rapide, le changement de diapositive et les choix Annuler / Ne pas enregistrer du dialogue de fermeture.

La passe de productivité ajoute un plan navigable, des statistiques, un mode concentration avec retour par Échap et l’organisation des paragraphes dans Writer. Sheets dispose d’un ruban à onglets, de sélections à la souris ou avec Maj+flèches, de navigation par plages (`A1:C6`), du copier-coller tabulaire, de formats sur la sélection, de totaux de sélection, du zoom et de l’affichage des formules. Le collage conserve les formules telles quelles, sans recalage relatif des références. Slides propose trois dispositions modifiables, des panneaux repliables et la taille, le gras et la couleur du texte réellement enregistrés. L’accueil filtre et trie les documents récents.

## Outils bureautiques supplémentaires

- Writer : listes à puces et numérotées, retraits, couleurs, surlignage, justification, interligne et espacement après, effacement du format, recherche avec choix de la casse et des mots entiers, navigation précédente/suivante et remplacements ciblés ou globaux annulables. Le ruban affiche la taille en points, propose une galerie compacte dans une petite fenêtre et peut être masqué. Les propriétés de caractères peuvent être appliquées à une sélection ; sans sélection, la commande garde la portée du paragraphe actif.
- Sheets : gras et formats général, nombre, monétaire en euros et pourcentage. La valeur calculée reste indépendante de la présentation. Les agrégats ignorent les cellules vides et le texte dans les références.
- Slides : duplication avec nouveaux identifiants, ordre des diapositives et des objets, centrage sur la diapositive, déplacement au clavier et diaporama plein écran. F5 présente depuis le début, Maj+F5 depuis la diapositive courante ; flèches pour naviguer, Échap pour quitter. Ctrl+D duplique l’objet sélectionné.
- Writer : Ctrl+B / Ctrl+I / Ctrl+U pour la mise en forme, Ctrl+F / Ctrl+H pour la recherche et le remplacement.
- Writer, onglet **Révision** : majuscules en début de phrase, correction de doubles majuscules, fautes françaises courantes et remplacements personnels (jusqu’à 100). Les apostrophes, `...`, `(c)`, `(r)`, `(tm)` et les flèches sont convertis pendant la saisie. Les sigles tout en majuscules sont préservés. Les options sont indépendantes et enregistrées dans `%LOCALAPPDATA%/Nexolume/writing.json`.
- `*`, `-` ou `1.` suivis d’Espace créent une liste. Entrée poursuit la liste ; Entrée sur un élément vide la termine. Retour arrière rétablit immédiatement la dernière correction de texte. Les commandes de casse agissent sur la sélection ou le paragraphe actif ; Maj+F3 alterne les majuscules à chaque mot, tout en majuscules et tout en minuscules. Les modifications s’annulent avec Ctrl+Z.

La correction automatique repose sur des règles et des remplacements. La vérification orthographique ci-dessous la complète ; la correction grammaticale contextuelle n’est pas encore disponible. Le texte collé, les compositions de plusieurs caractères et les documents ouverts ne sont pas réécrits automatiquement. Les adresses et chemins reconnus sont exclus des corrections de mots. Les mots ambigus comme `a`/`à` ou `ou`/`où` restent à la décision de l’auteur. Les tests UI couvrent la saisie réelle, le rétablissement, les options, les remplacements personnels, les listes, le changement de casse, les sauvegardes et le ruban Révision dans les deux thèmes à 1000, 1440 et 1920 px.

## Texte enrichi Writer

Sélectionnez un passage dans un paragraphe pour modifier uniquement ses caractères : gras, italique, souligné, famille de police, taille, couleur et surlignage. Le ruban indique la portée de l’action. **Effacer le format** réinitialise la sélection ; sans sélection, la commande réinitialise le paragraphe. L’alignement, les listes, les retraits et les espacements concernent toujours le paragraphe.

Le curseur, la sélection, la mesure des glyphes et les retours à la ligne utilisent le même moteur de mise en forme Avalonia. Les portions enrichies survivent à la frappe, aux corrections, à la recherche/remplacement, à Entrée/Retour arrière, à la duplication, à l’annulation et aux sauvegardes `.oswriter`, DOCX et ODT. Les anciens fichiers restent lisibles. Un fichier natif contenant des styles mixtes et sans réglages de page utilise le schéma 3 : les anciennes versions de Nexolume le refusent explicitement au lieu de perdre ces styles. Les fichiers sans styles mixtes ni réglages de page restent en schéma 2.

Limites : 20 000 portions de style par paragraphe, sélection dans un seul paragraphe et presse-papiers texte brut. Sans sélection, la mise en forme modifie encore le paragraphe actif plutôt qu’un style de frappe futur. La pagination dans la surface de saisie, les images et les tableaux éditables restent à développer. Les déclarations de polices ODT et les styles sur une sélection sont contrôlés en rouvrant réellement les fichiers avec Microsoft Word, en complément du validateur Open XML et des tests d’édition.

## Vérification orthographique et recherche Writer

**Révision → Orthographe**, ou **F7**, ouvre le panneau de relecture. La vérification s’exécute localement en arrière-plan après une courte pause de saisie, avec les dictionnaires Windows installés (`fr-FR` ou `en-US`). Les mots signalés sont soulignés en rouge et les suggestions se sélectionnent dans le panneau. Une correction passe par le moteur Rust et s’annule avec Ctrl+Z. Une suggestion calculée pour un texte devenu obsolète est ignorée.

On peut ignorer un mot jusqu’à la fermeture du document, l’ajouter au dictionnaire personnel de Nexolume ou le retirer avec **Mon dictionnaire**. Ces préférences se conservent par langue dans `writing.json` ; le dictionnaire global de Windows n’est pas modifié. Le soulignement est désactivable. Si la langue manque, le panneau l’explique et indique les fonctionnalités linguistiques Windows à installer. La liste est limitée à 200 signalements par paragraphe et ne prétend pas vérifier la grammaire.

La recherche propose **Respecter la casse**, **Mots entiers uniquement**, les occurrences précédente/suivante avec retour au début, le remplacement de la sélection correspondante et le remplacement global en une seule annulation. Les limites des mots prennent en compte les caractères Unicode, y compris les marques combinées. La navigation affiche au plus 10 000 résultats ; le remplacement global traite jusqu’à 100 000 occurrences et 1 000 paragraphes, sans appliquer de résultat partiel si une limite est dépassée.

Les tests couvrent le véritable correcteur français Windows, ses suggestions et positions UTF-16, les langues absentes, le panneau, l’annulation, le dictionnaire personnel, le changement de langue, F7, le mode concentration, les résultats périmés et les rendus dans les deux thèmes à trois tailles. La recherche est vérifiée avec des mots accentués, des emojis, plusieurs paragraphes et plus de 10 000 occurrences.

Référence de l’intégration native : [Microsoft Spell Checker API](https://learn.microsoft.com/windows/win32/api/spellcheck/).

L’inspection de Word et ses limites sont consignées dans [docs/WORD_REFERENCE.md](docs/WORD_REFERENCE.md). Les formats DOCX/ODT et leurs limites sont décrits dans [docs/OFFICE_FORMATS.md](docs/OFFICE_FORMATS.md). Tous les outils présentés par Nexolume passent par le moteur Rust pour modifier un document.


## Papier, aperçu et PDF

Dans Writer, **Mise en page → Papier et marges** règle A4, A5, Lettre ou un format personnalisé, l’orientation et les quatre marges en centimètres. Le réglage s’annule et se rétablit avec l’historique du document. **Insertion → Page avant le paragraphe** active ou retire un saut explicite. Entrée dans ce paragraphe conserve le saut sur le paragraphe initial et ne le répète pas sur le suivant.

**Mise en page → Aperçu · PDF · Imprimer**, le menu Fichier ou **Ctrl+P** ouvre les pages calculées, avec navigation, zoom et export PDF de toutes les pages, de la page affichée ou d’une plage. Les paragraphes longs sont répartis par lignes entières ; le PDF contient du texte sélectionnable, les styles enrichis, les listes et les sauts de page. L’export conserve le chemin et l’état d’enregistrement du document éditable. L’impression Windows propose le dialogue système, les pages et les copies ; la sortie papier/pilote reste non validée dans cet environnement, dont le service Spouleur est arrêté et inaccessible.

Les fichiers natifs avec réglages de page ou sauts explicites utilisent le schéma 4. DOCX/ODT conservent papier, marges et sauts de page. Pour les DOCX multisections, les réglages modifient la dernière section ; les autres sections restent dans le paquet d’origine. L’aperçu utilise un format global. Les objets Word absents de l’éditeur ne sont pas dessinés dans le PDF ni à l’impression.

Sorties en arrière-plan, sans ouvrir le document dans une fenêtre :

```powershell
.\Nexolume.exe --export-pdf "C:\Documents\Texte.oswriter" "C:\Documents\Texte.pdf"
.\Nexolume.exe --print "C:\Documents\Texte.oswriter" "Microsoft Print to PDF" "C:\Documents\Texte-systeme.pdf"
```

Les commandes quittent avec le code 0 en cas de succès, 1 en cas d’erreur (journal local de l’application). Le second exemple nécessite le service d’impression et le pilote indiqué.

Validation : `scripts/test.ps1 -Configuration Release`, `scripts/check-word.ps1 -NoBuild -Configuration Release`, puis `python scripts/check-pdf.py`. Le contrôle PDF indépendant utilise PyMuPDF (`python -m pip install --target artifacts/validation/pdf-tools pymupdf`) et compare le texte complet, les points typographiques, les dimensions, les pages exportées et le paysage. Ces outils de vérification ne sont pas requis par l’application portable.
