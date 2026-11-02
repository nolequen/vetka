# Vetka setup video – subtitles

One block per scene, in the order of the video (about two minutes in all). The editor runs in English, so menu
and button names stay in English in both languages, as they appear on screen. Each scene has one or two lines;
a line stays on screen for the whole step it describes. The author approved the English lines on 2026-11-02;
the Chinese lines (`zh:`) are what the video shows.

## 1. import (15 s)

- Vetka is a Git client inside the editor. Import the package: Assets → Import Package → Custom Package...
  zh: Vetka 是编辑器内的 Git 客户端。导入插件包：Assets → Import Package → Custom Package...
- Keep every file ticked and click Import.
  zh: 保持所有文件勾选，点击 Import。

## 2. settings (10 s)

- Everything is under Tools → Git. Settings are in Edit → Preferences → Vetka.
  zh: 所有功能都在 Tools → Git 菜单下，设置位于 Edit → Preferences → Vetka。
- Vetka finds Git by itself. Git 2.31 or newer must be installed.
  zh: Vetka 会自动检测 Git，需预先安装 Git 2.31 或更高版本。

## 3. commit (20 s)

- The Changes tab lists changed and new files. Moves and renames show as one row.
  zh: Changes 标签页列出已修改和新增的文件，移动或重命名的文件合并为一行显示。
- Tick the files, write a message and click Commit.
  zh: 勾选文件，填写提交信息，点击 Commit。

## 4. update (20 s)

- Incoming Changes shows what teammates pushed, before you take it.
  zh: Incoming Changes 会在拉取之前显示队友推送了哪些内容。
- Update Project fetches and merges it. If anything conflicts, everything is rolled back.
  zh: Update Project 获取并合并这些更改。一旦出现冲突，所有操作都会回滚。

## 5. push (10 s)

- Push sends your commits to the remote. Vetka uses the credentials Git already has.
  zh: Push 将你的提交推送到远程仓库，Vetka 沿用 Git 已有的凭据。

## 6. branches (20 s)

- Branches: create a branch, then switch back to main. Your local changes come along.
  zh: 在 Branches 中新建分支，再切换回 main，本地修改会一起带过去。
- Merge another branch into the current one from its menu.
  zh: 在另一个分支的菜单中，将它合并到当前分支。

## 7. log (10 s)

- The Log shows the commits of any branch and the files of each commit.
  zh: Log 显示任意分支的提交，以及每次提交改动的文件。

## 8. diff (8 s)

- Show Diff compares a file with its last committed version.
  zh: Show Diff 将文件与上次提交的版本对比。

## 9. blame (8 s)

- Blame shows who changed each line and in which commit.
  zh: Blame 显示每一行由谁、在哪次提交中修改。

## 10. end (5 s)

- Vetka – Git for Unity. Questions and feedback: vetka@upwake.dev
  zh: Vetka – Git for Unity。问题与反馈：vetka@upwake.dev
