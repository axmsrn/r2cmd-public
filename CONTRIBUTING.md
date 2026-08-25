# Contributing to R2Cmd

Thanks for your interest! Here's how to help.

## 🎯 Ways to Contribute

- 🐛 **Report bugs** — [Open an issue](https://github.com/axmsrn/R2Cmd/issues/new)
- 💡 **Suggest features** — [Open an issue](https://github.com/axmsrn/R2Cmd/issues/new)
- 🔧 **Fix bugs / Add features** — Submit a Pull Request
- 📚 **Improve docs** — Fix typos, add examples
- ⭐ **Star the repo** — It helps more than you think

---

## 🐛 Reporting Bugs

Before reporting, please:

1. Search [existing issues](https://github.com/axmsrn/R2Cmd/issues) for duplicates
2. Update to the latest version

Include:

- Steps to reproduce
- Expected vs actual behavior
- Your Windows version and R2Cmd version
- Screenshots if applicable

---

## 💡 Suggesting Features

Open an issue describing:

- The problem your feature solves
- How it should work
- Who would benefit

Check the [Roadmap](README.md#-roadmap) first — your idea might already be planned.

---

## 🔧 Submitting Code

### Workflow

```bash
# 1. Fork and clone
git clone https://github.com/axmsrn/R2Cmd.git
cd R2Cmd

# 2. Create a branch
git checkout -b feature/your-feature-name   # or fix/issue-number

# 3. Make changes, test thoroughly

# 4. Commit and push
git push origin feature/your-feature-name

# 5. Open a Pull Request on GitHub
```

### Build

```powershell
dotnet build -c Debug          # Debug build
dotnet run                      # Run the app
```

### Checklist Before Submitting

- [ ] Code compiles without warnings
- [ ] Works in both light and dark themes
- [ ] Existing functionality not broken
- [ ] Clear PR description

---

## 📝 Code Style

Keep it simple and consistent:

- **Comments in English**
- **Naming**: `PascalCase` for methods, `camelCase` for variables, `_camelCase` for private fields
- **Async**: Use `async`/`await` for all I/O, never block the UI thread
- **Exceptions**: Handle specifically, provide user feedback, don't swallow silently
- **WPF**: Prefer data binding over code-behind, use commands for actions

---

## 📌 Commit Messages

Format: `<type>: <description>`

Types: `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `chore`

Examples:

```
feat: add multi-tab support
fix: prevent progress bar freeze on skip
docs: update keyboard shortcuts
```

Reference issues in the commit body: `Fixes #123`

---

## ⚖️ License

By contributing, you agree your work will be licensed under the [MIT License](LICENSE).

---

## ❓ Questions?

Open an issue with the `question` label or email: [axmsrn@gmail.com](mailto:axmsrn@gmail.com)

---

**Thanks for contributing! 🎉**
