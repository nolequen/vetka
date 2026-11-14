import argparse
import pathlib
import sys
import xml.etree.ElementTree as ElementTree

GIT_MISSING = "Git is not installed."

ALLOWED_REASONS = {
    "Linux": [
        "Only Windows reports a file that another process holds open",
        "Only Windows keeps a file that is in use from being replaced",
    ],
    "macOS": [
        "Only Windows reports a file that another process holds open",
        "Only Windows keeps a file that is in use from being replaced",
    ],
    "Windows": [],
}

PASSED = "Passed"
FAILED = "Failed"
SKIPPED = "Skipped"
INCONCLUSIVE = "Inconclusive"


class Results:
    def __init__(self, cases, teardown_failures):
        self.cases = cases
        self.teardown_failures = teardown_failures

    @property
    def total(self):
        return len(self.cases)

    def count(self, result):
        return sum(1 for case in self.cases if case["result"] == result)


def message_of(case):
    for path in ("failure/message", "reason/message"):
        node = case.find(path)
        if node is not None and node.text and node.text.strip():
            return node.text.strip()
    return ""


def read_results(path):
    path = pathlib.Path(path)
    if not path.is_file():
        raise ValueError(f"{path} does not exist")
    if path.stat().st_size == 0:
        raise ValueError(f"{path} is empty")
    try:
        root = ElementTree.parse(path).getroot()
    except ElementTree.ParseError as error:
        raise ValueError(f"{path} is not valid XML: {error}") from error
    cases = []
    for case in root.iter("test-case"):
        cases.append({
            "name": case.get("fullname") or case.get("name") or "?",
            "result": case.get("result") or "?",
            "label": case.get("label") or "",
            "message": message_of(case),
        })
    teardown_failures = [
        f"{suite.get('fullname') or suite.get('name') or '?'}: {first_line(message_of(suite))}"
        for suite in root.iter("test-suite")
        if suite.get("result") == FAILED and suite.get("site") == "TearDown"
    ]
    return Results(cases, teardown_failures)


def first_line(text):
    return text.splitlines()[0].strip() if text else ""


def find_problems(results, os_name):
    allowed = ALLOWED_REASONS[os_name]
    problems = []
    expected_skips = []
    if results.total == 0:
        problems.append("The run reports 0 test cases.")
    for suite in results.teardown_failures:
        problems.append(f"Failed in a fixture teardown: {suite}")
    for case in results.cases:
        result = case["result"]
        if result == PASSED:
            continue
        message = case["message"]
        described = f"{result}{' (' + case['label'] + ')' if case['label'] else ''}: {case['name']}"
        if result == FAILED:
            problems.append(f"{described}: {first_line(message)}")
        elif result in (SKIPPED, INCONCLUSIVE):
            if GIT_MISSING in message:
                problems.append(f"{described}: git was not found ({first_line(message)})")
            elif any(reason in message for reason in allowed):
                expected_skips.append(f"{described}: {first_line(message)}")
            else:
                problems.append(f"{described}: unexpected, reason \"{first_line(message)}\"")
        else:
            problems.append(f"{described}: unknown result")
    return problems, expected_skips


def write_summary(summary_path, title, results, problems, expected_skips):
    lines = [f"### {title}", ""]
    if results is not None:
        lines += [
            "| Total | Passed | Failed | Skipped | Inconclusive |",
            "| ---: | ---: | ---: | ---: | ---: |",
            f"| {results.total} | {results.count(PASSED)} | {results.count(FAILED)} | "
            f"{results.count(SKIPPED)} | {results.count(INCONCLUSIVE)} |",
            "",
        ]
    lines.append("Result: **passed**" if not problems else f"Result: **failed**, {len(problems)} problem(s)")
    lines.append("")
    for heading, items in (("Problems", problems), ("Expected skips", expected_skips)):
        if items:
            lines += [f"{heading}:", ""]
            lines += [f"- `{item.replace('`', '')}`" for item in items[:100]]
            if len(items) > 100:
                lines.append(f"- and {len(items) - 100} more")
            lines.append("")
    text = "\n".join(lines) + "\n"
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(text)


def check(arguments):
    title = arguments.title or arguments.results
    try:
        results = read_results(arguments.results)
    except ValueError as error:
        problems = [str(error)]
        print(problems[0])
        write_summary(arguments.summary, title, None, problems, [])
        return 1
    problems, expected_skips = find_problems(results, arguments.os)
    print(f"total={results.total} passed={results.count(PASSED)} failed={results.count(FAILED)} "
          f"skipped={results.count(SKIPPED)} inconclusive={results.count(INCONCLUSIVE)}")
    for item in expected_skips:
        print(f"expected: {item}")
    for item in problems:
        print(f"PROBLEM: {item}")
    write_summary(arguments.summary, title, results, problems, expected_skips)
    return 1 if problems else 0


def totals(arguments):
    directory = pathlib.Path(arguments.directory)
    counts = {}
    folders = sorted(path for path in directory.iterdir() if path.is_dir()) if directory.is_dir() else []
    if not folders:
        print(f"{directory}: no results downloaded, the jobs themselves report it")
    for folder in folders:
        try:
            counts[folder.name] = read_results(folder / arguments.file).total
        except ValueError as error:
            print(f"{folder.name}: no results ({error}), the job itself reports it")
    for name, total in counts.items():
        print(f"{name}: {total} cases")
    distinct = sorted(set(counts.values()))
    lines = ["### Case totals", "", "| Job | Cases |", "| --- | ---: |"]
    lines += [f"| {name} | {total} |" for name, total in counts.items()]
    lines.append("")
    if len(distinct) > 1:
        lines.append(f"Result: **failed**, the jobs ran different numbers of cases: {', '.join(map(str, distinct))}")
        print(f"PROBLEM: the jobs ran different numbers of cases: {', '.join(map(str, distinct))}")
    else:
        lines.append("Result: **passed**")
    if arguments.summary:
        with open(arguments.summary, "a", encoding="utf-8") as summary:
            summary.write("\n".join(lines) + "\n")
    return 1 if len(distinct) > 1 else 0


def main():
    parser = argparse.ArgumentParser(description="Checks Unity Test Framework results.")
    commands = parser.add_subparsers(dest="command", required=True)
    check_parser = commands.add_parser("check", help="check the results of one job")
    check_parser.add_argument("results")
    check_parser.add_argument("--os", required=True, choices=sorted(ALLOWED_REASONS))
    check_parser.add_argument("--title", default="")
    check_parser.add_argument("--summary", default="")
    check_parser.set_defaults(handler=check)
    totals_parser = commands.add_parser("totals", help="compare the case totals of all jobs")
    totals_parser.add_argument("directory")
    totals_parser.add_argument("--file", default="results.xml")
    totals_parser.add_argument("--summary", default="")
    totals_parser.set_defaults(handler=totals)
    arguments = parser.parse_args()
    return arguments.handler(arguments)


if __name__ == "__main__":
    sys.exit(main())
