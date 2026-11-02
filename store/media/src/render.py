import subprocess
import sys
from pathlib import Path

from PIL import Image

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
SRC = Path(__file__).resolve().parent
OUT = SRC.parent

JOBS = {
    "icon": (160, 160, 4),
    "card": (420, 280, 4),
    "cover": (1950, 1300, 2),
    "promo": (1950, 1300, 2),
    "features-zh": (1920, 1080, 2),
}


def render(name, width, height, scale):
    raw = SRC / f"{name}.raw.png"
    subprocess.run([
        CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
        f"--force-device-scale-factor={scale}", f"--window-size={width},{height}",
        f"--screenshot={raw}", (SRC / f"{name}.html").as_uri(),
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    image = Image.open(raw).convert("RGB").resize((width, height), Image.LANCZOS)
    target = OUT / f"vetka-{name}-{width}x{height}.png"
    image.save(target, optimize=True)
    raw.unlink()
    print(target, image.size)


for job in sys.argv[1:] or JOBS:
    render(job, *JOBS[job])
