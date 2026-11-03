import argparse
import ctypes
import json
import sys
import time
from contextlib import contextmanager
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
POINTS = HERE / "points.json"
STORE = HERE.parents[2]

user32 = ctypes.WinDLL("user32", use_last_error=True)

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
MF_BYPOSITION = 0x0400
GA_ROOT = 2
WM_CLOSE = 0x0010

VK = {"alt": 0x12, "enter": 0x0D, "escape": 0x1B, "tab": 0x09, "down": 0x28, "up": 0x26, "right": 0x27, "left": 0x25,
      "control": 0x11, "a": 0x41, "f8": 0x77, "back": 0x08}

ULONG_PTR = ctypes.c_size_t


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]


class INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("u", INPUTUNION)]


WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.GetMenu.argtypes = [wintypes.HWND]
user32.GetMenu.restype = wintypes.HMENU
user32.GetSubMenu.argtypes = [wintypes.HMENU, ctypes.c_int]
user32.GetSubMenu.restype = wintypes.HMENU
user32.GetMenuItemCount.argtypes = [wintypes.HMENU]
user32.GetMenuStringW.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.LPWSTR, ctypes.c_int, wintypes.UINT]
user32.GetMenuItemRect.argtypes = [wintypes.HWND, wintypes.HMENU, wintypes.UINT, ctypes.POINTER(wintypes.RECT)]
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
user32.GetAsyncKeyState.restype = ctypes.c_short
user32.WindowFromPoint.argtypes = [wintypes.POINT]
user32.WindowFromPoint.restype = wintypes.HWND
user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetAncestor.restype = wintypes.HWND
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.EnumChildWindows.argtypes = [wintypes.HWND, WNDENUMPROC, wintypes.LPARAM]
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.MoveWindow.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.BOOL]


def now_ms():
    return int(time.time() * 1000)


def send(*inputs):
    array = (INPUT * len(inputs))(*inputs)
    if user32.SendInput(len(inputs), array, ctypes.sizeof(INPUT)) != len(inputs):
        raise SystemExit(f"SendInput failed: {ctypes.get_last_error()}")


def mouse(flags):
    return INPUT(type=INPUT_MOUSE, u=INPUTUNION(mi=MOUSEINPUT(dwFlags=flags)))


def key(vk=0, scan=0, flags=0):
    return INPUT(type=INPUT_KEYBOARD, u=INPUTUNION(ki=KEYBDINPUT(wVk=vk, wScan=scan, dwFlags=flags)))


def cursor():
    point = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(point))
    return point.x, point.y


def move(x, y, seconds=0.45):
    start_x, start_y = cursor()
    steps = max(1, int(seconds * 60))
    for step in range(1, steps + 1):
        t = step / steps
        eased = t * t * (3 - 2 * t)
        user32.SetCursorPos(round(start_x + (x - start_x) * eased), round(start_y + (y - start_y) * eased))
        time.sleep(seconds / steps)


def move_along(x, y, sideways_first):
    start_x, start_y = cursor()
    corner = (x, start_y) if sideways_first else (start_x, y)
    if corner != (start_x, start_y) and corner != (x, y):
        move(*corner, seconds=0.3)
    move(x, y, seconds=0.3)


def click(x, y, right=False, double=False):
    move(x, y)
    time.sleep(0.15)
    down, up = (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP) if right else (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
    for _ in range(2 if double else 1):
        send(mouse(down), mouse(up))
        time.sleep(0.08)
    time.sleep(0.25)


def press(name, times=1, pause=0.12):
    for _ in range(times):
        send(key(VK[name]), key(VK[name], flags=KEYEVENTF_KEYUP))
        time.sleep(pause)


def chord(*names):
    send(*[key(VK[name]) for name in names])
    send(*[key(VK[name], flags=KEYEVENTF_KEYUP) for name in reversed(names)])
    time.sleep(0.12)


def type_text(text, pause=0.06):
    for character in text:
        units = character.encode("utf-16-le")
        for index in range(0, len(units), 2):
            code = int.from_bytes(units[index:index + 2], "little")
            send(key(scan=code, flags=KEYEVENTF_UNICODE), key(scan=code, flags=KEYEVENTF_UNICODE | KEYEVENTF_KEYUP))
        time.sleep(pause)


def window_text(hwnd):
    buffer = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buffer, 512)
    return buffer.value


def class_name(hwnd):
    buffer = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buffer, 256)
    return buffer.value


def process_id(hwnd):
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def top_windows():
    found = []

    def collect(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(WNDENUMPROC(collect), 0)
    return found


def children(hwnd):
    found = []

    def collect(child, _):
        found.append((class_name(child), window_text(child).strip()))
        return True

    user32.EnumChildWindows(hwnd, WNDENUMPROC(collect), 0)
    return found


def screen_rect(hwnd):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    return [rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top]


def private_rects(dialog):
    found = {}

    def collect(child, _):
        name = class_name(child)
        if name in ("SysTreeView32", "Address Band Root", "msctls_progress32") and name not in found and                 user32.IsWindowVisible(child):
            found[name] = screen_rect(child)
        return True

    user32.EnumChildWindows(dialog, WNDENUMPROC(collect), 0)
    if "SysTreeView32" not in found:
        return [screen_rect(dialog)]
    rects = [found["SysTreeView32"]]
    band, field = found.get("Address Band Root"), found.get("msctls_progress32")
    if band and field and band[0] <= field[0] and band[1] <= field[1] and             field[0] + field[2] <= band[0] + band[2] and field[1] + field[3] <= band[1] + band[3]:
        rects.append(field)
    elif band:
        rects.append(band)
    return rects


def menu_text(menu, index):
    buffer = ctypes.create_unicode_buffer(256)
    user32.GetMenuStringW(menu, index, buffer, 256, MF_BYPOSITION)
    return buffer.value.split("\t")[0].replace("&", "").strip()


def menu_index(menu, name):
    names = [menu_text(menu, index) for index in range(user32.GetMenuItemCount(menu))]
    if name not in names:
        raise SystemExit(f"menu item {name!r} not found in {names}")
    return names.index(name)


def item_rect(hwnd, menu, index):
    rect = wintypes.RECT()
    if not user32.GetMenuItemRect(hwnd, menu, index, ctypes.byref(rect)) or rect.right <= rect.left:
        return None
    return (rect.left + rect.right) // 2, (rect.top + rect.bottom) // 2


class Driver:
    def __init__(self, project, timeline):
        self.project = Path(project)
        self.state_path = self.project / "Temp" / "Video.state.json"
        self.timeline_path = Path(timeline)
        self.events = []
        self.points = json.loads(POINTS.read_text(encoding="utf-8")) if POINTS.exists() else {}
        self.main = self.find_main()
        self.pid = process_id(self.main)
        self.seen = None

    def find_main(self):
        name = self.project.name.lower()
        windows = top_windows()
        for hwnd in windows:
            if user32.GetMenu(hwnd) and name in window_text(hwnd).lower():
                return hwnd
        candidates = "\n".join(f"  {class_name(hwnd)}: {window_text(hwnd)}" for hwnd in windows if window_text(hwnd))
        raise SystemExit(f"no editor main window for {self.project}, visible windows:\n{candidates}")

    def mark(self, event, scene, at=None, **extra):
        self.events.append({"time": at or now_ms(), "event": event, "scene": scene, **extra})
        self.timeline_path.parent.mkdir(parents=True, exist_ok=True)
        self.timeline_path.write_text(json.dumps(self.events, indent=1), encoding="utf-8")

    @contextmanager
    def scene(self, name):
        self.mark("scene_begin", name)
        yield
        self.mark("scene_end", name)

    @contextmanager
    def cut(self, scene):
        self.mark("cut_begin", scene)
        yield
        self.mark("cut_end", scene)

    def state(self):
        for _ in range(20):
            try:
                return json.loads(self.state_path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                time.sleep(0.05)
        return None

    def idle(self, state):
        return state is not None and now_ms() - state["time"] < 1500 and \
            not (state["compiling"] or state["updating"] or state["progress"])

    def check_dialogs(self):
        for hwnd in top_windows():
            if class_name(hwnd) != "#32770" or process_id(hwnd) != self.pid:
                continue
            controls = children(hwnd)
            classes = {name for name, _ in controls}
            if "Button" in classes and "msctls_progress32" not in classes:
                texts = " | ".join(text for _, text in controls if text)
                raise SystemExit(f"editor dialog: {window_text(hwnd)}: {texts}")

    def wait_idle(self, timeout=300, settle=1.5):
        deadline = time.time() + timeout
        calm_since = None
        while time.time() < deadline:
            state = self.state()
            if self.idle(state):
                calm_since = calm_since or time.time()
                if time.time() - calm_since >= settle:
                    self.seen = state["started"]
                    return
            else:
                calm_since = None
                if state is None or now_ms() - state["time"] > 3000:
                    self.check_dialogs()
            time.sleep(0.1)
        raise SystemExit(f"editor still busy after {timeout} s")

    def wait_busy(self, timeout=15):
        deadline = time.time() + timeout
        while time.time() < deadline:
            state = self.state()
            if not self.idle(state) or state["started"] != self.seen:
                return True
            time.sleep(0.05)
        return False

    def work(self, scene, timeout=300):
        with self.cut(scene):
            self.wait_busy()
            self.wait_idle(timeout)

    def has_window(self, type_name, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            state = self.state()
            if state and any(window["type"] == type_name for window in state["windows"]):
                return True
            time.sleep(0.1)
        return False

    def window(self, type_name, timeout=30):
        deadline = time.time() + timeout
        while time.time() < deadline:
            state = self.state()
            if state:
                rects = [window["rect"] for window in state["windows"] if window["type"] == type_name]
                if rects:
                    return rects[-1]
            time.sleep(0.1)
        raise SystemExit(f"window {type_name} did not appear in {timeout} s")

    def point(self, name, timeout=30):
        if name not in self.points:
            raise SystemExit(f"point {name!r} is not calibrated, run drive.py --calibrate")
        point = self.points[name]
        x, y, width, height = self.window(point["window"], timeout)
        base_x = x + width if "r" in point["corner"] else x
        base_y = y + height if "b" in point["corner"] else y
        return base_x + point["x"], base_y + point["y"]

    def click_point(self, name, right=False, double=False):
        self.focus()
        click(*self.point(name), right=right, double=double)

    def in_front(self):
        front = user32.GetForegroundWindow()
        return bool(front) and process_id(front) == self.pid

    def focus(self):
        if self.in_front():
            return
        send(key(VK["alt"]), key(VK["alt"], flags=KEYEVENTF_KEYUP))
        user32.SetForegroundWindow(self.main)
        deadline = time.time() + 2
        while not self.in_front():
            if time.time() > deadline:
                raise SystemExit("the editor is not in front, nothing was clicked")
            time.sleep(0.05)
        time.sleep(0.3)

    def menu(self, *path):
        self.focus()
        bar = user32.GetMenu(self.main)
        index = menu_index(bar, path[0])
        click(*item_rect(self.main, bar, index))
        menu = user32.GetSubMenu(bar, index)
        for depth, name in enumerate(path[1:], 1):
            time.sleep(0.35)
            index = menu_index(menu, name)
            last = depth == len(path) - 1
            target = None
            deadline = time.time() + 3
            while not target and time.time() < deadline:
                target = item_rect(None, menu, index)
                time.sleep(0.05)
            if not target:
                raise SystemExit(f"menu item {name!r} is not on screen")
            move_along(*target, sideways_first=depth > 1)
            if last:
                time.sleep(0.15)
                send(mouse(MOUSEEVENTF_LEFTDOWN), mouse(MOUSEEVENTF_LEFTUP))
                time.sleep(0.25)
            else:
                time.sleep(0.5)
            if not last:
                menu = user32.GetSubMenu(menu, index)

    def dialog(self, timeout=15):
        deadline = time.time() + timeout
        while time.time() < deadline:
            for hwnd in top_windows():
                if class_name(hwnd) == "#32770" and process_id(hwnd) == self.pid:
                    return hwnd
            time.sleep(0.1)
        raise SystemExit("no file dialog")

    def open(self, type_name, *path):
        if not self.has_window(type_name, timeout=0.5):
            self.menu(*path)
        return self.window(type_name)

    def root_of(self, type_name):
        x, y, width, height = self.window(type_name)
        return user32.GetAncestor(user32.WindowFromPoint(wintypes.POINT(x + width // 2, y + height // 2)), GA_ROOT)

    def enlarge(self, type_name, x=310, y=110, width=1300, height=820):
        hwnd = self.root_of(type_name)
        if hwnd and hwnd != self.main:
            user32.MoveWindow(hwnd, x, y, width, height, True)
        time.sleep(0.8)

    def close(self, type_name):
        hwnd = self.root_of(type_name)
        if hwnd and hwnd != self.main:
            user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        time.sleep(0.5)


CHANGES = "Upwake.Vetka.ChangesWindow"
PUSH = "Upwake.Vetka.PushWindow"
INCOMING = "Upwake.Vetka.IncomingChangesWindow"
STRATEGY = "Upwake.Vetka.UpdateProjectStrategyDialog"
BRANCHES = "Upwake.Vetka.BranchesWindow"
NEW_BRANCH = "Upwake.Vetka.NewBranchWindow"
LOG = "Upwake.Vetka.GitLogWindow"
DIFF = "Upwake.Vetka.DiffWindow"
BLAME = "Upwake.Vetka.BlameWindow"
IMPORT = "UnityEditor.PackageImport"
PREFERENCES = "UnityEditor.PreferenceSettingsWindow"

NEEDED = {
    "import.button": (IMPORT, "br", "the Import button"),
    "commit.file1": (CHANGES, "tl", "the checkbox of the first changed file"),
    "commit.file2": (CHANGES, "tl", "the checkbox of the second changed file"),
    "commit.file3": (CHANGES, "tl", "the checkbox of the meta file of the first one"),
    "commit.message": (CHANGES, "bl", "the commit message field"),
    "commit.button": (CHANGES, "br", "the Commit button"),
    "push.button": (PUSH, "br", "the Push button"),
    "incoming.update": (INCOMING, "br", "the Update Project button"),
    "branches.search": (BRANCHES, "tl", "the search field"),
    "branches.new": (BRANCHES, "tl", "the New Branch... row, search empty"),
    "branches.first": (BRANCHES, "tl", "the first branch row while the search says 'main'"),
    "branches.menu.checkout": (BRANCHES, "tl", "Checkout in the menu of that row"),
    "branches.menu.merge": (BRANCHES, "tl", "Merge ... into ... in the menu of that row, search 'fix/spawn'"),
    "log.commit": (LOG, "tl", "a commit row in the Log"),
    "log.file": (LOG, "tl", "a file of that commit"),
    "changes.file": (CHANGES, "tl", "a changed script in the Changes tab"),
    "changes.menu.diff": (CHANGES, "tl", "Show Diff in its context menu"),
    "changes.menu.blame": (CHANGES, "tl", "Blame in its context menu"),
}


def scene_import(d, package):
    with d.scene("import"):
        d.menu("Assets", "Import Package", "Custom Package...")
        dialog = d.dialog()
        opened = now_ms()
        time.sleep(0.5)
        d.mark("blur_begin", "import", at=opened, rects=private_rects(dialog))
        chord("control", "a")
        type_text(str(package), pause=0.02)
        time.sleep(0.4)
        press("enter")
        deadline = time.time() + 10
        while user32.IsWindowVisible(dialog) and time.time() < deadline:
            time.sleep(0.05)
        d.mark("blur_end", "import")
        d.window(IMPORT, timeout=60)
        time.sleep(2)
        d.click_point("import.button")
        d.work("import", timeout=600)


def scene_settings(d):
    with d.scene("settings"):
        d.menu("Tools", "Git", "Settings...")
        d.window(PREFERENCES)
        time.sleep(5)
        d.close(PREFERENCES)


def scene_commit(d, message):
    with d.scene("commit"):
        d.open(CHANGES, "Tools", "Git", "Commit...")
        time.sleep(1.5)
        d.click_point("commit.file1")
        d.click_point("commit.file3")
        d.click_point("commit.file2")
        d.click_point("commit.message")
        type_text(message, pause=0.15)
        time.sleep(1)
        d.click_point("commit.button")
        d.work("commit")
        time.sleep(2)


def scene_push(d):
    with d.scene("push"):
        d.menu("Tools", "Git", "Push...")
        d.window(PUSH)
        time.sleep(2.5)
        d.click_point("push.button")
        d.work("push")
        time.sleep(1.5)


def scene_update(d):
    with d.scene("update"):
        d.menu("Tools", "Git", "Incoming Changes...")
        d.window(INCOMING)
        d.work("update")
        time.sleep(4)
        d.click_point("incoming.update")
        if d.has_window(STRATEGY, timeout=3):
            time.sleep(1.5)
            press("enter")
        d.work("update", timeout=600)
        time.sleep(2.5)
        d.close(INCOMING)


def branches(d, search):
    d.open(BRANCHES, "Tools", "Git", "Branches...")
    time.sleep(1)
    d.click_point("branches.search")
    chord("control", "a")
    press("back")
    type_text(search, pause=0.08)
    time.sleep(1)


def scene_branches(d, branch, other):
    with d.scene("branches"):
        branches(d, "")
        d.click_point("branches.new")
        d.window(NEW_BRANCH)
        time.sleep(0.5)
        type_text(branch, pause=0.08)
        time.sleep(0.5)
        press("enter")
        d.work("branches")
        time.sleep(1.5)
        branches(d, "main")
        d.click_point("branches.first", right=True)
        time.sleep(0.6)
        d.click_point("branches.menu.checkout")
        d.work("branches")
        time.sleep(1.5)
        branches(d, other)
        d.click_point("branches.first", right=True)
        time.sleep(0.6)
        d.click_point("branches.menu.merge")
        d.work("branches", timeout=600)
        time.sleep(2)
        d.close(BRANCHES)


def scene_log(d):
    with d.scene("log"):
        d.menu("Tools", "Git", "Log...")
        d.window(LOG)
        d.work("log")
        time.sleep(1.5)
        d.click_point("log.commit")
        time.sleep(1.5)
        d.click_point("log.file")
        time.sleep(3)
        d.close(LOG)


def scene_diff(d):
    with d.scene("diff"):
        d.open(CHANGES, "Tools", "Git", "Commit...")
        time.sleep(1)
        d.click_point("changes.file", right=True)
        time.sleep(0.6)
        d.click_point("changes.menu.diff")
        d.window(DIFF)
        d.enlarge(DIFF)
        time.sleep(4)
        d.close(DIFF)


def scene_blame(d):
    with d.scene("blame"):
        d.open(CHANGES, "Tools", "Git", "Commit...")
        time.sleep(1)
        d.click_point("changes.file", right=True)
        time.sleep(0.6)
        d.click_point("changes.menu.blame")
        d.window(BLAME)
        d.enlarge(BLAME)
        d.work("blame")
        time.sleep(4)


def calibrate(d, names):
    print("Hover over each control and press F8; Escape skips it.")
    for name in names:
        type_name, corner, hint = NEEDED[name]
        print(f"{name}: {hint} ({type_name})", flush=True)
        while True:
            if user32.GetAsyncKeyState(VK["escape"]) & 0x8000:
                print("  skipped")
                time.sleep(0.4)
                break
            if user32.GetAsyncKeyState(VK["f8"]) & 0x8000:
                x, y = cursor()
                left, top, width, height = d.window(type_name, timeout=2)
                base_x = left + width if "r" in corner else left
                base_y = top + height if "b" in corner else top
                d.points[name] = {"window": type_name, "corner": corner, "x": x - base_x, "y": y - base_y}
                POINTS.write_text(json.dumps(d.points, indent=2) + "\n", encoding="utf-8")
                print(f"  {d.points[name]}")
                time.sleep(0.4)
                break
            time.sleep(0.03)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", default=str(STORE / "demo-cn"))
    parser.add_argument("--timeline", default=str(STORE / "media" / "video" / "timeline.json"))
    parser.add_argument("--package", default=str(STORE / "releases" / "vetka-0.2.0-tuanjie.unitypackage"))
    parser.add_argument("--message", default="添加冲刺技能")
    parser.add_argument("--branch", default="feature/存档")
    parser.add_argument("--merge", default="fix/spawn-rate")
    parser.add_argument("--scenes", default="import,settings,commit,update,push,branches,log,diff,blame")
    parser.add_argument("--calibrate", nargs="*")
    arguments = parser.parse_args()

    d = Driver(arguments.project, arguments.timeline)
    if arguments.calibrate is not None:
        calibrate(d, arguments.calibrate or [name for name in NEEDED if name not in d.points])
        return

    d.wait_idle(timeout=120)
    scenes = {
        "import": lambda: scene_import(d, Path(arguments.package)),
        "settings": lambda: scene_settings(d),
        "commit": lambda: scene_commit(d, arguments.message),
        "push": lambda: scene_push(d),
        "update": lambda: scene_update(d),
        "branches": lambda: scene_branches(d, arguments.branch, arguments.merge),
        "log": lambda: scene_log(d),
        "diff": lambda: scene_diff(d),
        "blame": lambda: scene_blame(d),
    }
    for name in arguments.scenes.split(","):
        scenes[name]()
        time.sleep(1)
    print(f"timeline: {d.timeline_path}")


if __name__ == "__main__":
    sys.exit(main())
