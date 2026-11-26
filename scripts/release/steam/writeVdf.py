import subprocess


def escapeVdfValue(rawText):
    escaped = rawText.replace("\\", "\\\\")
    escaped = escaped.replace('"', '\\"')
    escaped = escaped.replace("\r\n", "\n")
    return escaped


def verifySteamCmdBinary(steamCmdExe):
    if not steamCmdExe.exists():
        raise FileNotFoundError(f"SteamCMD not found at: {steamCmdExe}")

    escapedPath = str(steamCmdExe).replace("'", "''")
    psCommand = (
        f"$sig = Get-AuthenticodeSignature -FilePath '{escapedPath}'; "
        "Write-Output ($sig.Status.ToString() + '|' + $sig.SignerCertificate.Subject)"
    )

    sigResult = subprocess.run(
        ["powershell", "-NoProfile", "-Command", psCommand],
        capture_output=True,
        text=True,
    )

    if sigResult.returncode != 0:
        raise RuntimeError("Could not verify SteamCMD digital signature.")

    outputText = sigResult.stdout.strip()
    parts = outputText.split("|", 1)

    if len(parts) != 2:
        raise RuntimeError(f"Unexpected signature check output: {outputText}")

    sigStatus, sigSubject = parts[0].strip(), parts[1].strip()

    if sigStatus != "Valid" or "O=Valve" not in sigSubject:
        raise RuntimeError(
            f"Refusing to run SteamCMD because signature check failed: {sigStatus} ({sigSubject})"
        )


def writeWorkshopVdf(vdfPath, publishConfig, stageDir, changeNotes, repoRoot):
    lines = [
        '"workshopitem"',
        "{",
        f'    "appid"            "{escapeVdfValue(str(publishConfig["appId"]))}"',
        f'    "publishedfileid"  "{escapeVdfValue(str(publishConfig["publishedFileId"]))}"',
        f'    "contentfolder"    "{escapeVdfValue(str(stageDir.resolve()))}"',
    ]

    previewSetting = str(publishConfig.get("previewFile", "")).strip()

    if previewSetting:
        previewPath = (repoRoot / previewSetting).resolve()
        if not previewPath.exists():
            raise FileNotFoundError(f"Preview image not found: {previewPath}")
        lines.append(f'    "previewfile"      "{escapeVdfValue(str(previewPath))}"')

    lines.append(f'    "changenote"       "{escapeVdfValue(changeNotes)}"')
    lines.append("}")

    vdfPath.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return vdfPath
