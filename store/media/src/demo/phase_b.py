import os
import shutil
import subprocess
import uuid
from pathlib import Path

import files as f

ROOT = Path(r"E:\projects\vetka\store\demo")
REMOTE = Path(r"E:\projects\vetka\store\demo-remote.git")
S = "Assets/Game/Scripts/"
ALEX = ("Alex Morgan", "alex@skyward.example")
KATE = ("Kate Lin", "kate@skyward.example")


def git(*args, author=None, date=None):
    env = dict(os.environ)
    env["GIT_TERMINAL_PROMPT"] = "0"
    if author:
        env.update(GIT_AUTHOR_NAME=author[0], GIT_AUTHOR_EMAIL=author[1],
                   GIT_COMMITTER_NAME=author[0], GIT_COMMITTER_EMAIL=author[1])
    if date:
        env.update(GIT_AUTHOR_DATE=date, GIT_COMMITTER_DATE=date)
    result = subprocess.run(["git", "-C", str(ROOT), *args], env=env, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed:\n{result.stdout}\n{result.stderr}")
    return result.stdout.strip()


def write(relative, text):
    (ROOT / relative).write_text(text, encoding="utf-8", newline="\n")


def with_metas(paths):
    result = set()
    for path in paths:
        result.add(path)
        if path.startswith("Assets/"):
            result.add(path + ".meta")
            parent = Path(path).parent
            while parent.as_posix() != "Assets":
                result.add(parent.as_posix() + ".meta")
                parent = parent.parent
    return sorted(result)


def commit(message, author, date, paths):
    git("add", "--", *with_metas(paths))
    git("commit", "-q", "-m", message, author=author, date=date)


def folder_meta(relative):
    write(relative + ".meta",
          f"fileFormatVersion: 2\nguid: {uuid.uuid4().hex}\nfolderAsset: yes\nDefaultImporter:\n"
          "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


if (ROOT / ".git").exists():
    raise SystemExit("demo repository already exists")
if REMOTE.exists():
    shutil.rmtree(REMOTE)
subprocess.run(["git", "init", "-q", "--bare", "-b", "main", str(REMOTE)], check=True)

git("init", "-q", "-b", "main")
git("config", "core.autocrlf", "false")
git("remote", "add", "origin", str(REMOTE))

final_player = (ROOT / (S + "Player/PlayerController.cs")).read_text(encoding="utf-8")
final_camera = (ROOT / (S + "Gameplay/CameraFollow.cs")).read_text(encoding="utf-8")

commit("Initial project setup", ALEX, "2026-09-14T10:12:00+02:00",
       [".gitignore", "Packages/manifest.json", "Packages/packages-lock.json", "ProjectSettings"])

write(S + "Player/PlayerController.cs", f.PLAYER_V1)
commit("Add player movement", ALEX, "2026-09-15T11:40:00+02:00",
       [S + "Player/PlayerController.cs", "Assets/Game/Prefabs/Player.prefab", "Assets/Game/Materials/Player.mat",
        "Assets/Game/Materials/Ground.mat", "Assets/Game/Scenes/Main.unity"])

write(S + "Gameplay/CameraFollow.cs", f.CAMERA_V1)
commit("Add camera follow", KATE, "2026-09-16T15:05:00+02:00", [S + "Gameplay/CameraFollow.cs"])

commit("Add enemy AI and spawner", KATE, "2026-09-18T09:30:00+02:00",
       [S + "Enemies/EnemyAI.cs", S + "Enemies/Spawner.cs", "Assets/Game/Prefabs/Enemy.prefab",
        "Assets/Game/Materials/Enemy.mat"])

write(S + "Player/PlayerController.cs", f.PLAYER_V2)
commit("Add player health and HUD", ALEX, "2026-09-21T13:20:00+02:00",
       [S + "Player/PlayerHealth.cs", S + "UI/HealthBar.cs", S + "Player/PlayerController.cs"])

write(S + "Player/PlayerController.cs", final_player)
commit("Tune jump and gravity", KATE, "2026-09-23T17:45:00+02:00", [S + "Player/PlayerController.cs"])
git("push", "-q", "-u", "origin", "main")

git("switch", "-q", "-c", "feature/inventory")
commit("Add inventory and items", KATE, "2026-09-24T10:15:00+02:00",
       [S + "Inventory/Inventory.cs", S + "Inventory/Item.cs", "Assets/Game/Items"])
git("push", "-q", "-u", "origin", "feature/inventory")

git("switch", "-q", "main")
commit("Add main menu scene", ALEX, "2026-09-25T12:00:00+02:00", ["Assets/Game/Scenes/Menu.unity"])
git("push", "-q", "origin", "main")
git("push", "-q", "origin", "main:release/0.3")

git("switch", "-q", "-c", "fix/spawn-rate")
spawner = (ROOT / (S + "Enemies/Spawner.cs")).read_text(encoding="utf-8")
write(S + "Enemies/Spawner.cs", spawner.replace("interval = 4f", "interval = 3f"))
commit("Spawn enemies more often", KATE, "2026-09-26T16:30:00+02:00", [S + "Enemies/Spawner.cs"])

git("switch", "-q", "main")
git("merge", "-q", "--no-ff", "feature/inventory", "-m", "Merge branch 'feature/inventory'",
    author=KATE, date="2026-09-28T11:10:00+02:00")

write(S + "Gameplay/CameraFollow.cs", final_camera)
commit("Fix camera jitter", ALEX, "2026-09-30T14:25:00+02:00", [S + "Gameplay/CameraFollow.cs"])

write(S + "Enemies/Spawner.cs", spawner.replace("maxAlive = 6", "maxAlive = 10"))
git("stash", "push", "-q", "-m", "WIP: spawner tuning", author=ALEX, date="2026-10-01T09:00:00+02:00")

write(S + "Player/PlayerController.cs", f.PLAYER_V4)
write(S + "Enemies/EnemyAI.cs", f.ENEMY_V2)
(ROOT / (S + "Camera")).mkdir(exist_ok=True)
folder_meta(S + "Camera")
for suffix in ("", ".meta"):
    shutil.move(str(ROOT / (S + "Gameplay/CameraFollow.cs" + suffix)), str(ROOT / (S + "Camera/CameraFollow.cs" + suffix)))
git("add", "--", S + "Enemies/BossFight.cs", S + "Enemies/BossFight.cs.meta")

print(git("log", "--oneline", "--graph", "--all"))
print(git("status", "--short"))
