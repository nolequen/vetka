import argparse
import json
import re
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
STORE = HERE.parents[2]
VIDEO = STORE / "media" / "video"
FPS = 30
FLASH_SCENES = {"settings"}
PICTURE = 972
MARGIN = 0.3


def subtitles(path):
    scenes = {}
    current = None
    for line in path.read_text(encoding="utf-8").splitlines():
        heading = re.match(r"## \d+\. (\w+)", line)
        if heading:
            current = scenes.setdefault(heading.group(1), [])
        elif current is not None and line.strip().startswith("zh:"):
            current.append(line.strip()[3:].strip())
    return scenes


def start_time(raw):
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=start_time", "-of", "json", str(raw)],
                            capture_output=True, text=True, check=True)
    value = float(json.loads(result.stdout)["format"]["start_time"])
    if value < 1e9:
        raise SystemExit(f"{raw} has no wall-clock timestamps (start_time {value})")
    return value


def private_areas(events, origin):
    areas = []
    opened = None
    for event in events:
        at = event["time"] / 1000 - origin
        if event["event"] == "blur_begin":
            opened = (at - 0.2, event["rects"])
        elif event["event"] == "blur_end" and opened:
            areas.extend((opened[0], at + 0.4, rect) for rect in opened[1])
            opened = None
    return areas


def blur_chain(areas, hidden, margin=0):
    chain = []
    for index, (begin, end, (x, y, w, h)) in enumerate(areas):
        left, top = max(0, x - margin), max(0, y - margin)
        right, bottom = min(1920, x + w + margin), min(1080, y + h + margin)
        chain.append(f"[s{index}]split[m{index}][n{index}];[n{index}]crop={right - left}:{bottom - top}:{left}:{top},"
                     f"gblur=sigma=12[b{index}];[m{index}][b{index}]overlay={left}:{top}:"
                     f"enable='between(t,{begin:.3f},{end:.3f})'[s{index + 1}]")
    label = f"[s{len(areas)}]"
    if hidden:
        x, y, w, h, color = hidden
        spans = "+".join(f"between(t,{begin:.3f},{end:.3f})" for begin, end, _ in areas)
        enable = f"not({spans})" if spans else "1"
        chain.append(f"{label}drawbox=x={x}:y={y}:w={w}:h={h}:color=0x{color}@1:t=fill:enable='{enable}'[hidden]")
        label = "[hidden]"
    return chain, label


def plan(events, origin):
    scenes = []
    current = None
    cut_from = None
    for event in events:
        at = event["time"] / 1000 - origin
        if event["event"] == "scene_begin":
            current = {"name": event["scene"], "begin": at, "cuts": []}
        elif event["event"] == "cut_begin":
            cut_from = at
        elif event["event"] == "cut_end" and current is not None and cut_from is not None:
            if at - cut_from > 1:
                current["cuts"].append((cut_from + MARGIN, at - MARGIN))
            cut_from = None
        elif event["event"] == "scene_end" and current is not None:
            current["end"] = at
            spans = []
            position = current["begin"]
            for cut_begin, cut_end in current["cuts"]:
                spans.append((position, cut_begin))
                position = cut_end
            spans.append((position, current["end"]))
            current["spans"] = [(a, b) for a, b in spans if b - a > 0.05]
            scenes.append(current)
            current = None
    return scenes


def flashes(raw, begin, end, longest=0.5, jump=60):
    command = ["ffmpeg", "-hide_banner", "-i", str(raw), "-vf",
               f"setpts=PTS-STARTPTS,select='between(t,{begin:.3f},{end:.3f})',crop=900:500:500:100,format=gray,"
               "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"]
    output = subprocess.run(command, capture_output=True, text=True).stderr
    times = [float(value) for value in re.findall(r"pts_time:([0-9.]+)", output)]
    levels = [float(value) for value in re.findall(r"YAVG=([0-9.]+)", output)]
    found = []
    base = levels[0] if levels else 0
    start = None
    for at, level in zip(times, levels):
        if level - base > jump:
            start = at if start is None else start
        else:
            if start is not None and at - start <= longest:
                found.append((start - 1 / FPS, at + 1 / FPS))
            start = None
            base = level
    return found


def subtract(spans, holes):
    for hole_begin, hole_end in holes:
        result = []
        for a, b in spans:
            if hole_end <= a or hole_begin >= b:
                result.append((a, b))
                continue
            if hole_begin > a:
                result.append((a, hole_begin))
            if hole_end < b:
                result.append((hole_end, b))
        spans = result
    return [(a, b) for a, b in spans if b - a > 0.05]


def srt_time(seconds):
    millis = int(round(seconds * 1000))
    return f"{millis // 3600000:02}:{millis // 60000 % 60:02}:{millis // 1000 % 60:02},{millis % 1000:03}"


def write_srt(path, entries):
    blocks = []
    for index, (begin, end, text) in enumerate(entries, 1):
        blocks.append(f"{index}\n{srt_time(begin)} --> {srt_time(end)}\n{text}\n")
    path.write_text("\n".join(blocks), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--raw", default=str(VIDEO / "raw.mkv"))
    parser.add_argument("--timeline", default=str(VIDEO / "timeline.json"))
    parser.add_argument("--subtitles", default=str(STORE / "listing-cn" / "subtitles.md"))
    parser.add_argument("--card", default=str(STORE / "media" / "vetka-features-zh-1920x1080.png"))
    parser.add_argument("--card-seconds", type=float, default=5)
    parser.add_argument("--taskbar", type=int, default=0)
    parser.add_argument("--hide", default="8,48,64,24", help="x,y,w,h of the account button, in screen pixels")
    parser.add_argument("--hide-color", default="181818")
    parser.add_argument("--out", default=str(VIDEO / "vetka-setup-zh.mp4"))
    arguments = parser.parse_args()

    raw = Path(arguments.raw).resolve()
    events = json.loads(Path(arguments.timeline).read_text(encoding="utf-8"))
    origin = start_time(raw)
    out = Path(arguments.out).resolve()
    card_image = Path(arguments.card).resolve()
    lines = subtitles(Path(arguments.subtitles))
    scenes = plan(events, origin)
    for scene in scenes:
        if scene["name"] in FLASH_SCENES:
            holes = flashes(raw, scene["begin"], scene["end"])
            scene["spans"] = subtract(scene["spans"], holes)
            for a, b in holes:
                print(f"{scene['name']}: cut a flash {a:.2f}-{b:.2f}")
    hidden = (*(int(value) for value in arguments.hide.split(",")), arguments.hide_color) if arguments.hide else None
    blurs, blurred_label = blur_chain(private_areas(events, origin), hidden)
    if not scenes:
        raise SystemExit("no complete scenes in the timeline")

    spans = []
    entries = []
    clock = 0.0
    for scene in scenes:
        length = sum(b - a for a, b in scene["spans"])
        texts = lines.get(scene["name"], [])
        weights = [max(len(text), 1) for text in texts]
        position = clock
        for text, weight in zip(texts, weights):
            share = length * weight / sum(weights)
            entries.append((position, position + share, text))
            position += share
        spans.extend(scene["spans"])
        clock += length
        print(f"{scene['name']}: {scene['end'] - scene['begin']:.1f} s recorded, {length:.1f} s kept")
    for text in lines.get("end", []):
        entries.append((clock, clock + arguments.card_seconds, text))
    total = clock + arguments.card_seconds

    srt = VIDEO / "subtitles-zh.srt"
    write_srt(srt, entries)

    select = "+".join(f"between(t,{a:.3f},{b:.3f})" for a, b in spans)
    height = 1080 - arguments.taskbar
    video = ";".join([f"[0:v]setpts=PTS-STARTPTS,fps={FPS}[s0]", *blurs]) + \
        f";{blurred_label}select='{select}',setpts=N/{FPS}/TB,crop=1920:{height}:0:0"
    video += f",scale=-2:{PICTURE}:flags=lanczos,pad=1920:1080:(ow-iw)/2:0:color=0x15171a,format=yuv420p,setsar=1[recorded]"
    card = (f"[1:v]scale=-2:{PICTURE}:flags=lanczos,pad=1920:1080:(ow-iw)/2:0:color=0x15171a,fps={FPS},"
            f"format=yuv420p,setsar=1[card]")
    style = "FontName=Microsoft YaHei,FontSize=12,BorderStyle=1,Outline=0,Shadow=0,MarginV=8"
    joined = f"[recorded][card]concat=n=2:v=1:a=0[joined];[joined]subtitles={srt.name}:force_style='{style}'[out]"
    command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(raw),
               "-loop", "1", "-framerate", str(FPS), "-t", str(arguments.card_seconds), "-i", str(card_image),
               "-filter_complex", ";".join([video, card, joined]), "-map", "[out]",
               "-c:v", "libx264", "-preset", "slow", "-crf", "23", "-r", str(FPS), "-an", "-movflags", "+faststart",
               str(out)]
    subprocess.run(command, cwd=srt.parent, check=True)
    size = out.stat().st_size / 1024 / 1024
    print(f"{out}: {total:.1f} s, {size:.1f} MB")


if __name__ == "__main__":
    main()
