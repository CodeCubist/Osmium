# Contributing to Osmium

Thank you for your interest in contributing to Osmium! We welcome all kinds of contributions, including bug reports, feature requests, documentation improvements, translations, and code changes. Please take a moment to read these guidelines before you start.

## Code of Conduct

This project and everyone participating in it is governed by the [Contributor Covenant Code of Conduct](../CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code. Please report unacceptable behavior to the project maintainers.

## Ground Rules

- Be respectful and considerate when communicating with other contributors and maintainers.
- Follow the [Code of Conduct](../CODE_OF_CONDUCT.md).
- Create an issue for any major change or new feature before submitting a pull request. Discuss the change transparently and get community feedback first.
- Keep changes focused and as small as possible. One logical change per pull request.
- Do not submit generated files, build artifacts, or large third-party binaries.
- Respect the project's license (MIT). By contributing, you agree that your contributions will be licensed under the same license.

## What We Are Looking For

We welcome contributions in many forms:

- Bug reports and feature requests via [GitHub Issues](https://github.com/OsmiumRemote/Osmium/issues).
- Documentation improvements.
- Translations of the user interface and documentation.
- Code fixes, performance optimizations, and new features.
- Help triaging and reproducing issues across different GPUs, displays, and network environments.

## What We Are Not Looking For

- Do not use the issue tracker for general support questions. Please check the README first.
- Do not submit pull requests that are unrelated to the project's purpose.
- Do not submit changes that bypass authentication, authorization, or security controls.

## Getting Started

### Prerequisites

- Windows 10 (19041+) or Windows 11 (x64).
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.
- Visual Studio 2022 (or later) with the **Desktop development with C++** and **.NET desktop development** workloads.
- The [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- FFmpeg n9.0 shared runtime DLLs (`avcodec-63.dll`, `avutil-61.dll`, `swscale-10.dll`, `swresample-7.dll`) placed in `native\deps\ffmpeg\bin\`.

### Building the Project

1. Clone the repository:
   ```bash
   git clone https://github.com/OsmiumRemote/Osmium.git
   ```
2. Build the native C++ engine (`OsmiumEngine`, including `SDL3` and `opus`):
   ```powershell
   msbuild native\osmium-engine\OsmiumEngine.vcxproj /m /p:Configuration=Release /p:Platform=x64
   ```
3. Open `Osmium.sln` in Visual Studio / Rider, or build from the command line:
   ```powershell
   dotnet build src\Osmium.App\Osmium.App.csproj -c Release -p:Platform=x64
   ```

## How to Report a Bug

If you find a security vulnerability, do **NOT** open a public issue. Please see [SECURITY.md](../SECURITY.md) for instructions on how to report it privately.

For all other bugs, open an issue using the **Bug Report** template. Please include:

- A clear description of the problem.
- Detailed steps to reproduce the issue.
- The Osmium version, role (host/controller), and Windows version.
- Relevant logs (`%LocalAppData%\Osmium\logs`) and screenshots, if available.

## How to Suggest a Feature or Enhancement

Open an issue using the **Suggestions & Feature Requests** template. Please describe:

- The problem or limitation you are facing.
- Your proposed solution.
- The expected benefit of the change.
- Any alternatives you have considered.

## Submitting a Pull Request

1. Fork the repository and create a new branch for your change.
2. Make your changes, following the existing code style.
3. Build and test your changes locally.
4. Write clear, descriptive commit messages.
5. Submit a pull request and describe what you changed and why.
6. Reference any related issues in the pull request description.

## Code Review Process

- Maintainers review pull requests as time permits.
- A pull request may be closed if it does not receive a response to review feedback within two weeks.
- The maintainers may request changes before merging. Please keep the branch up to date with the latest `master` branch.

## Style Guide

- Follow the existing code style and the conventions in [.editorconfig](../.editorconfig).
- Write commit messages in a clear, descriptive style (for example, `fix: ...`, `feat: ...`, `docs: ...`).
- Use the [Conventional Commits](https://www.conventionalcommits.org/) format for commit messages where practical.

## Community

- Discussions and issue tracking happen on [GitHub](https://github.com/OsmiumRemote/Osmium).
- Maintainer contact: [Telegram](https://t.me/codecubist).
