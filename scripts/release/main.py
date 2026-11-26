import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build.buildRelease import buildCleanRelease
from config.loadConfig import getSteamUsername, loadPublishConfig
from metadata.readVersion import readChangeNotes, readModVersion
from package.createArchive import createReleaseZip
from steam.uploadWorkshop import confirmUpload, runSteamUpload, showReleaseSummary
from steam.writeVdf import verifySteamCmdBinary, writeWorkshopVdf
from utils.console import console
from utils.paths import getRepoPaths


def parseArgs():
    parser = argparse.ArgumentParser(description="Build and publish Advanced SAR to Steam Workshop.")
    parser.add_argument("--dry-run", action="store_true", help="Build and generate VDF without uploading.")
    return parser.parse_args()


def runRelease():
    cliArgs = parseArgs()
    paths = getRepoPaths()
    publishConfig = loadPublishConfig(paths["publishConfigPath"])
    modVersion = readModVersion(paths["csprojPath"])
    changeNotes = readChangeNotes(paths["changelogPath"], modVersion)

    verifySteamCmdBinary(paths["steamCmdExe"])
    buildCleanRelease(paths, publishConfig["expectedModName"])
    zipPath = createReleaseZip(paths["stageDir"], paths["distDir"], modVersion)
    vdfPath = writeWorkshopVdf(
        paths["vdfPath"],
        publishConfig,
        paths["stageDir"],
        changeNotes,
        paths["repoRoot"],
    )

    showReleaseSummary(modVersion, publishConfig, paths["stageDir"], zipPath, changeNotes)

    if cliArgs.dry_run:
        console.print("[bold yellow]Dry run enabled. Skipping Steam Workshop upload.[/bold yellow]")
        return

    if not confirmUpload():
        console.print("[yellow]Upload cancelled.[/yellow]")
        return

    steamUsername = getSteamUsername(paths["localConfigPath"])
    runSteamUpload(paths["steamCmdExe"], steamUsername, vdfPath)


if __name__ == "__main__":
    try:
        runRelease()
    except Exception as error:
        console.print(f"[bold red]Release aborted:[/bold red] {error}")
        sys.exit(1)
