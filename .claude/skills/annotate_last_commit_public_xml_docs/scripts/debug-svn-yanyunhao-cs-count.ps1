param(
  [string]$WcRoot = "E:/HongWaWorkSpace/MaxiBIMPMEP_trunk/HWTransMaster4PMEP",
  [string]$Author = "yanyunhao",
  [int]$History = 80
)

$ErrorActionPreference = "Stop"

Push-Location -LiteralPath $WcRoot
try {
  $logText = svn log -l $History

  # 形如：
  # r112510 | liweichun | 2026-04-01 ...
  $revList = [System.Collections.Generic.List[int]]::new()
  foreach ($line in ($logText -split "`r?`n")) {
    if ($line -match '^r(\d+)\s*\|\s*([^|]+)\s*\|') {
      $rev = [int]$Matches[1]
      $candAuthor = ($Matches[2]).Trim()
      if ($candAuthor -ieq $Author) {
        $revList.Add($rev) | Out-Null
      }
    }
  }

  $revList = ($revList | Sort-Object -Unique -Descending)
  Write-Host ("Found yanyunhao revisions: " + $revList.Count)

  foreach ($rev in $revList) {
    $diffText = svn diff -c $rev --summarize
    # 你可按需为某个 revision 打印调试信息
    $diffLines = @()
    if ($diffText -is [string]) {
      $diffLines = ($diffText -split "`r?`n")
    } else {
      $diffLines = @($diffText) | ForEach-Object {
        if ($_ -is [string]) { $_ } else { ($_ | Out-String) }
      }
    }
    $diffLines = $diffLines | Where-Object { $_ -and $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() }

    $csFiles = @()
    foreach ($l in $diffLines) {
      # 行示例：M       code\...Some.cs
      if ($l -match '\.cs\s*$') {
        $csFiles += $l
      }
    }

    Write-Host ("rev {0}: csChanged={1}" -f $rev, $csFiles.Count)
    if ($csFiles.Count -gt 0) {
      $csFiles | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) }
    }
    Write-Host "---"
  }
}
finally {
  Pop-Location
}

