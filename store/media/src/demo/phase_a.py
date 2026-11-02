import argparse
import json
import subprocess
from pathlib import Path

import files as f

parser = argparse.ArgumentParser()
parser.add_argument("--project", default=r"E:\projects\vetka\store\demo")
parser.add_argument("--no-package", action="store_true")
arguments = parser.parse_args()

ROOT = Path(arguments.project)
TGZ = ROOT / "LocalPackages/dev.upwake.vetka-0.1.0.tgz"
SCRIPTS = ROOT / "Assets/Game/Scripts"

FINAL = {
    "Player/PlayerController.cs": f.PLAYER_V3,
    "Player/PlayerHealth.cs": f.PLAYER_HEALTH,
    "Gameplay/CameraFollow.cs": f.CAMERA_V2,
    "Enemies/EnemyAI.cs": f.ENEMY_V1,
    "Enemies/Spawner.cs": f.SPAWNER,
    "Enemies/BossFight.cs": f.BOSS,
    "Inventory/Item.cs": f.ITEM,
    "Inventory/Inventory.cs": f.INVENTORY,
    "UI/HealthBar.cs": f.HEALTH_BAR,
}


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


for relative, text in FINAL.items():
    write(SCRIPTS / relative, text)
write(ROOT / "Assets/Game/Docs/BossFight.md", f.DESIGN_NOTES)
write(ROOT / "Assets/Editor/DemoSetup.cs", f.SETUP)
write(ROOT / ".gitignore", f.GITIGNORE)

if not arguments.no_package:
    TGZ.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["git", "-C", r"E:\projects\vetka", "archive", "--format=tar.gz", "--prefix=package/",
                    "-o", str(TGZ), "v0.1.0:Packages/dev.upwake.vetka"], check=True)

    manifest = ROOT / "Packages/manifest.json"
    data = json.loads(manifest.read_text(encoding="utf-8"))
    data["dependencies"]["dev.upwake.vetka"] = "file:" + TGZ.as_posix()
    manifest.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
print("phase A written")
