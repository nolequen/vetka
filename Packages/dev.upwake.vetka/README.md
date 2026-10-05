# Vetka – Git for Unity

A Git client inside the Unity Editor, modelled on the version control integration of JetBrains IDEs
(IntelliJ IDEA, Rider). It runs the regular `git` command line, so it works with any repository and
hosting you already use.

## Features

- **Never left half-merged** – Update Project, merge, rebase, checkout and applying a stash either
  finish or are rolled back completely, with your local changes restored.
- **Changes tab** – a dockable tab with tracked changes and unversioned files, moves and renames
  shown as one row, *Commit* and *Commit and Push*, commit message history.
- **Update Project** – fetch, then merge or rebase the upstream branch.
- **Push** – outgoing commits, upstream set automatically for new branches.
- **Branches** – local, remote and recent branches with search and favourites; checkout, new
  branch, merge, rebase, delete.
- **Log** – recent commits and their files, *Amend last commit*, *Undo last commit*.
- **Stash** – stash, apply, pop and drop.
- **Patches** – create a patch from selected files, apply a patch file.
- **Diff and Blame** windows.
- **File actions** in the Changes tab and the Project window: Show Diff, Blame, Add to Git,
  Rollback, Create Patch.
- **Git status badges** in the Project window and the current branch in the main window title.

## Requirements

- Unity 6.0 LTS or newer.
- Git 2.31 or newer, installed separately.
- The Unity project inside a Git repository.
- Working Git authentication: if `git fetch` works in a terminal, it works in Vetka. Vetka never
  asks for passwords.

## Getting started

- Everything is under **Tools → Git**.
- Commit from the **Changes** tab: tick the files, type a message, click **Commit** or
  **Commit and Push**.
- Right-click files in the Changes tab or the Project window for Show Diff, Blame, Rollback and
  more.
- **Tools → Git → Settings...** sets the path to Git (found automatically), merge or rebase for
  Update Project, and display options.

Documentation: [upwake.dev/vetka](https://upwake.dev/vetka/). Release history: [CHANGELOG.md](CHANGELOG.md).

## License

Proprietary, all rights reserved – see [LICENSE.md](LICENSE.md). Copies obtained from the Unity
Asset Store are licensed under the Asset Store EULA.

Contact: [vetka@upwake.dev](mailto:vetka@upwake.dev)
