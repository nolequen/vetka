# Vetka 0.2.0 – Unity China Asset Store listing, English source

The English source of every field on the China portal. `listing-zh.md` is translated from this file.
The author shortened the Description and approved Summary and Description for translation on 2026-11-02.

## Title

Vetka – Git for Unity

## Summary

A full Git client inside the Tuanjie Editor: commit, update, branches, log, stash, diff and blame without
leaving the editor. Any operation that hits a conflict is rolled back completely.

## Description

A Git client inside the editor, in the style of JetBrains IDEs. Not only commits: update, branches, log,
stash, diff and blame without leaving the editor.

Update Project, merge, rebase and checkout either finish or roll back completely, with your local changes
restored. Vetka runs the Git installed on your machine, so it works with any hosting and the credentials you
already have.

- Changes tab: commit and push
- Update Project and Incoming Changes
- Branches: checkout, merge, rebase, compare
- Log of any branch: cherry-pick, revert, tags
- File history, diff and blame
- Stash and patches

The interface and the documentation are in English.

## Technical details

- Tuanjie 1.6 or newer (Unity 2022.3 or newer works too). Tested on Tuanjie 1.6 and 1.11, on Windows.
- Git 2.31 or newer, installed separately. Vetka does not include Git.
- Vetka does not handle authentication: it uses what Git already uses on your machine (Git Credential Manager
  or an SSH agent). If `git fetch` works in a terminal, it works in Vetka.
- Editor-only: nothing is added to your builds. Works with any render pipeline.
- No dependencies and no third-party content.
- Menus: Tools → Git, and Assets → Git in the Project window. Settings: Edit → Preferences → Vetka.
- Documentation: the README in Assets/Vetka and https://upwake.dev/vetka/. Support: vetka@upwake.dev.

## Release notes 0.2.0

First release in the Unity China Asset Store, with Tuanjie 1.6 support. New in 0.2.0: tags, force push with a
lease, update and push again after a rejected push, cherry-pick and revert, branch compare, file and folder
history, Incoming Changes. Settings moved to Edit → Preferences → Vetka, the log loads in portions, Git runs in
English whatever the system language.

## AI description

The code and the documentation were written with the help of an AI coding assistant (Claude) under the
author's direction and review. The images are rendered from the author's own HTML templates and editor
screenshots; there are no generated pictures. The setup video is recorded by scripts written with the same
assistant. The Chinese text is a translation of the author's English text made with AI.

## Keywords

git, Git 客户端 (Git client), 版本控制 (version control), 版本管理 (version management), 源代码管理 (source
control), 团结引擎 (Tuanjie), 提交 (commit), 分支 (branch), 合并 (merge), 冲突 (conflict), GitHub, GitLab, Gitee,
编辑器扩展 (editor extension), version control

## Reviewer note

Editor-only Git client, needs Git 2.31+ installed. Docs: README in Assets/Vetka and upwake.dev/vetka.
Support: vetka@upwake.dev. UI and docs in English.
