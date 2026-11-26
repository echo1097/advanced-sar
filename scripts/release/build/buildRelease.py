import os
import re
import shutil
import subprocess
from utils.console import console


def buildCleanRelease(paths, expectedModName):
    stageDir = paths["stageDir"]

    if stageDir.exists():
        shutil.rmtree(stageDir)

    dotnetExe = paths["dotnetExe"]

    if not dotnetExe.exists():
        raise FileNotFoundError(f"Bundled dotnet SDK not found: {dotnetExe}")

    buildEnv = os.environ.copy()
    buildEnv["DOTNET_ROOT"] = str(paths["dotnetRoot"])
    buildEnv["DOTNET_CLI_HOME"] = str(paths["dotnetHome"])

    buildCommand = [
        str(dotnetExe),
        "build",
        str(paths["csprojPath"]),
        "-c",
        "Release",
        "--no-restore",
        "-p:CopyToDevMod=false",
    ]

    console.print("[bold cyan]Building clean Release package...[/bold cyan]")
    buildResult = subprocess.run(
        buildCommand,
        cwd=str(paths["repoRoot"]),
        env=buildEnv,
    )

    if buildResult.returncode != 0:
        raise RuntimeError("Release build failed.")

    verifyStagedRelease(stageDir, expectedModName)


def verifyStagedRelease(stageDir, expectedModName):
    requiredFiles = ["AdvancedSar.dll", "_info.ini", "CHANGELOG.md"]

    for fileName in requiredFiles:
        filePath = stageDir / fileName
        if not filePath.exists():
            raise FileNotFoundError(f"Expected staged file is missing: {filePath}")

    iniText = (stageDir / "_info.ini").read_text(encoding="utf-8")
    hasLanguage = re.search(r"(?im)^\s*\[Language_en\]\s*$", iniText) is not None
    expectedPattern = rf"(?im)^\s*Name\s*=\s*{re.escape(expectedModName)}\s*$"
    hasExpectedName = re.search(expectedPattern, iniText) is not None
    hasDevTag = "(DEV)" in iniText.upper()

    if not hasLanguage or not hasExpectedName or hasDevTag:
        raise ValueError(
            f"Staged _info.ini failed verification. Expected Name={expectedModName} without (DEV)."
        )
