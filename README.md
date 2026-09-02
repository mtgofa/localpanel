<p align="center">
  <img src="src/Assets/logo.png" width="150" alt="LocalPanel logo"/>
</p>

<h1 align="center">LocalPanel</h1>

<p align="center">
  <b>A free, portable local development environment manager for Linux and Windows.</b><br/>
  Think Laravel Herd / Laragon — cross-platform, no subscription, no installer, one isolated folder.
</p>

---

## What is LocalPanel?

LocalPanel is a native, portable local development stack manager for **Linux** and **Windows** (.NET 8). It downloads, installs and manages your whole local web stack inside its own folder — copy it anywhere, delete it, and your system stays clean.

## Features

| Area | What you get |
|---|---|
| **Node.js (NVM)** | Install multiple LTS versions side by side (v26, v24, v22, v20, v18), instant switching (`nvm use 20`), and direct version runners (`node20`, `node22`, `npm20`, `npm22`) |
| **PHP** | Install multiple PHP versions (8.4, 8.3, 8.2, 8.1...) side by side, per-version php.ini, extensions enabled automatically, `php` / `php84` / `php83` commands in terminal |
| **Sites** | Park a folder → every project inside becomes `name.test` automatically (Laravel `public/` detected), multi-route proxy/PHP/static support |
| **SSL** | Trusted local certificates via mkcert — green lock in Chrome, Edge **and Firefox**, no warnings |
| **Databases** | MySQL, MariaDB, PostgreSQL and MongoDB — portable isolated data folders; phpMyAdmin at `localhost/phpmyadmin` |
| **Services** | Nginx and Redis — start/stop/restart, auto-start, clean shutdown on exit; Composer and SSL install and update silently |
| **Mail** | Built-in Mailpit inbox catches every email your sites send |
| **PM2** | PM2 process control from CLI / UI |
| **Isolated & Safe** | 100% portable in its own folder; does not interfere with system services or packages |
| **One-Command Update** | Self-updating CLI command or `./scripts/update.sh` |

---

# Getting Started

LocalPanel is built from source once, then runs entirely from its own folder.
The binaries it produces are **self-contained** — the .NET runtime is bundled inside them,
so .NET is only needed to *build*, never to *run*.

| | Linux | Windows |
|---|---|---|
| To build | .NET 8 SDK (`scripts/setup-dotnet.sh` installs it for you) | .NET 8 SDK |
| To run | nothing else | [Visual C++ Runtime 2015–2022](https://aka.ms/vs/17/release/vc_redist.x64.exe) — PHP, MySQL and Nginx need it |
| You get | `dist/localpanel` (CLI) + `dist/localpanel-gui` (desktop app) | `dist\LocalPanel.exe` (desktop app) |

---

## 🐧 Linux

### 1. Clone and build

```bash
git clone https://github.com/mtgofa/localpanel.git
cd localpanel

# One-time: installs an isolated .NET 8 SDK into ~/.dotnet.
# Skip this if `dotnet --version` already reports 8.x.
./scripts/setup-dotnet.sh

# Builds both binaries into dist/
./scripts/build.sh
```

### 2. Run it

```bash
./dist/localpanel-gui      # desktop window
./dist/localpanel status   # or drive it from the CLI
```

The first CLI run registers `localpanel` and the short alias `lpanel` in `bin/shims/`
(which it adds to your `PATH`). **Open a new terminal**, and both work from any directory:

```bash
lpanel status              # same as: localpanel status
lpanel start               # start the auto-start services
lpanel node install 22
lpanel node use 22
lpanel sites sync
```

Per-version runners land in `bin/shims/` too — `node20`, `node22`, `npm22`, `php84`, `nvm use 20`.

> **Tray icon:** the desktop app puts an icon in the system tray. On Linux this uses the
> DBus *StatusNotifierItem* spec, which GNOME only implements via the
> **AppIndicator** extension (KDE, Cinnamon, Budgie and most others have it built in).
> Without it the tray icon is simply absent — the window still works, and running
> `localpanel-gui` again brings the window back if you closed it.

---

## 🪟 Windows

### 1. Clone and build

```powershell
git clone https://github.com/mtgofa/localpanel.git
cd localpanel
powershell -ExecutionPolicy Bypass -File build.ps1
```

### 2. Run it

```powershell
.\dist\LocalPanel.exe
```

That single file is the whole app — copy it anywhere and it creates its portable folders
next to itself. Closing the window keeps LocalPanel in the tray; **Exit** from the tray menu
stops every running service first.

> `build.ps1` builds the desktop app only. The `localpanel` command-line tool is currently
> produced by the Linux build script.

---

## Updating

```bash
./scripts/update.sh        # git pull + rebuild
lpanel update              # or self-update from GitHub Releases
```

The desktop app can also update itself from **Settings → Check for Updates**.

---

## بالعربي 🇪🇬

**LocalPanel** بديل مجاني واحترافي لـ Herd وLaragon يعمل على **Linux و Windows**:
- **نظام Node / NVM مدمج:** تثبيت عدة إصدارات معاً (Node 18, 20, 22, 24)، والتبديل الفوري بينها، وأوامر مباشرة لكل إصدار (`node20`, `node22`).
- **PHP متعدد:** تشغيل عدة نسخ PHP في نفس الوقت مع Composer و phpMyAdmin.
- **مواقع ودومينات `.test` تلقائية:** شهادات SSL محلية موثوقة عبر mkcert.
- **أمان وعزل تام:** كل شيء يعمل داخل مجلد البرنامج دون المساس بالبرامج المثبتة على جهازك.
- **تحديث سريع بـ Command واحد:** عبر تشغيل `./scripts/update.sh` أو `lpanel update`.

### التشغيل السريع

**على لينكس:**
```bash
git clone https://github.com/mtgofa/localpanel.git && cd localpanel
./scripts/setup-dotnet.sh     # مرة واحدة: تثبيت .NET 8 داخل ~/.dotnet
./scripts/build.sh            # بناء الملفات في dist/
./dist/localpanel-gui         # الواجهة الرسومية
```
بعد أول تشغيل للـ CLI هيتسجّل أمرا `localpanel` و `lpanel` تلقائياً — افتح ترمينال جديد
وتقدر تكتب `lpanel status` من أي مكان.

**على ويندوز:**
```powershell
git clone https://github.com/mtgofa/localpanel.git; cd localpanel
powershell -ExecutionPolicy Bypass -File build.ps1
.\dist\LocalPanel.exe
```
يحتاج **Visual C++ Runtime 2015–2022** حتى تعمل PHP و MySQL و Nginx.

---

<p align="center">Made with ❤️ for local development on Linux & Windows</p>
