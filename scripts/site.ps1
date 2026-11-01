#requires -Version 7.0
<#
.SYNOPSIS
Builds the public site (codex-tracker.zqsdev.com) into artifacts/site, and deploys it with -Deploy.
.DESCRIPTION
Sources are in site/. The build injects the version from Directory.Build.props and today's date, copies the
demo screenshots from docs/, the embedded Roboto fonts and the app icon, then writes robots.txt and sitemap.xml.
-Deploy publishes to the Netlify site through the Netlify CLI, which must be signed in (`netlify login`).
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [string]$Site = '4614a772-0a46-40bd-a0d3-85f95dd514b7' # Netlify project codex-tracker-zqsdev
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$origin = 'https://codex-tracker.zqsdev.com'
$out = Join-Path $root 'artifacts/site'
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$date = (Get-Date).ToString('yyyy-MM-dd')

if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'site') -Destination $out -Recurse
Get-ChildItem $out -Recurse -File -Include *.html, *.txt | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName)
    [IO.File]::WriteAllText($_.FullName, $text.Replace('{{VERSION}}', $version).Replace('{{DATE}}', $date))
}

$images = New-Item -ItemType Directory -Force (Join-Path $out 'img')
foreach ($name in 'hero', 'dashboard', 'resets-week', 'calendar', 'social-preview') { Copy-Item (Join-Path $root "docs/$name.png") $images }
$fonts = New-Item -ItemType Directory -Force (Join-Path $out 'fonts')
Copy-Item (Join-Path $root 'src/CodexTracker.App/Assets/Fonts/*.ttf') $fonts

Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::new((Join-Path $root 'src/CodexTracker.App/Assets/App.ico'), 256, 256)
try { $bitmap = $icon.ToBitmap(); $bitmap.Save((Join-Path $out 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose() }
finally { $icon.Dispose() }

[IO.File]::WriteAllText((Join-Path $out 'robots.txt'), "User-agent: *`nAllow: /`n`nSitemap: $origin/sitemap.xml`n")
# Translations of one page share their hreflang alternates; any other page is listed on its own.
$groups = @(
    [ordered]@{ en = '/'; fr = '/fr/'; es = '/es/'; de = '/de/'; 'pt-BR' = '/pt/'; ja = '/ja/' },
    [ordered]@{ en = '/privacy/'; fr = '/fr/confidentialite/' }
)
$published = Get-ChildItem $out -Recurse -File -Filter index.html | ForEach-Object {
    '/' + [IO.Path]::GetRelativePath($out, $_.DirectoryName).Replace('\', '/').Trim('.').Trim('/') + '/' -replace '^//$', '/'
}
$grouped = $groups | ForEach-Object { $_.Values }
$entries = @(foreach ($group in $groups) {
    $present = [ordered]@{}; foreach ($k in $group.Keys) { if ($published -contains $group[$k]) { $present[$k] = $group[$k] } }
    foreach ($path in $present.Values) { @{ Path = $path; Alternates = $present } }
}) + @($published | Where-Object { $_ -notin $grouped } | Sort-Object | ForEach-Object { @{ Path = $_; Alternates = $null } })
$urls = foreach ($entry in $entries) {
    $priority = $entry.Path -eq '/' ? '1.0' : $entry.Path -match '^/(fr|es|de|pt|ja)/$' ? '0.9' : $entry.Path -like '/guides/*' ? '0.8' : '0.5'
    $links = if ($entry.Alternates) {
        (@($entry.Alternates.Keys | ForEach-Object { "    <xhtml:link rel=`"alternate`" hreflang=`"$_`" href=`"$origin$($entry.Alternates[$_])`"/>" }) +
            "    <xhtml:link rel=`"alternate`" hreflang=`"x-default`" href=`"$origin$(@($entry.Alternates.Values)[0])`"/>") -join "`n"
    }
    "  <url>`n    <loc>$origin$($entry.Path)</loc>`n    <lastmod>$date</lastmod>`n    <priority>$priority</priority>$(if ($links) { "`n$links" })`n  </url>"
}
$sitemap = "<?xml version=`"1.0`" encoding=`"UTF-8`"?>`n<urlset xmlns=`"http://www.sitemaps.org/schemas/sitemap/0.9`" xmlns:xhtml=`"http://www.w3.org/1999/xhtml`">`n$($urls -join "`n")`n</urlset>`n"
[IO.File]::WriteAllText((Join-Path $out 'sitemap.xml'), $sitemap)

$left = Get-ChildItem $out -Recurse -File -Include *.html, *.txt | Select-String -SimpleMatch '{{' -List
if ($left) { throw "Variables non remplacées : $($left.Path -join ', ')" }
Write-Output "Site $version construit dans $out"

if ($Deploy) {
    $netlify = (Get-Command netlify -ErrorAction Stop).Source
    & $netlify deploy --prod --dir $out --site $Site --message "Codex Tracker $version"
    if ($LASTEXITCODE) { throw 'Déploiement Netlify échoué.' }
    Write-Output "Publié sur $origin"
    # IndexNow notifies Bing (and through it ChatGPT search and Copilot), Yandex, Seznam and Naver of the new content.
    $key = (Get-ChildItem (Join-Path $root 'site') -Filter '*.txt' | Where-Object { $_.BaseName -match '^[0-9a-f]{32}$' } | Select-Object -First 1).BaseName
    if ($key) {
        $urls = @('/', '/fr/', '/privacy/', '/fr/confidentialite/', '/llms.txt') | ForEach-Object { "$origin$_" }
        $payload = @{ host = 'codex-tracker.zqsdev.com'; key = $key; keyLocation = "$origin/$key.txt"; urlList = $urls } | ConvertTo-Json
        $answer = Invoke-WebRequest 'https://api.indexnow.org/indexnow' -Method Post -ContentType 'application/json; charset=utf-8' -Body $payload -SkipHttpErrorCheck
        Write-Output "IndexNow : HTTP $($answer.StatusCode)"
    }
}
