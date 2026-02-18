# DupFree - Start Here

Welcome to the DupFree project — a Windows desktop application for finding duplicate files and visually similar images.

## Documentation Index

| Document | What It Covers |
|----------|---------------|
| [README.md](README.md) | Project overview, setup, and quick reference |
| [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) | Detailed feature list, tech stack, and project structure |
| [QUICKSTART.md](QUICKSTART.md) | Step-by-step guide for first-time users |
| [USAGE_GUIDE.md](USAGE_GUIDE.md) | Comprehensive usage instructions for all features |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Developer setup, NuGet dependencies, and contribution guide |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System architecture, data flow, and design decisions |

## Quick Overview

- **Language**: C# (.NET 8)
- **UI Framework**: WPF (Windows Presentation Foundation)
- **Version**: v1.2
- **Author**: Miguel Campos

## Core Capabilities

1. **Duplicate File Detection** — Finds files with matching names and sizes
2. **Similar Image Detection** — Perceptual hashing + GPU-accelerated SSIM comparison
3. **In-App Recycle Bin** — Delete with restore capability
4. **Persistent Settings** — Preferences saved between sessions
5. **Dark Theme UI** — Modern interface with sidebar navigation

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run
```

For detailed instructions, see [QUICKSTART.md](QUICKSTART.md).
