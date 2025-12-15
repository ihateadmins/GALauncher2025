# Agendorks Launcher 2025

**Final Working Version - December 15, 2025**
C# .Net 4.7.2 and compatible with linux/wine!

This is a custom multi-login launcher for **Global Agenda** on the **Agendorks** community private server.

Global Agenda is a classic class-based MMO shooter originally developed by Hi-Rez Studios (released 2010, went free-to-play in 2011). Official servers have had intermittent availability, but the dedicated community keeps the game alive through the **Agendorks** private server.

This launcher automates launching and logging in multiple game clients simultaneously (multiboxing), arranges windows, sends login credentials, and detects successful logins to manage the process.

### Features

- **Multi-client support**: Launch up to 100 clients (default/recommended: 7) with different accounts.
- **Accounts database**: Use an `accounts.db` file (format: `username:password` per line) for automatic multi-account login, or single-account mode.
- **Automated login**: Sends username/password via simulated input and repeatedly presses Enter until login succeeds.
- **Login detection**: Monitors open file handles to detect when a client loads an in-game map (confirms successful login).
- **Window management**: Automatically resizes and tiles client windows in a grid.
- **Bot mode support**: Optional low FPS capping and engine.ini patching for reduced resource usage.
- **Discord Rich Presence**: Shows you're playing on Agendorks.
- **Steam API integration** (if Steam is running).
- **Hotkey stop**: Press **F8** to emergency stop all automation.
- **Admin elevation**: Automatically restarts as administrator if needed (required for handle monitoring).
- **Config saving**: Saves game path, args, credentials, and settings to `loginscriptconfig.ini`.
- **Detailed logging**: Real-time log window and `log.txt` file.

### Requirements

- Windows (tested on modern versions; compatible with .NET Framework 3.5).
- Global Agenda installed (Steam version recommended: Steam App ID 17020).
- Game executable: `GlobalAgenda.exe` (launcher can browse/search common Steam paths).
- Run as Administrator (launcher will prompt elevation if needed).
- For multi-account: Create `accounts.db` in the launcher folder with lines like:
  ```
  user1:pass1
  user2:pass2
  # comments allowed
  ```

### Setup & Usage

1. **Join the community**:
   - Discord: https://discord.gg/EeHwBpCPsK (main hub for server status, registration if required, updates, and help).

2. **Install Global Agenda**:
   - Via Steam (search for "Global Agenda" – it's free and occasionally re-listed).
   - Ensure the game connects to the Agendorks server (default args in launcher point to it).

3. **Place the launcher**:
   - Extract the compiled `GALauncher2025.exe` (and optional resources like `app.ico`, `menubg.jpg`) anywhere convenient.

4. **Run the launcher**:
   - Double-click `GALauncher2025.exe`.
   - It will prompt for admin rights if necessary.
   - Configure:
     - Browse to `GlobalAgenda.exe` if not auto-detected.
     - Enter username/password (single mode) or prepare `accounts.db` (multi mode).
     - Adjust client count, resolution, windowed/bot mode, etc.
     - Click **Save** to persist settings.

5. **Start**:
   - Click **Start**.
   - Clients will launch, windows arranged, logins sent, and automation runs until all are logged in (or stopped with F8).

6. **Stop**:
   - Click **Stop** or press **F8** at any time.

### Notes & Warnings

- **Multiboxing rules**: Follow Agendorks server rules – some activities may be restricted.
- **Do not set client amount below 7** without reason (as per built-in warning).
- **Bot mode**: Reduces FPS to 1 for background farming; use responsibly.
- **Single-account mode**: Other clients auto-kill after the first logs in, then sets full-screen res.
- Logs and handle dumps (for debugging) are saved in the launcher folder.
- This is a community tool – no official support from Hi-Rez.

Enjoy the game, Agents! See you in Dome City.

For issues or suggestions, ask in the Agendorks Discord.
