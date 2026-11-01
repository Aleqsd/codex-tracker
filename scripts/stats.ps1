#requires -Version 7.0
<#
.SYNOPSIS
Public reach of Codex Tracker: downloads per release, GitHub traffic (14 days), referrers, stars and forks.
.DESCRIPTION
Uses the GitHub CLI (`gh auth login`, push access needed for traffic). -Json prints one object for agents.
Search Console clicks are not included: open https://search.google.com/search-console?resource_id=sc-domain:zqsdev.com
#>
[CmdletBinding()]
param([switch]$Json, [string]$Repo = 'Aleqsd/codex-tracker')
$ErrorActionPreference = 'Stop'
function Api([string]$path) { gh api $path --paginate 2>$null | ConvertFrom-Json }

$repository = Api "repos/$Repo"
$releases = @(Api "repos/$Repo/releases?per_page=100") | ForEach-Object { $_ } | ForEach-Object {
    $installer = ($_.assets | Where-Object name -like '*-Setup.exe' | Measure-Object download_count -Sum).Sum
    $zip = ($_.assets | Where-Object name -like '*.zip' | Measure-Object download_count -Sum).Sum
    [pscustomobject]@{ tag = $_.tag_name; published = ([datetime]$_.published_at).ToString('yyyy-MM-dd'); installer = [int]$installer; zip = [int]$zip }
}
$views = Api "repos/$Repo/traffic/views"
$clones = Api "repos/$Repo/traffic/clones"
$referrers = @(Api "repos/$Repo/traffic/popular/referrers")
$result = [pscustomobject]@{
    stars = $repository.stargazers_count; forks = $repository.forks_count; watchers = $repository.subscribers_count
    downloads = [pscustomobject]@{ installer = ($releases | Measure-Object installer -Sum).Sum; zip = ($releases | Measure-Object zip -Sum).Sum }
    views14d = [pscustomobject]@{ total = $views.count; unique = $views.uniques }
    clones14d = [pscustomobject]@{ total = $clones.count; unique = $clones.uniques }
    referrers = $referrers | ForEach-Object { [pscustomobject]@{ source = $_.referrer; views = $_.count; unique = $_.uniques } }
    releases = $releases
}
if ($Json) { $result | ConvertTo-Json -Depth 5; return }
"Codex Tracker · $Repo"
"Étoiles $($result.stars) · forks $($result.forks) · abonnés $($result.watchers)"
"Téléchargements : installateur $($result.downloads.installer) · ZIP $($result.downloads.zip) (mises à jour automatiques incluses)"
"GitHub, 14 jours : $($result.views14d.total) vues ($($result.views14d.unique) visiteurs) · $($result.clones14d.total) clones"
if ($result.referrers) { "Sources :"; $result.referrers | ForEach-Object { "  $($_.source) : $($_.views) vues ($($_.unique))" } }
"Dernières versions :"
$releases | Select-Object -First 8 | ForEach-Object { "  $($_.tag) ($($_.published)) : $($_.installer) installateur, $($_.zip) ZIP" }
