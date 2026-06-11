param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$NoBuild,
    [int]$RepairSeed = 610,
    [int]$FullRebuildSeed = 611,
    [string]$NativeDllPath
)

$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
}

function Resolve-MSBuild {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw "MSBuild.exe was not found."
}

function Assert-Equal {
    param(
        [string]$Name,
        $Actual,
        $Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Name expected '$Expected' but was '$Actual'."
    }
}

function Invoke-NativeRaceHook {
    param(
        [type]$InteropType,
        [string]$Action,
        [int]$Seed
    )

    $request = @{ action = $Action; seed = $Seed } | ConvertTo-Json -Compress
    $json = $InteropType.GetMethod("InvokeTestControl").Invoke($null, [object[]]@([string]$request))
    Write-Host "$Action => $json"
    return $json | ConvertFrom-Json
}

$repoRoot = Resolve-RepoRoot
Set-Location $repoRoot

if (!$NativeDllPath) {
    $NativeDllPath = Join-Path $repoRoot "Tools\MftScanner.Native\bin\$Configuration\$Platform\MftScanner.Native.dll"
}

if (!$NoBuild) {
    $msbuild = Resolve-MSBuild
    & $msbuild (Join-Path $repoRoot "Tools\MftScanner.Native\MftScanner.Native.vcxproj") "/p:Configuration=$Configuration" "/p:Platform=$Platform" /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed with exit code $LASTEXITCODE."
    }
}

if (!(Test-Path $NativeDllPath)) {
    throw "Native DLL was not found: $NativeDllPath"
}

$resolvedDllPath = (Resolve-Path $NativeDllPath).Path
$dllLiteral = $resolvedDllPath -replace '"', '""'
$typeName = "NativeMftRaceHookSmoke_" + [Guid]::NewGuid().ToString("N")
$previousTestHooks = [Environment]::GetEnvironmentVariable("PM_MFT_INDEX_TEST_HOOKS", "Process")

$source = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class $typeName
{
    [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_create")]
    private static extern IntPtr Create(IntPtr statusCallback, IntPtr changeCallback, IntPtr userData);

    [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_test_control")]
    private static extern int TestControl(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

    [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_free_string")]
    private static extern void FreeString(IntPtr value);

    [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_shutdown")]
    private static extern void Shutdown(IntPtr handle);

    [DllImport(@"$dllLiteral", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_destroy")]
    private static extern void Destroy(IntPtr handle);

    public static string InvokeTestControl(string requestJson)
    {
        IntPtr handle = Create(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("pm_index_create returned null.");
        }

        IntPtr request = Utf8(requestJson);
        IntPtr response = IntPtr.Zero;
        try
        {
            int ok = TestControl(handle, request, out response);
            string json = ReadUtf8(response);
            if (ok == 0)
            {
                throw new InvalidOperationException("pm_index_test_control failed: " + json);
            }

            return json;
        }
        finally
        {
            if (response != IntPtr.Zero)
            {
                FreeString(response);
            }

            if (request != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(request);
            }

            Shutdown(handle);
            Destroy(handle);
        }
    }

    private static IntPtr Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static string ReadUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
        {
            length++;
        }

        byte[] bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }
}
"@

try {
    [Environment]::SetEnvironmentVariable("PM_MFT_INDEX_TEST_HOOKS", "1", "Process")
    $interopType = Add-Type -TypeDefinition $source -PassThru |
        Where-Object { $_.Name -eq $typeName } |
        Select-Object -First 1

    if ($null -eq $interopType) {
        throw "Failed to compile native hook interop type."
    }

    $repair = Invoke-NativeRaceHook -InteropType $interopType -Action "simulateRepairOverlayDeleteRace" -Seed $RepairSeed
    Assert-Equal "repair.success" $repair.success $true
    Assert-Equal "repair.overlayDeletesSeen" $repair.overlayDeletesSeen 1
    Assert-Equal "repair.overlayDeletesAbsorbed" $repair.overlayDeletesAbsorbed 0
    Assert-Equal "repair.overlayDeletesRetained" $repair.overlayDeletesRetained 1
    Assert-Equal "repair.baselineSuppressedByOverlay" $repair.baselineSuppressedByOverlay 1
    Assert-Equal "repair.mutatesLiveIndex" $repair.mutatesLiveIndex $false

    $full = Invoke-NativeRaceHook -InteropType $interopType -Action "simulateFullRebuildEnumerationDeleteRace" -Seed $FullRebuildSeed
    Assert-Equal "full.success" $full.success $true
    Assert-Equal "full.changed" $full.changed $true
    Assert-Equal "full.remainingRecords" $full.remainingRecords 0
    Assert-Equal "full.volumeRecordCount" $full.volumeRecordCount 0
    Assert-Equal "full.volumeMaxFrn" $full.volumeMaxFrn 0
    Assert-Equal "full.mutatesLiveIndex" $full.mutatesLiveIndex $false

    Write-Host "Native MFT index race hook validation passed."
}
finally {
    [Environment]::SetEnvironmentVariable("PM_MFT_INDEX_TEST_HOOKS", $previousTestHooks, "Process")
}
