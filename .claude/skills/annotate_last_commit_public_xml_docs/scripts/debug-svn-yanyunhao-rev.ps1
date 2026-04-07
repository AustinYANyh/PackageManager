param(
  [string]$WcRoot = "E:/HongWaWorkSpace/MaxiBIMPMEP_trunk/HWTransMaster4PMEP",
  [string]$Author = "yanyunhao",
  [int]$History = 30,
  [string]$OnlyRevision = ""
)

$ErrorActionPreference = "Stop"

Push-Location -LiteralPath $WcRoot
try {
  $logText = svn log -l $History

  # svn log 的格式一般形如：
  # r112510 | author | 2026-04-01 ... | ...
  $revMatches = [regex]::Matches($logText, "r(\d+)\s*\|\s*$([regex]::Escape($Author))\s*\|")
  $revList = @()
  foreach ($m in $revMatches) {
    $revList += $m.Groups[1].Value
  }
  $revList = $revList | Sort-Object -Unique

  if (-not $revList -or $revList.Count -eq 0) {
    Write-Host "No revisions found for author=$Author in wcRoot=$WcRoot"
    return
  }

  foreach ($rev in $revList) {
    if (-not [string]::IsNullOrWhiteSpace($OnlyRevision) -and $rev -ne $OnlyRevision) {
      continue
    }
    $diffText = svn diff -c $rev --summarize
    Write-Host ("--- debug rev {0} ---" -f $rev)
    Write-Host ("diff type: {0}" -f ($diffText.GetType().FullName))
    if ($diffText -is [string]) {
      Write-Host ("diff first 200 chars: " + ($diffText.Substring(0, [Math]::Min(200, $diffText.Length))).Replace(\"`r\",\"\\r\").Replace(\"`n\",\"\\n\"))
    }

    $lines = @()
    if ($null -ne $diffText) {
      $lines = ($diffText -split "\r?\n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }
    Write-Host ("lines after split: {0}" -f $lines.Count)
    foreach ($line in $lines | Select-Object -First 10) {
      Write-Host ("line: [" + $line + "]")
    }

    $csLines = @()
    foreach ($line in $lines) {
      if ($line -match "\\.cs\\s*$") {
        $csLines += $line.Trim()
      }
    }

    Write-Host ("rev {0}: csChanged={1}" -f $rev, $csLines.Count)
    if ($csLines.Count -gt 0) {
      $csLines | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_) }
    }
    Write-Host "---"
  }
}
finally {
  Pop-Location
}

