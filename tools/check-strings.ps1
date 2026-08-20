<#
.SYNOPSIS
    Controlla i cataloghi delle lingue (Resources\*.json) contro l'uso reale nel codice.

.DESCRIPTION
    Nel progetto nessuna stringa mostrata all'utente sta nel codice: si usa
    L.T("chiave") e il testo vive in Resources\<lingua>.json.
    Questo script verifica che:
      1) ogni chiave usata nel codice esista nel catalogo di riferimento (it);
      2) ogni altra lingua abbia le stesse chiavi del riferimento;
      3) i segnaposto ({0}, {1}, ...) coincidano fra le lingue.

    Esce con codice 1 se trova un problema, cosi' puo' fare da guardia in CI.
    Le chiavi presenti nel catalogo ma non referenziate staticamente vengono solo
    elencate (alcune si compongono a runtime, es. unit.b/unit.kb/...).

.EXAMPLE
    pwsh tools\check-strings.ps1
#>
[CmdletBinding()]
param(
    # Tutto src/: le chiavi si usano nell'applicazione Windows, nel core condiviso
    # e nel client Android, e un catalogo va verificato contro tutti e tre insieme.
    [string] $SourceDir = (Join-Path $PSScriptRoot '..\src'),
    # I cataloghi stanno nel core: una traduzione aggiunta vale per tutte le
    # piattaforme, non per una sola.
    [string] $ResourcesDir = (Join-Path $PSScriptRoot '..\src\NetClipboard.Core\Resources'),
    [string] $ReferenceLanguage = 'it'
)

$ErrorActionPreference = 'Stop'
$SourceDir = (Resolve-Path $SourceDir).Path
$resourcesDir = (Resolve-Path $ResourcesDir).Path
$problems = 0

function Read-Catalog([string] $path) {
    $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $map = @{}
    foreach ($p in $json.PSObject.Properties) { $map[$p.Name] = $p.Value }
    return $map
}

function Get-Placeholders([string] $text) {
    $found = [regex]::Matches($text, '\{(\d+)') | ForEach-Object { [int]$_.Groups[1].Value }
    return ($found | Sort-Object -Unique) -join ','
}

# --- chiavi usate nel codice -------------------------------------------------
$used = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem $SourceDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        Get-Content $_.FullName -Encoding UTF8 |
            Where-Object { $_ -match 'L\.T\(' } |
            ForEach-Object {
                foreach ($m in [regex]::Matches($_, '"([^"]+)"')) {
                    $v = $m.Groups[1].Value
                    if ($v -match '^[a-z][a-zA-Z]*\.[a-zA-Z]+$') { [void]$used.Add($v) }
                }
            }
    }

# --- catalogo di riferimento -------------------------------------------------
$refPath = Join-Path $resourcesDir "$ReferenceLanguage.json"
if (-not (Test-Path $refPath)) { throw "Catalogo di riferimento assente: $refPath" }
$reference = Read-Catalog $refPath

Write-Host "Catalogo '$ReferenceLanguage': $($reference.Count) chiavi - usate nel codice: $($used.Count)"

$missing = @($used | Where-Object { -not $reference.ContainsKey($_) } | Sort-Object)
if ($missing.Count) {
    $problems += $missing.Count
    Write-Host "ERRORE - chiavi usate nel codice ma assenti dal catalogo:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}

$unused = @($reference.Keys | Where-Object { -not $used.Contains($_) } | Sort-Object)
if ($unused.Count) {
    Write-Host "Nota - chiavi non referenziate staticamente (possono essere composte a runtime):"
    $unused | ForEach-Object { Write-Host "  - $_" }
}

# --- confronto fra le lingue -------------------------------------------------
Get-ChildItem $resourcesDir -Filter *.json |
    Where-Object { $_.BaseName -ne $ReferenceLanguage } |
    ForEach-Object {
        $lang = $_.BaseName
        $other = Read-Catalog $_.FullName
        Write-Host "Catalogo '$lang': $($other.Count) chiavi"

        $absent = @($reference.Keys | Where-Object { -not $other.ContainsKey($_) } | Sort-Object)
        if ($absent.Count) {
            $problems += $absent.Count
            Write-Host "ERRORE - '$lang' non traduce:" -ForegroundColor Red
            $absent | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        }

        $extra = @($other.Keys | Where-Object { -not $reference.ContainsKey($_) } | Sort-Object)
        if ($extra.Count) {
            $problems += $extra.Count
            Write-Host "ERRORE - '$lang' ha chiavi che il riferimento non conosce:" -ForegroundColor Red
            $extra | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        }

        foreach ($key in $reference.Keys) {
            if (-not $other.ContainsKey($key)) { continue }
            $a = Get-Placeholders $reference[$key]
            $b = Get-Placeholders $other[$key]
            if ($a -ne $b) {
                $problems++
                Write-Host "ERRORE - segnaposto diversi in '$key': $ReferenceLanguage={$a} $lang={$b}" -ForegroundColor Red
            }
        }
    }

if ($problems -gt 0) {
    Write-Host ""
    Write-Host "$problems problema/i trovato/i." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Cataloghi coerenti." -ForegroundColor Green
exit 0
