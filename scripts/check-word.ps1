param([switch]$NoBuild, [ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (-not $NoBuild) { & "$PSScriptRoot/build.ps1" -Configuration $Configuration }
$validation=Join-Path $root 'artifacts/validation'
New-Item -ItemType Directory -Force -Path $validation | Out-Null
$sourcePath=Join-Path $validation 'word-reference.docx'
$outputPath=Join-Path $validation 'word-reference-edited.docx'
$picturePath=Join-Path $validation 'word-test-image.png'
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new(160,90); $graphics=[Drawing.Graphics]::FromImage($bitmap)
try { $graphics.Clear([Drawing.Color]::FromArgb(40,92,179)); $graphics.FillRectangle([Drawing.Brushes]::White,24,22,112,46); $bitmap.Save($picturePath,[Drawing.Imaging.ImageFormat]::Png) }
finally { $graphics.Dispose(); $bitmap.Dispose() }
$word=$null; $document=$null
try {
    $word=New-Object -ComObject Word.Application
    $word.Visible=$false; $word.DisplayAlerts=0; $word.AutomationSecurity=3
    $document=$word.Documents.Add()
    $document.Content.Text="Compte rendu Nexolume`rUn paragraphe avec du gras et de l’italique.`rUne ligne à modifier sans perdre la mise en forme.`rPremier élément`rDeuxième élément`r"
    $document.Styles.Item(-1).Font.Name='Calibri'; $document.Styles.Item(-1).Font.Size=12
    $heading=$document.Styles.Add('NexolumeHeading',1)
    $heading.BaseStyle=$document.Styles.Item(-1).NameLocal
    $heading.Font.Size=24; $heading.Font.Bold=-1
    $document.Paragraphs.Item(1).Range.Style='NexolumeHeading'
    $start=$document.Content.Text.IndexOf('gras'); $document.Range($start,$start+4).Font.Bold=-1
    $start=$document.Content.Text.IndexOf('italique'); $document.Range($start,$start+8).Font.Italic=-1
    $document.Range($document.Paragraphs.Item(4).Range.Start,$document.Paragraphs.Item(5).Range.End).ListFormat.ApplyBulletDefault()
    $document.Sections.Item(1).Headers.Item(1).Range.Text='Nexolume · En-tête conservé'
    $document.Sections.Item(1).Footers.Item(1).Range.Text='Pied de page conservé'
    $document.PageSetup.Orientation=1
    $document.PageSetup.LeftMargin=54; $document.PageSetup.RightMargin=54
    $table=$document.Tables.Add($document.Range($document.Content.End-1,$document.Content.End-1),2,2)
    $table.Cell(1,1).Range.Text='Article'; $table.Cell(1,2).Range.Text='Montant'
    $table.Cell(2,1).Range.Text='Projet'; $table.Cell(2,2).Range.Text='120 €'
    $table.Range.InsertParagraphAfter()
    $picture=$document.InlineShapes.AddPicture($picturePath,$false,$true,$document.Range($document.Content.End-1,$document.Content.End-1))
    $picture.LockAspectRatio=-1; $picture.Width=90
    $document.SaveAs2($sourcePath,16); $document.Close(0); $document=$null
    & (Join-Path $root "tests/Integration/bin/$Configuration/net10.0/Integration.exe") --word-fixture $sourcePath $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Word package preservation integration failed.' }
    $document=$word.Documents.Open($outputPath,$false,$true,$false)
    if (-not $document.Content.Text.Contains('Une ligne modifiée dans Nexolume') -or -not $document.Content.Text.Contains('Paragraphe ajouté depuis Nexolume')) { throw 'Word did not read the Nexolume edits.' }
    if ($document.Tables.Count -ne 1 -or $document.InlineShapes.Count -ne 1) { throw 'Word lost a table or image.' }
    if (-not $document.Sections.Item(1).Headers.Item(1).Range.Text.Contains('En-tête conservé')) { throw 'Word lost the header.' }
    if (-not $document.Sections.Item(1).Footers.Item(1).Range.Text.Contains('Pied de page conservé')) { throw 'Word lost the footer.' }
    if ($document.PageSetup.Orientation -ne 1) { throw 'Word lost the landscape page layout.' }
    $start=$document.Content.Text.IndexOf('gras'); if ($document.Range($start,$start+4).Font.Bold -ne -1) { throw 'Word lost mixed bold runs.' }
    $start=$document.Content.Text.IndexOf('italique'); if ($document.Range($start,$start+8).Font.Italic -ne -1) { throw 'Word lost mixed italic runs.' }
    $start=$document.Content.Text.IndexOf('Nexolume sans perdre')
    $rich=$document.Range($start,$start+8)
    if ($rich.Font.Bold -ne -1 -or $rich.Font.Size -ne 24 -or $rich.Font.Name -ne 'Georgia' -or $rich.Font.Color -ne 0x3D26B3 -or $rich.Shading.BackgroundPatternColor -ne 11071999) { throw 'Word did not retain selected character formatting in the imported document.' }
    $start=$document.Content.Text.IndexOf('Une ligne modifiée')
    if ($document.Range($start,$start+3).Font.Bold -ne 0 -or $document.Range($start,$start+3).Font.Size -ne 15) { throw 'Selected formatting leaked outside its range.' }
    $document.Close(0); $document=$null
    $document=$word.Documents.Open((Join-Path $validation 'word-reference-numbered.docx'),$false,$true,$false)
    $start=$document.Content.Text.IndexOf('Paragraphe ajouté depuis Nexolume')
    if ($document.Range($start,$start+10).ListFormat.ListType -ne 3) { throw 'Added numbering was not read correctly by Word.' }
    $document.Close(0); $document=$null
    $document=$word.Documents.Open((Join-Path $validation 'word-reference-new.docx'),$false,$true,$false)
    if ($document.Paragraphs.Item(1).Range.Characters.Item(1).Font.Size -ne 24) { throw 'New Word document font size conversion failed.' }
    if ($document.Paragraphs.Item(2).Range.ListFormat.ListType -ne 2) { throw 'New Word document lost native bullets.' }
    if ($document.Paragraphs.Item(2).Range.Characters.Item(1).Shading.BackgroundPatternColor -ne 11071999) { throw 'New Word document lost highlighting.' }
    $start=$document.Content.Text.IndexOf('passage enrichi')
    $rich=$document.Range($start,$start+15)
    if ($rich.Font.Bold -ne -1 -or $rich.Font.Italic -ne -1 -or $rich.Font.Underline -ne 1 -or $rich.Font.Size -ne 18 -or $rich.Font.Name -ne 'Georgia') { throw 'New DOCX lost selected rich formatting.' }
    $document.Close(0); $document=$null
    $document=$word.Documents.Open((Join-Path $validation 'word-reference-new.odt'),$false,$true,$false)
    $start=$document.Content.Text.IndexOf('passage enrichi')
    $rich=$document.Range($start,$start+15)
    if ($rich.Font.Bold -ne -1 -or $rich.Font.Italic -ne -1 -or $rich.Font.Underline -ne 1 -or $rich.Font.Size -ne 18 -or $rich.Font.Name -ne 'Georgia') { throw ('New ODT lost selected rich formatting: bold={0}, italic={1}, underline={2}, size={3}, font={4}' -f $rich.Font.Bold,$rich.Font.Italic,$rich.Font.Underline,$rich.Font.Size,$rich.Font.Name) }
    $document.Close(0); $document=$null
    foreach ($extension in @('docx','odt')) {
        $document=$word.Documents.Open((Join-Path $validation ('word-reference-page.'+$extension)),$false,$true,$false)
        if ([Math]::Abs($document.PageSetup.PageWidth-419.53) -gt 0.2 -or [Math]::Abs($document.PageSetup.PageHeight-595.28) -gt 0.2 -or [Math]::Abs($document.PageSetup.LeftMargin-28.35) -gt 0.2) { throw ('Word did not read A5 paper and 1 cm margins from '+$extension) }
        if ($document.Paragraphs.Item(3).Range.ParagraphFormat.PageBreakBefore -ne -1) { throw ('Word did not read the explicit page break from '+$extension) }
        $document.Close(0); $document=$null
    }
    $document=$word.Documents.Open((Join-Path $validation 'word-reference-page-edited.docx'),$false,$true,$false)
    if ($document.PageSetup.Orientation -ne 0 -or [Math]::Abs($document.PageSetup.PageWidth-612) -gt 0.2 -or [Math]::Abs($document.PageSetup.LeftMargin-63) -gt 0.2) { throw 'Word did not read changed Letter page settings in preserved DOCX.' }
    if ($document.Tables.Count -ne 1 -or $document.InlineShapes.Count -ne 1 -or -not $document.Sections.Item(1).Headers.Item(1).Range.Text.Contains('En-tête conservé')) { throw 'Changing paper settings lost existing Word objects.' }
    $document.Close(0); $document=$null
    Write-Output 'PASS paper settings in Word: native DOCX/ODT A5, 1 cm margins, page break, preserved DOCX changed to Letter portrait with objects retained.'
    Write-Output 'PASS selected rich text in Word: imported DOCX, new DOCX and ODT; fonts, sizes, decorations, exact color/highlight and unmodified surrounding text.'
    Write-Output 'PASS new DOCX in Word: 24 pt typography, bullet numbering and exact highlight color.'
    $report='PASS: Word-generated DOCX -> Nexolume edit -> native save/reopen -> DOCX -> Word; text, insertion, mixed formatting, table, image, header/footer and landscape layout verified.'
    $report | Set-Content -LiteralPath (Join-Path $validation 'word-compatibility.txt')
    Write-Output $report
}
finally {
    if ($document) { $document.Close(0) }
    if ($word) { $word.Quit(0); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word) }
}
