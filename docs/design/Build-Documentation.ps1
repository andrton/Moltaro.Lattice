$ErrorActionPreference = 'Stop'

$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'architecture.md') -Raw
$content = (ConvertFrom-Markdown -InputObject $source).Html
foreach ($diagram in @('architecture', 'memory', 'write', 'replication', 'session')) {
    $diagramHtml = Get-Content -LiteralPath (Join-Path $PSScriptRoot "assets/$diagram.html") -Raw
    $content = $content.Replace("<!-- DIAGRAM:$diagram -->", $diagramHtml)
}

if ($content.Contains('<!-- DIAGRAM:')) {
    throw 'An unresolved diagram marker remains in the generated document.'
}

$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'assets/template.html') -Raw
$html = $template.Replace('{{CONTENT}}', $content)
$destination = Join-Path $PSScriptRoot 'index.html'
[System.IO.File]::WriteAllText($destination, $html, [System.Text.UTF8Encoding]::new($false))
Write-Output "Generated $destination"
