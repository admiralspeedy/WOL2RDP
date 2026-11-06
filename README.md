# WOL2RDP

A small Windows C# / WinForms helper: send a Wake-on-LAN magic packet, show an animated waiting indicator, check the destination in an RDP profile, then open that RDP profile with Windows Remote Desktop Connection.

## Download and run

1. Download the **WOL2RDP-win-x64** artifact from a successful run under this repository’s **Actions → Build WOL2RDP** page.
2. Extract the artifact into a folder on your Windows PC.
3. Edit `config.cfg` and place your `.rdp` file alongside `WOL2RDP.exe`.
4. Double-click `WOL2RDP.exe`.

The Windows build is **self-contained**: no .NET runtime installation is required on the destination computer. The app's EXE is **not code-signed** by GitHub Actions.

## Configuration

The repository includes **`config.example.cfg`** with blank network/MAC fields. The real **`config.cfg` is ignored by Git**, so you can put your own private values in it without accidentally committing them. The GitHub Actions build copies the example to `config.cfg` in the downloadable artifact.

Create a `config.cfg` beside the executable (or rename a copy of `config.example.cfg`) containing:

```ini
wolDestination=
macAddress=
rdpFile=connection.rdp
```

- `wolDestination`: IPv4 destination for the WoL magic packet, usually your LAN's broadcast address.
- `macAddress`: target MAC in `AA:BB:CC:DD:EE:FF` format (colons or dashes).
- `rdpFile`: name of the `.rdp` file located beside the executable.

The helper reads the RDP host and optional port from the RDP file. It uses UDP port 9 for WoL, waits up to 120 seconds for TCP connectivity, then launches the RDP profile unmodified via `mstsc.exe`. The file may be a signed RDP profile; changing its contents after signing can invalidate its signature. The helper neither handles nor stores RDP passwords.

## Standard .NET build (no batch file)

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows and run, in the repository directory:

```powershell
dotnet build WOL2RDP.csproj --configuration Release
```

To produce the same portable build used by GitHub Actions:

```powershell
dotnet publish WOL2RDP.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true --output publish
Copy-Item config.example.cfg publish/config.cfg
```

Copy your signed `connection.rdp` to the `publish` directory; edit the `publish/config.cfg` values. The `config.cfg` and `.rdp` are ignored by Git.

## Notes

- Windows-only GUI app (C#, .NET 8, WinForms).
- Build is performed on a GitHub-hosted Windows runner.
- The application can be built using the standard .NET SDK or the included GitHub Actions workflow.
- Keep RDP on your trusted local network.
