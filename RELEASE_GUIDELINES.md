# BMSMonitor Release Guidelines

Panduan ini menjelaskan file yang wajib dibuat saat release agar fitur update
aplikasi berjalan normal, terutama silent update yang mengunduh ZIP lalu
me-restart BMSMonitor.

## Asset Wajib Di GitHub Release

Setiap release GitHub wajib memiliki dua asset berikut:

1. `BMSMonitorSetup-vX.Y.Z.exe`
   - Installer penuh untuk instalasi manual atau fresh install.
   - Dibuat dari `installer.iss` menggunakan Inno Setup.

2. `BMSMonitorUpdate-vX.Y.Z.zip`
   - Paket update untuk fitur update otomatis di aplikasi.
   - Nama file harus berakhiran `.zip`.
   - Nama file sebaiknya mengandung kata `Update`, karena updater akan
     memprioritaskan asset ZIP dengan nama tersebut.

Contoh untuk versi `2.0.2`:

```text
BMSMonitorSetup-v2.0.2.exe
BMSMonitorUpdate-v2.0.2.zip
```

## Format ZIP Update

ZIP update harus berisi isi folder publish secara langsung di root ZIP.
Jangan bungkus file publish di dalam folder tambahan.

Benar:

```text
BMSMonitorUpdate-v2.0.2.zip
+-- BMSMonitor.exe
+-- BMSMonitor.dll
+-- BMSMonitor.deps.json
+-- BMSMonitor.runtimeconfig.json
+-- resources.pri
+-- Microsoft.ui.xaml.dll
+-- WinRT.Runtime.dll
+-- Assets/
+-- ...file dan folder publish lainnya
```

Salah:

```text
BMSMonitorUpdate-v2.0.2.zip
+-- AppFiles/
    +-- BMSMonitor.exe
    +-- ...
```

Salah:

```text
BMSMonitorUpdate-v2.0.2.zip
+-- BMSMonitor-v2.0.2/
    +-- BMSMonitor.exe
    +-- ...
```

Minimal, ZIP harus memiliki `BMSMonitor.exe` di root atau di folder payload
yang dapat ditemukan updater. Format root tetap wajib dipakai untuk menjaga
release konsisten dan mudah diverifikasi.

## Cara Kerja Updater

Updater membaca release terbaru dari:

```text
https://api.github.com/repos/Khlfnalvr/BMSMonitor/releases/latest
```

Lalu updater:

1. Membandingkan versi aplikasi saat ini dengan `tag_name` release GitHub.
2. Mencari asset ZIP, dengan prioritas nama yang mengandung `Update`.
3. Mengunduh ZIP ke folder temp.
4. Mengekstrak ZIP dan memastikan payload berisi `BMSMonitor.exe`.
5. Menjalankan helper PowerShell dengan privilege admin.
6. Menunggu proses BMSMonitor lama keluar.
7. Menyalin file payload ke folder instalasi.
8. Menjalankan ulang `BMSMonitor.exe`.

Karena itu, release tanpa ZIP update yang benar akan membuat tombol
`Download & Apply` gagal atau tidak dapat melakukan silent update.

## Checklist Sebelum Release

Update versi di file berikut:

```text
BMSMonitor.csproj
installer.iss
```

Pastikan nilai versinya sama:

```xml
<Version>X.Y.Z</Version>
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
<InformationalVersion>X.Y.Z</InformationalVersion>
```

Di `installer.iss`:

```text
#define AppVersion   "X.Y.Z"
OutputBaseFilename=BMSMonitorSetup-vX.Y.Z
VersionInfoVersion=X.Y.Z.0
```

## Build Release

Bersihkan output publish lama:

```powershell
Remove-Item -LiteralPath .\Publish\AppFiles -Recurse -Force -ErrorAction SilentlyContinue
```

Publish aplikasi:

```powershell
dotnet publish .\BMSMonitor.csproj -c Release -r win-x64 --self-contained true -o .\Publish\AppFiles
```

Buat ZIP update:

```powershell
Compress-Archive -Path .\Publish\AppFiles\* -DestinationPath .\Publish\BMSMonitorUpdate-vX.Y.Z.zip -Force
```

Buat installer:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer.iss
```

Hasil yang harus ada:

```text
Publish\BMSMonitorSetup-vX.Y.Z.exe
Publish\BMSMonitorUpdate-vX.Y.Z.zip
```

## Verifikasi ZIP

Pastikan ZIP berisi `BMSMonitor.exe` dan file publish utama:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::OpenRead(".\Publish\BMSMonitorUpdate-vX.Y.Z.zip").Entries |
    Where-Object {
        $_.FullName -eq "BMSMonitor.exe" -or
        $_.FullName -eq "BMSMonitor.dll" -or
        $_.FullName -eq "BMSMonitor.runtimeconfig.json"
    } |
    Select-Object FullName, Length
```

Output minimal harus menampilkan:

```text
BMSMonitor.exe
BMSMonitor.dll
BMSMonitor.runtimeconfig.json
```

## Buat GitHub Release

Tag release harus memakai format:

```text
vX.Y.Z
```

Upload kedua asset:

```powershell
gh release create vX.Y.Z `
  ".\Publish\BMSMonitorSetup-vX.Y.Z.exe#BMSMonitorSetup-vX.Y.Z.exe" `
  ".\Publish\BMSMonitorUpdate-vX.Y.Z.zip#BMSMonitorUpdate-vX.Y.Z.zip" `
  --repo Khlfnalvr/BMSMonitor `
  --target master `
  --title "vX.Y.Z - Release title" `
  --notes "Ringkasan perubahan release."
```

Verifikasi asset release:

```powershell
gh release view vX.Y.Z --repo Khlfnalvr/BMSMonitor --json tagName,name,url,assets,publishedAt
```

## Checklist Akhir

Sebelum mengumumkan release, pastikan:

- `BMSMonitor.csproj` sudah memakai versi baru.
- `installer.iss` sudah memakai versi baru.
- GitHub release memakai tag `vX.Y.Z`.
- Release memiliki asset installer `.exe`.
- Release memiliki asset update `.zip`.
- Nama ZIP update mengandung `Update`.
- ZIP update berisi `BMSMonitor.exe` di root.
- ZIP update dibuat dari output `Publish\AppFiles` versi terbaru.
- Commit versi dan fix sudah di-push ke branch release, biasanya `master`.

Jika salah satu item di atas tidak terpenuhi, fitur update aplikasi berisiko
tidak mendeteksi release, salah memilih asset, gagal mengekstrak payload, atau
gagal menjalankan silent update.
