#requires -Version 7.0
<#
.SYNOPSIS
Public reach of Codex Tracker: downloads per release, GitHub traffic (14 days), referrers, stars and forks,
and the site's visits from Cloudflare Web Analytics.
.DESCRIPTION
Uses the GitHub CLI (`gh auth login`, push access needed for traffic). -Json prints one object for agents.
Site visits need a Cloudflare API token with "Account Analytics: Read", created by the owner and kept outside the repo
in %USERPROFILE%\.codex-tracker\cloudflare-analytics-token.txt (or $env:CLOUDFLARE_ANALYTICS_TOKEN); without it the
section is skipped. Search Console clicks: https://search.google.com/search-console?resource_id=sc-domain:zqsdev.com
#>
[CmdletBinding()]
param([switch]$Json, [string]$Repo = 'Aleqsd/codex-tracker', [int]$Days = 14,
    [string]$CloudflareAccount = '7b9ee223c6a171ffa7672f2c34bf7c16', [string]$CloudflareSite = '7e28301112c54d118f8986211e58dbb1')
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

# Site visits (Cloudflare Web Analytics, GraphQL): totals, top pages and referrers over $Days days.
$site = $null
$tokenFile = Join-Path $HOME '.codex-tracker/cloudflare-analytics-token.txt'
$token = $env:CLOUDFLARE_ANALYTICS_TOKEN
if (-not $token -and (Test-Path $tokenFile)) { $token = (Get-Content -LiteralPath $tokenFile -Raw).Trim() }
if ($token) {
    $since = (Get-Date).ToUniversalTime().AddDays(-$Days).ToString('yyyy-MM-dd')
    $filter = "{ siteTag: `"$CloudflareSite`", date_geq: `"$since`" }"
    $query = "{ viewer { accounts(filter: { accountTag: `"$CloudflareAccount`" }) {
        total: rumPageloadEventsAdaptiveGroups(filter: $filter, limit: 1) { count sum { visits } }
        pages: rumPageloadEventsAdaptiveGroups(filter: $filter, limit: 8, orderBy: [count_DESC]) { count dimensions { requestPath } }
        sources: rumPageloadEventsAdaptiveGroups(filter: $filter, limit: 8, orderBy: [count_DESC]) { count dimensions { refererHost } }
        countries: rumPageloadEventsAdaptiveGroups(filter: $filter, limit: 5, orderBy: [count_DESC]) { count dimensions { countryName } } } } }"
    try {
        $answer = Invoke-RestMethod 'https://api.cloudflare.com/client/v4/graphql' -Method Post -ContentType 'application/json' `
            -Headers @{ Authorization = "Bearer $token" } -Body (@{ query = $query } | ConvertTo-Json -Compress)
        $account = $answer.data.viewer.accounts[0]
        if ($account) {
            $site = [pscustomobject]@{
                days = $Days; pageViews = [int]($account.total[0].count); visits = [int]($account.total[0].sum.visits)
                pages = $account.pages | ForEach-Object { [pscustomobject]@{ path = $_.dimensions.requestPath; views = $_.count } }
                sources = $account.sources | Where-Object { $_.dimensions.refererHost } | ForEach-Object { [pscustomobject]@{ host = $_.dimensions.refererHost; views = $_.count } }
                countries = $account.countries | ForEach-Object { [pscustomobject]@{ country = $_.dimensions.countryName; views = $_.count } }
            }
        } elseif ($answer.errors) { Write-Warning "Cloudflare : $($answer.errors[0].message)" }
    } catch { Write-Warning "Cloudflare Web Analytics indisponible : $($_.Exception.Message)" }
}
$result = [pscustomobject]@{
    stars = $repository.stargazers_count; forks = $repository.forks_count; watchers = $repository.subscribers_count
    downloads = [pscustomobject]@{ installer = ($releases | Measure-Object installer -Sum).Sum; zip = ($releases | Measure-Object zip -Sum).Sum }
    views14d = [pscustomobject]@{ total = $views.count; unique = $views.uniques }
    clones14d = [pscustomobject]@{ total = $clones.count; unique = $clones.uniques }
    site = $site
    referrers = $referrers | ForEach-Object { [pscustomobject]@{ source = $_.referrer; views = $_.count; unique = $_.uniques } }
    releases = $releases
}
if ($Json) { $result | ConvertTo-Json -Depth 5; return }
"Codex Tracker · $Repo"
"Étoiles $($result.stars) · forks $($result.forks) · abonnés $($result.watchers)"
"Téléchargements : installateur $($result.downloads.installer) · ZIP $($result.downloads.zip) (mises à jour automatiques incluses)"
"GitHub, 14 jours : $($result.views14d.total) vues ($($result.views14d.unique) visiteurs) · $($result.clones14d.total) clones"
if ($result.referrers) { "Sources :"; $result.referrers | ForEach-Object { "  $($_.source) : $($_.views) vues ($($_.unique))" } }
if ($site) {
    "Site, $($site.days) jours : $($site.visits) visites, $($site.pageViews) pages vues"
    if ($site.sources) { "  Sources : " + (($site.sources | ForEach-Object { "$($_.host) $($_.views)" }) -join ' · ') }
    if ($site.pages) { "  Pages : " + (($site.pages | ForEach-Object { "$($_.path) $($_.views)" }) -join ' · ') }
    if ($site.countries) { "  Pays : " + (($site.countries | ForEach-Object { "$($_.country) $($_.views)" }) -join ' · ') }
} else { "Site : jeton Cloudflare absent (voir l’aide du script) ; tableau : https://dash.cloudflare.com/$CloudflareAccount/web-analytics" }
"Dernières versions :"
$releases | Select-Object -First 8 | ForEach-Object { "  $($_.tag) ($($_.published)) : $($_.installer) installateur, $($_.zip) ZIP" }
