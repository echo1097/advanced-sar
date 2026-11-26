import zipfile


def createReleaseZip(stageDir, distDir, modVersion):
    zipPath = distDir / f"AdvancedSAR-{modVersion}.zip"

    if zipPath.exists():
        zipPath.unlink()

    with zipfile.ZipFile(zipPath, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for filePath in sorted(stageDir.rglob("*")):
            if filePath.is_file():
                relativePath = filePath.relative_to(stageDir.parent)
                archive.write(filePath, arcname=str(relativePath))

    return zipPath
