# Vetka – Git for Unity

A Git client inside the Unity Editor, modelled on the version control integration of JetBrains IDEs
(IntelliJ IDEA, Rider). It drives the regular `git` command line, so it works with any repository
and hosting you already use.

- [Package README](Packages/dev.upwake.vetka/README.md) – features, requirements and getting started.
- [Website](https://upwake.dev/vetka/).
- [Changelog](Packages/dev.upwake.vetka/CHANGELOG.md).

## Installation

Vetka is a UPM package, `dev.upwake.vetka`. Install it in one of two ways:

- **Package Manager → + → Add package from git URL…** with
  `https://github.com/nolequen/vetka.git?path=/Packages/dev.upwake.vetka`
  (requires access to this repository);
- or copy the `Packages/dev.upwake.vetka` folder into the `Packages` folder of your project.

## Repository layout

This repository is itself a Unity project used to develop and test the plugin:

- `Packages/dev.upwake.vetka` – the package: `Editor` holds the plugin.
- `Assets/Tests` – the plugin's tests, kept outside the package so they are never shipped to users.
- `Assets/DevTools` – development-only tooling, not part of the package.

The tests are EditMode tests: run them from *Window → General → Test Runner*. They create temporary
repositories in the system temp folder.

## License

Proprietary, all rights reserved – see [LICENSE.md](LICENSE.md). Copies obtained from the Unity
Asset Store are licensed under the Asset Store EULA.
