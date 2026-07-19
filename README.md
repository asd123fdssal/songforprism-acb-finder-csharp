# Shinycolors Song for Prism ACB Finder

## Readme

Use this program only for personal resource inspection and backup. Do not use it for commercial or illegal activities. The developer is not responsible for problems arising from its use. Extracted game resources remain the property of their respective copyright holders and must not be redistributed.

## Development environment

- OS: Windows 10 or later
- .NET 10
- C# / WPF

## How to use

1. Download the latest release ZIP file.
2. Extract the ZIP to a location with enough free space. This folder is used as the application's working directory.
3. Run `AcbFinder.App.exe`.
4. Click **Browse** and select the folder containing the game resources.

   The current default location is:

   ```text
   C:\Users\{USER_NAME}\AppData\LocalLow\BNE\imasscprism\D
   ```

5. Click **Decrypt**. The application scans the selected folder, copies supported CRI files, and decrypts supported encrypted variants. Your original game files are not modified.
6. The copied files are saved to:

   ```text
   Current_Directory/process/YYYYMMDD/origin
   ```

7. Click **Extract ACB**. Detected ACB files are moved to `Current_Directory/process/YYYYMMDD/origin/acb` and renamed using their embedded name data when available.
8. Click **Extract AWB**. Detected AWB files are moved to `Current_Directory/process/YYYYMMDD/origin/awb`, and their HCA tracks are extracted to `Current_Directory/process/YYYYMMDD/origin/hca`.
9. Convert the extracted `.hca` files to WAV with [foobar2000](https://www.foobar2000.org/) and the [vgmstream](https://vgmstream.org/) component. Using **Convert → Default** is recommended.
10. Save or move the converted WAV files to `Current_Directory/process/YYYYMMDD/origin/wav`.
11. Click **Categorize WAV** *(optional)* to sort WAV files into folders named after characters found in their filenames. Files without a matching character name are moved to `etc`.

## Build the executable

```bash
dotnet publish src/AcbFinder.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The executable is created at:

```text
src/AcbFinder.App/bin/Release/net10.0-windows/win-x64/publish/AcbFinder.App.exe
```
