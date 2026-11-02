import argparse
import os
import re
import shutil
import subprocess
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
STORE = HERE.parents[2]
SKIP = ["Library", "Temp", "Logs", "UserSettings", "obj"]
DEFAULT_LAYOUT = Path(r"F:\tuanjie\installs\2022.3.61t15\Editor\Data\Resources\Layouts\Default.wlt")
TEAMMATE = ("Li Wei", "li.wei@skyward.example")
INCOMING = [
    ("Assets/Game/Materials/Enemy.mat", "{r: 0.93, g: 0.55, b: 0.18, a: 1}", "调整敌人材质颜色", 25),
    ("Assets/Game/Materials/Ground.mat", "{r: 0.28, g: 0.36, b: 0.26, a: 1}", "地面颜色调暗一些", 12),
]


def mirror(source, target):
    skipped = [str(Path(root) / name) for root in (source, target) for name in SKIP]
    result = subprocess.run(["robocopy", str(source), str(target), "/MIR", "/R:2", "/W:1", "/NJH", "/NJS", "/NP",
                             "/NDL", "/NFL", "/XD", *skipped], capture_output=True, text=True)
    if result.returncode >= 8:
        raise SystemExit(f"robocopy {source} -> {target} failed ({result.returncode}):\n{result.stdout}")


def git(directory, *args, author=None, minutes_ago=None):
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0")
    if author:
        env.update(GIT_AUTHOR_NAME=author[0], GIT_AUTHOR_EMAIL=author[1],
                   GIT_COMMITTER_NAME=author[0], GIT_COMMITTER_EMAIL=author[1])
    if minutes_ago is not None:
        stamp = f"{int(time.time()) - minutes_ago * 60} +0800"
        env.update(GIT_AUTHOR_DATE=stamp, GIT_COMMITTER_DATE=stamp)
    result = subprocess.run(["git", "-C", str(directory), *args], env=env, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed in {directory}:\n{result.stdout}\n{result.stderr}")
    return result.stdout.strip()


def editor_open(project):
    for name in ("TuanjieLockfile", "UnityLockfile"):
        lock = project / "Temp" / name
        if not lock.exists():
            continue
        try:
            with open(lock, "r+b"):
                pass
        except OSError:
            return True
    return False


def forget_editor_state(project):
    for pattern in ("UserSettings/Vetka*.asset", "UserSettings/Layouts/*.dwlt", "Library/CurrentLayout*.dwlt"):
        for file in project.glob(pattern):
            file.unlink()
    layouts = project / "UserSettings" / "Layouts"
    layouts.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(DEFAULT_LAYOUT, layouts / "default-2022.dwlt")


def add_incoming(remote):
    clone = Path(tempfile.mkdtemp(prefix="vetka-incoming-"))
    try:
        git(clone.parent, "clone", "-q", str(remote), str(clone))
        for path, color, message, minutes_ago in INCOMING:
            file = clone / path
            text = file.read_text(encoding="utf-8")
            changed, count = re.subn(r"- _Color: \{[^}]*\}", f"- _Color: {color}", text, count=1)
            if count != 1:
                raise SystemExit(f"no _Color in {path}")
            file.write_text(changed, encoding="utf-8", newline="\n")
            git(clone, "commit", "-q", "-am", message, author=TEAMMATE, minutes_ago=minutes_ago)
        git(clone, "push", "-q", "origin", "main")
    finally:
        shutil.rmtree(clone, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", default=str(STORE / "demo-cn"))
    parser.add_argument("--snapshot", default=str(STORE / "media" / "video" / "snapshot"))
    parser.add_argument("--save", action="store_true")
    arguments = parser.parse_args()

    project = Path(arguments.project)
    remote = project.parent / (project.name + "-remote.git")
    snapshot = Path(arguments.snapshot)
    if arguments.save:
        if (project / "Assets" / "Vetka").exists():
            raise SystemExit("the demo already has Assets/Vetka, save the snapshot before the first import")
        mirror(project, snapshot / "project")
        mirror(remote, snapshot / "remote")
        print(f"snapshot saved: {snapshot}")
        return

    if not (snapshot / "project" / ".git").exists():
        raise SystemExit(f"no snapshot in {snapshot}, run reset.py --save first")
    if editor_open(project):
        raise SystemExit(f"close the editor on {project} first")
    mirror(snapshot / "project", project)
    mirror(snapshot / "remote", remote)
    mirror(HERE / "com.vetka.video", project / "Packages" / "com.vetka.video")
    forget_editor_state(project)
    add_incoming(remote)
    print(git(project, "status", "--short"))
    print(f"reset done, {len(INCOMING)} incoming commits on {remote.name}")


if __name__ == "__main__":
    main()
