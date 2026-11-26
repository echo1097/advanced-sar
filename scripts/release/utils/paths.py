from pathlib import Path


def getRepoPaths():
    repoRoot = Path(__file__).resolve().parents[3]

    return {
        "repoRoot": repoRoot,
        "csprojPath": repoRoot / "src" / "AdvancedSar" / "AdvancedSar.csproj",
        "changelogPath": repoRoot / "CHANGELOG.md",
        "distDir": repoRoot / "dist",
        "stageDir": repoRoot / "dist" / "AdvancedSAR",
        "vdfPath": repoRoot / "dist" / "workshop_upload.vdf",
        "dotnetExe": repoRoot / ".tools" / "dotnet" / "dotnet.exe",
        "dotnetRoot": repoRoot / ".tools" / "dotnet",
        "dotnetHome": repoRoot / ".tools" / "dotnet-home",
        "steamCmdExe": repoRoot / ".tools" / "steamcmd" / "steamcmd.exe",
        "publishConfigPath": repoRoot / "workshop" / "publish.json",
        "localConfigPath": repoRoot / "workshop" / "local.json",
    }
