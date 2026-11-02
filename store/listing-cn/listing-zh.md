# Vetka 0.2.0 – Unity China Asset Store listing, Simplified Chinese

Translated from `source-en.md` and checked by a separate reviewer for meaning, terms and tone. Each section gives
the text for the portal field, then a Russian back-translation for the author's check. Git terms and the names of
Vetka's windows and buttons stay in English, since the plugin UI is in English; editor menus also give the
Chinese editor's names in brackets.

## Title

Vetka – Git for Unity

> Обратный перевод: Vetka – Git для Unity (название не переводится).

## Summary

团结引擎编辑器内功能完整的 Git 客户端：提交、拉取更新、分支、日志、stash、diff 和 blame，无需离开编辑器。任何操作一旦遇到冲突，都会完整回滚。

> Обратный перевод: Полнофункциональный Git-клиент внутри редактора Tuanjie: коммит, получение обновлений,
> ветки, лог, stash, diff и blame, не выходя из редактора. Любая операция, как только встречает конфликт,
> полностью откатывается.

## Description

编辑器内的 Git 客户端，风格与 JetBrains IDE 一致。不只是提交：拉取更新、分支、日志、stash、diff 和 blame，都在编辑器里完成。

Update Project、merge、rebase 和 checkout 要么完整执行，要么完全回滚，你的本地修改都会被恢复。Vetka 调用你电脑上已安装的 Git，因此适用于任何代码托管平台，并沿用你现有的凭据。

- Changes 标签页：commit 和 push
- Update Project 和 Incoming Changes
- 分支：checkout、merge、rebase、比较
- 任意分支的日志：cherry-pick、revert、标签
- 文件历史、diff 和 blame
- Stash 和补丁

界面和文档均为英文。

> Обратный перевод: Git-клиент внутри редактора, в стиле JetBrains IDE. Не только коммиты: получение
> обновлений, ветки, лог, stash, diff и blame – всё делается в редакторе.
>
> Update Project, merge, rebase и checkout либо выполняются полностью, либо полностью откатываются, ваши
> локальные изменения в любом случае восстанавливаются. Vetka вызывает Git, установленный на вашем компьютере,
> поэтому подходит для любой платформы хостинга кода и использует ваши существующие учётные данные.
>
> – Вкладка Changes: commit и push
> – Update Project и Incoming Changes
> – Ветки: checkout, merge, rebase, сравнение
> – Лог любой ветки: cherry-pick, revert, теги
> – История файла, diff и blame
> – Stash и патчи
>
> Интерфейс и документация на английском.

## Technical details

- 团结引擎 1.6 或更高版本（也支持 Unity 2022.3 或更高版本）。已在 Windows 平台的团结引擎 1.6 和 1.11 上测试通过。
- 需要另行安装 Git 2.31 或更高版本，Vetka 不内置 Git。
- Vetka 不处理身份验证，而是使用你电脑上 Git 已有的身份验证方式（Git Credential Manager 或 SSH agent）。只要 `git fetch` 能在终端中正常运行，在 Vetka 中同样可用。
- 仅限编辑器使用：不会被打包进构建，兼容任何渲染管线。
- 无依赖项，不含第三方内容。
- 菜单：Tools → Git，以及 Project（项目）窗口中的 Assets（资源）→ Git。设置：Edit（编辑）→ Preferences（首选项）→ Vetka。
- 文档：Assets/Vetka 中的 README 以及 https://upwake.dev/vetka/ 。技术支持：vetka@upwake.dev。

> Обратный перевод:
> – Tuanjie 1.6 или новее (также поддерживается Unity 2022.3 или новее). Проверено и работает в Tuanjie 1.6 и
>   1.11 на Windows.
> – Нужен отдельно установленный Git 2.31 или новее, Vetka не содержит встроенного Git.
> – Vetka не занимается аутентификацией, а использует способ аутентификации, который уже есть у Git на вашем
>   компьютере (Git Credential Manager или SSH agent). Если `git fetch` нормально работает в терминале, в Vetka
>   он тоже работает.
> – Только для редактора: не попадает в сборку, совместим с любым конвейером рендеринга.
> – Нет зависимостей, нет стороннего содержимого.
> – Меню: Tools → Git и Assets (Ресурсы) → Git в окне Project (Проект). Настройки: Edit (Правка) → Preferences
>   (Настройки) → Vetka.
> – Документация: README в Assets/Vetka и https://upwake.dev/vetka/. Техподдержка: vetka@upwake.dev.

## Release notes 0.2.0

首次在 Unity 中国资源商店发布，支持团结引擎 1.6。0.2.0 新增：标签、force push（--force-with-lease）、push 被拒绝后先更新再重新 push、cherry-pick 和 revert、分支比较、文件和文件夹历史、Incoming Changes。设置移至 Edit → Preferences → Vetka，日志分批加载，Git 输出始终为英文，不受系统语言影响。

> Обратный перевод: Первый выпуск в Unity China Asset Store, с поддержкой Tuanjie 1.6. Новое в 0.2.0: теги,
> force push (--force-with-lease), после отклонённого push – сначала обновить, потом снова push, cherry-pick и
> revert, сравнение веток, история файлов и папок, Incoming Changes.
> Настройки перенесены в Edit → Preferences → Vetka, лог загружается порциями, вывод Git всегда на английском
> независимо от языка системы.

## AI description

代码和文档在作者的指导和审核下，借助 AI 编程助手（Claude）编写。图片由作者自己的 HTML 模板和编辑器截图渲染而成，没有 AI 生成的图片。安装演示视频由同一助手编写的脚本录制。中文文本由 AI 根据作者的英文原文翻译。

> Обратный перевод: Код и документация написаны с помощью AI-ассистента для программирования (Claude) под
> руководством и с проверкой автора. Изображения отрисованы из собственных HTML-шаблонов автора и скриншотов
> редактора, сгенерированных AI изображений нет. Демонстрационное видео установки записано скриптами, написанными
> с помощью того же ассистента. Китайский текст переведён AI с английского оригинала автора.

## Keywords

git, Git 客户端, 版本控制, 版本管理, 源代码管理, 团结引擎, 提交, 分支, 合并, 冲突, GitHub, GitLab, Gitee, 编辑器扩展, version control

> Обратный перевод: git, Git-клиент, контроль версий, управление версиями, управление исходным кодом, Tuanjie,
> коммит, ветка, слияние, конфликт, GitHub, GitLab, Gitee (китайский хостинг), расширение редактора, version
> control (15 слов).

## Reviewer note

仅在编辑器中运行的 Git 客户端，需预先安装 Git 2.31+。文档：Assets/Vetka 中的 README 及 upwake.dev/vetka。技术支持：vetka@upwake.dev。界面和文档均为英文。

> Обратный перевод: Git-клиент, работающий только в редакторе, нужен заранее установленный Git 2.31+.
> Документация: README в Assets/Vetka и upwake.dev/vetka. Техподдержка: vetka@upwake.dev. Интерфейс и
> документация на английском.
