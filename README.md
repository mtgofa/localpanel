<p align="center">
  <img src="src/Assets/logo.png" width="150" alt="LocalPanel logo"/>
</p>

<h1 align="center">LocalPanel</h1>

<p align="center">
  <b>A free, portable local development environment manager for Windows.</b><br/>
  Think Laravel Herd / Laragon — no subscription, no installer, one folder.
</p>

---

## What is LocalPanel?

LocalPanel is a native Windows app (WPF / .NET 8) that downloads, installs and manages your whole local web stack from one dark, Herd-style window. Everything lives inside the app's own folder — copy it anywhere, delete it, and your system stays clean.

## Who is it for?

- **Laravel / PHP developers** who want several PHP versions side by side, sites on `https://myapp.test`, Composer and phpMyAdmin working out of the box.
- **Node developers** who want nvm-style version switching and PM2 process control without installing anything globally.
- **Anyone leaving Herd or Laragon** who wants the same workflow — parked project folders, automatic domains, trusted SSL — for free.

## Features

| Area | What you get |
|---|---|
| **PHP** | Install any version (8.5 → 7.3) side by side, per-version php.ini settings, phpredis + common extensions enabled automatically, `php` / `php84` / `php83` commands in any terminal |
| **Node** | LTS versions side by side, nvm-style global switching (`node` / `npm` / `npx` follow the selected version instantly) |
| **Sites** | Park a folder → every project inside becomes `name.test` automatically (Laravel `public/` detected), aliases per site, reverse proxy to a port or unix socket with WebSocket support |
| **SSL** | Trusted local certificates via mkcert — green lock in Chrome, Edge **and Firefox**, no warnings |
| **Services** | Nginx, MySQL 8.4, Redis, phpMyAdmin, Composer — one-click install, start/stop/restart, auto-start, clean shutdown on exit |
| **PM2** | See every PM2 process, stop / restart / delete / read logs from the UI |
| **Hosts file** | Managed automatically in a clearly-marked block, one line per site |
| **No UAC** | Runs without admin rights — a single one-time permission grant for the hosts file, then never again |
| **Self-update** | Optional update check against GitHub Releases at startup |

## Quick start

1. Download `LocalPanel.exe` and put it in its own folder (e.g. `C:\LocalPanel`).
2. Run it — no installer, no admin prompt.
3. Open **Services** and install Nginx, MySQL, mkcert (and whatever else you need).
4. Open **Settings → Parked folders** and add your projects folder.
5. That's it — every project is now live at `https://project-name.test`.

> **Requirement:** Microsoft Visual C++ Runtime 2015-2022 (the app checks and offers the download if missing).

## Building from source

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Produces `dist\LocalPanel.exe` — a single self-contained file, no .NET needed on the target machine.

---

## بالعربي 🇪🇬

**LocalPanel** بديل مجاني لـ Herd وLaragon على ويندوز — برنامج واحد portable بينزّل ويدير كل بيئة التطوير بتاعتك: نسخ PHP جنب بعض، Node بنظام nvm، مواقع `.test` تلقائية بـ SSL موثوق، MySQL وRedis وphpMyAdmin وComposer وPM2 — كله من واجهة واحدة، من غير صلاحيات admin، وأي فولدر مشاريع بتحطه بيتحول لمواقع شغالة لوحده.

---

<p align="center">Made with ❤️ for local development on Windows</p>
