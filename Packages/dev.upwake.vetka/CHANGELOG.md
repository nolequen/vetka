# Changelog

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this package
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-11-04

### Added

- Tags: create and delete them from the Log, which shows them, and push them.
- Force push with a lease in the Push window.
- An offer to update and push again when a push is rejected.
- Cherry-pick and revert from the Log, which can now show any branch.
- Compare a branch with the current one: commits on each side and files that differ.
- The history of a file or folder, Compare with Local, Get This Version, blame of a revision.
- Incoming Changes: what Update Project would bring, before running it.
- Update Project lists the branches and tags the fetch changed.
- Unity 2022.3 and Tuanjie 1.6 support; the branch in the main window title stays Unity 6.0 or
  newer.

### Changed

- Settings moved to Edit → Preferences → Vetka; Tools → Git → Settings... opens them there.
- The Log loads in portions as it scrolls.
- Git runs in English whatever the system language, so its messages are understood everywhere.
- A commit starts faster and shows its progress from the click.
- The commit message field can be resized with the mouse.

### Fixed

- Slashes in branch names in the branch menu.
- The commit message field stays empty after a commit made while it has focus.
- Double clicks on the Stash and Log buttons.
- Smaller defects found in a review of the whole code.

## [0.1.0] - 2026-10-05

First public release: Changes tab with commit and push, Update Project, branches, log, stash,
patches, diff and blame, Git status in the Project window.
